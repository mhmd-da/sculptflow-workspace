using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Platform-admin API key enforcement and the admin sign-in API the Omni.Admin portal uses.</summary>
[Suite(80)]
public static class AdminAuthSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-auth", "Admin: key and sign-in",
            "Every /api/platform-admin route needs X-Platform-Admin-Key; the portal's sign-in API accepts only active admins with one generic refusal.")
        .Test("no-key", "Admin routes refuse a caller without the key (401)", async ctx =>
        {
            var anon = ctx.NewHttp();
            foreach (var path in new[] { "/clients", "/admins", "/settings", "/cache/keys", "/logs/summary", "/business-types", "/overview/totals", "/automation/clients" })
            {
                var r = await anon.GetAsync(AdminHelpers.Api + path, ct: ctx.Ct);
                Expect.Equal(401, r.Status, $"GET {AdminHelpers.Api}{path} without the key");
            }
        }, needs: Needs.None)
        .Test("wrong-key", "A wrong, padded or query-string key is refused (401)", async ctx =>
        {
            var key = ctx.Env.PlatformAdminKey ?? throw new TestSkipped("No platform-admin key configured.");
            foreach (var bad in new[] { "wrong-key", key + "x", key[..^1] })
            {
                var http = ctx.NewHttp(new Dictionary<string, string> { ["X-Platform-Admin-Key"] = bad });
                Expect.Equal(401, (await http.GetAsync($"{AdminHelpers.Api}/clients", ct: ctx.Ct)).Status, "GET clients with a wrong/padded key");
            }
            var anon = ctx.NewHttp();
            foreach (var name in new[] { "key", "apiKey", "X-Platform-Admin-Key" })
            {
                var r = await anon.GetAsync($"{AdminHelpers.Api}/clients?{name}={Uri.EscapeDataString(key)}", ct: ctx.Ct);
                Expect.Equal(401, r.Status, $"GET clients with the key in the query string as \"{name}\"");
            }
        }, needs: Needs.AdminKey)
        .Test("key-before-body", "A request without the key gets 401 even when its body is invalid", async ctx =>
        {
            var anon = ctx.NewHttp();
            var r = await anon.PostRawAsync($"{AdminHelpers.Api}/auth/login", "{\"email\":1}", "application/json", ct: ctx.Ct);
            Expect.True(r.Status == 401,
                $"POST auth/login with an invalid body and no key answered {r.Status} instead of 401: the body is validated before the key is checked, so an outsider sees internal type names.{r.Snippet(300)}");
        }, needs: Needs.None, finding: "t6-admin-08")
        .Test("login-wrong", "Wrong password and unknown email get the same generic 401", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "auth");
            try
            {
                var wrong = (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, "Wrong-password-1A")).ExpectStatus(401);
                var unknown = (await AdminHelpers.AdminLoginAsync(ctx, $"nobody-{ctx.Tag}@{TestContext.EmailDomain}", "Wrong-password-1A")).ExpectStatus(401);
                var empty = (await ctx.Admin.PostAsync($"{AdminHelpers.Api}/auth/login", new { }, ct: ctx.Ct)).ExpectStatus(401);
                Expect.Equal(wrong.Str("error"), unknown.Str("error"), "Unknown-email refusal vs wrong-password refusal");
                Expect.Equal(wrong.Str("error"), empty.Str("error"), "Empty sign-in refusal vs wrong-password refusal");
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("login-ok", "An active admin signs in and is confirmed by auth/admins/{id}", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "auth");
            try
            {
                var login = (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectOk();
                Expect.Equal(admin.Id, login.Str("id"), "Signed-in admin id");
                Expect.Equal(admin.Email, login.Str("email"), "Signed-in admin email");
                AdminHelpers.NoSecretProperties(login.Json, "The sign-in answer");
                var check = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/auth/admins/{admin.Id}", ct: ctx.Ct)).ExpectOk();
                Expect.Equal(admin.Id, check.Str("id"), "auth/admins/{id} answer");
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("client-refused", "A client owner's correct credentials are refused by the admin sign-in (401)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            (await AdminHelpers.AdminLoginAsync(ctx, client.Email, client.Password)).ExpectStatus(401);
            var ownerId = await AdminHelpers.OwnerIdAsync(ctx, client);
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/auth/admins/{ownerId}", ct: ctx.Ct)).Status,
                "auth/admins/{id} for a client owner's user id");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("member-refused", "A member user's correct credentials are refused by the admin sign-in (401)", async ctx =>
        {
            // The trial plan has one seat; a member needs a plan with two.
            var client = await ctx.NamedClientAsync("adm-member-login");
            await ctx.SetPlanAsync(client, "inbox");
            var (email, password) = await AdminHelpers.NewMemberAsync(ctx, client, "member");
            Expect.True(await AdminHelpers.CanSignInAsync(ctx, email, password), "The new member can't sign in to the client app.");
            (await AdminHelpers.AdminLoginAsync(ctx, email, password)).ExpectStatus(401);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("deactivated-refused", "A deactivated admin can't sign in and is no longer confirmed", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "deact");
            try
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/active", new { active = false, actingAdminId = (string?)null }, ctx.Ct)).ExpectOk();
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectStatus(401);
                Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/auth/admins/{admin.Id}", ct: ctx.Ct)).Status,
                    "auth/admins/{id} for a deactivated admin");
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("lockout", "Five wrong passwords lock the admin; a password reset by another admin unlocks it", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "lock");
            try
            {
                for (var i = 0; i < 5; i++) (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, $"Wrong-{i}-password1A")).ExpectStatus(401);
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectStatus(401);
                var row = await AdminHelpers.FindAdminAsync(ctx, admin.Email);
                Expect.True(row?["loginLocked"]?.GetValue<bool>() == true, "The admins list doesn't show the admin as locked after 5 wrong passwords.");

                var password = AdminHelpers.NewPassword();
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/password", new { password }, ctx.Ct)).ExpectOk();
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, password)).ExpectOk();
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("password-stamp", "Changing an admin's password changes what auth/admins/{id} answers, so old portal sessions end", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "stamp");
            try
            {
                var login = (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectOk().RequireJson().AsObject();
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/password", new { password = AdminHelpers.NewPassword() }, ctx.Ct)).ExpectOk();
                var after = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/auth/admins/{admin.Id}", ct: ctx.Ct)).ExpectOk().RequireJson().AsObject();

                static string Session(JsonObject o) => string.Join("|", o.Where(p => p.Key is not ("id" or "fullName" or "email")).Select(p => $"{p.Key}={p.Value}"));
                var before = Session(login);
                Expect.True(before.Length > 0 && before != Session(after),
                    "The admin identity has no security stamp (or it doesn't change with the password): after another admin sets a new password, " +
                    "auth/admins/{id} answers the same as at sign-in, so the portal keeps sessions opened with the old password.");
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey, finding: "t6-admin-13")
        .Build();
}

/// <summary>Admin users: list, create (validation), self-deactivation, password rules.</summary>
[Suite(81)]
public static class AdminAdminsSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-admins", "Admin: admin users",
            "Admin users in identity.users (role admin): list, create, deactivate, password. Only automation admins are touched.")
        .Test("list", "Admins list answers rows without secrets", async ctx =>
        {
            var list = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/admins", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(list.Count > 0, "The admins list is empty.");
            Expect.True(list.Any(a => a?["isActive"]?.GetValue<bool>() == true), "No active admin in the list.");
            AdminHelpers.NoSecretProperties(list, "The admins list");
        }, needs: Needs.AdminKey)
        .Test("create-validation", "Creating an admin refuses a bad email, short or weak password, blank name and a taken email (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var email = $"auto-{ctx.Tag}-admin-bad@{TestContext.EmailDomain}";
            var cases = new (string What, object Body)[]
            {
                ("bad email", new { email = "not-an-email", fullName = "[Automation] X", password = AdminHelpers.NewPassword() }),
                ("9-char password", new { email, fullName = "[Automation] X", password = "Abcdef12!" }),
                ("blank name", new { email, fullName = "  ", password = AdminHelpers.NewPassword() }),
                ("weak 12-char password", new { email, fullName = "[Automation] X", password = "abcdefghijkl" }),
                ("a client owner's email", new { email = client.Email, fullName = "[Automation] X", password = AdminHelpers.NewPassword() }),
            };
            var accepted = new List<string>();
            foreach (var (what, body) in cases)
            {
                var r = await ctx.Admin.PostAsync($"{AdminHelpers.Api}/admins", body, ct: ctx.Ct);
                if (r.Status == 201)
                {
                    accepted.Add(what);
                    await AdminHelpers.DeactivateAdminAsync(ctx, r.Str("id"));
                }
                else Expect.Equal(400, r.Status, $"Creating an admin with {what}");
            }
            Expect.True(accepted.Count == 0, $"Admin create accepted: {string.Join(", ", accepted)}.");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("create", "A new admin is created active, listed, and can sign in", async ctx =>
        {
            // One fresh admin per run (the only one that isn't re-used); deactivated at the end.
            var email = $"auto-{ctx.Tag}-admin-new@{TestContext.EmailDomain}";
            var password = AdminHelpers.NewPassword();
            var created = (await ctx.Admin.PostAsync($"{AdminHelpers.Api}/admins", new { email, fullName = $"{TestContext.ClientNamePrefix} Admin {ctx.Tag}", password }, ct: ctx.Ct)).ExpectStatus(201);
            var id = created.Str("id");
            try
            {
                Expect.True(created.Prop("isActive").GetValue<bool>(), "A new admin isn't active.");
                Expect.True((await AdminHelpers.FindAdminAsync(ctx, email)) is not null, "The new admin isn't in the admins list.");
                (await AdminHelpers.AdminLoginAsync(ctx, email, password)).ExpectOk();
                Expect.Equal(400, (await ctx.Admin.PostAsync($"{AdminHelpers.Api}/admins", new { email, fullName = "[Automation] Dup", password }, ct: ctx.Ct)).Status,
                    "Creating a second admin with the same email");
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, id); }
        }, needs: Needs.AdminKey)
        .Test("self-deactivate", "An admin can't deactivate their own account (400)", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "self");
            try
            {
                var r = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/active", new { active = false, actingAdminId = admin.Id }, ctx.Ct);
                Expect.Equal(400, r.Status, "Deactivating yourself");
                var row = await AdminHelpers.FindAdminAsync(ctx, admin.Email);
                Expect.True(row?["isActive"]?.GetValue<bool>() == true, "The admin was deactivated even though the request was refused.");
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("deactivate-reactivate", "Deactivating and reactivating an admin shows in the list", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "toggle");
            try
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/active", new { active = false, actingAdminId = (string?)null }, ctx.Ct)).ExpectOk();
                Expect.True((await AdminHelpers.FindAdminAsync(ctx, admin.Email))?["isActive"]?.GetValue<bool>() == false, "Deactivated admin still listed active.");
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/active", new { active = true, actingAdminId = (string?)null }, ctx.Ct)).ExpectOk();
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectOk();
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("password", "Setting an admin password: too short refused; a valid one replaces the old", async ctx =>
        {
            var admin = await AdminHelpers.SlotAdminAsync(ctx, "pw");
            try
            {
                Expect.Equal(400, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/password", new { password = "Short1A" }, ctx.Ct)).Status, "A 7-character password");
                Expect.Equal(400, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/password", new { password = "" }, ctx.Ct)).Status, "An empty password");
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectOk();

                var password = AdminHelpers.NewPassword();
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{admin.Id}/password", new { password }, ctx.Ct)).ExpectOk();
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, password)).ExpectOk();
                (await AdminHelpers.AdminLoginAsync(ctx, admin.Email, admin.Password)).ExpectStatus(401);
            }
            finally { await AdminHelpers.DeactivateAdminAsync(ctx, admin.Id); }
        }, needs: Needs.AdminKey)
        .Test("not-admin", "Admin writes on an unknown id or a client user's id answer 404", async ctx =>
        {
            var ownerId = await AdminHelpers.OwnerIdAsync(ctx, await ctx.ClientAsync());
            foreach (var id in new[] { Guid.NewGuid().ToString(), ownerId })
            {
                Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{id}/active", new { active = false, actingAdminId = (string?)null }, ctx.Ct)).Status,
                    "Deactivating a non-admin as admin");
                Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/admins/{id}/password", new { password = AdminHelpers.NewPassword() }, ctx.Ct)).Status,
                    "Setting a non-admin's password through the admins API");
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Build();
}
