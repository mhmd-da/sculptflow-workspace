using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Clients: list/search/filters, details, edit validation, activation, default language, business type, inbox settings.</summary>
[Suite(82)]
public static class AdminClientsSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-clients", "Admin: clients",
            "The clients API behind the portal's Clients pages. Writes only on private automation clients.")
        .Test("search", "Search finds a client by name; odd search text and paging are tolerated", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var found = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?search={Uri.EscapeDataString(client.Name)}", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId(found.Array("items"), client.ClientId, "Search by the client's full name");
            var partial = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?search={Uri.EscapeDataString("main " + ctx.Tag)}", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId(partial.Array("items"), client.ClientId, "Search by part of the name");
            foreach (var q in new[] { "%", "_", "عربي", "😀" })
            {
                (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?search={Uri.EscapeDataString(q)}", ct: ctx.Ct)).ExpectOk().Array("items");
            }
            foreach (var page in new[] { "-5", "0", "999" })
            {
                var r = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?page={page}", ct: ctx.Ct)).ExpectOk();
                r.Array("items");
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("active-filter", "The active filter includes an active client and excludes it with active=false", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var q = Uri.EscapeDataString(client.Name);
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?search={q}&active=true", ct: ctx.Ct)).ExpectOk().Array("items"), client.ClientId, "active=true");
            Expect.LacksId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?search={q}&active=false", ct: ctx.Ct)).ExpectOk().Array("items"), client.ClientId, "active=false");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("options-detail-counts", "Options, details and counts describe the client", async ctx =>
        {
            var client = await ctx.ClientAsync();
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/options", ct: ctx.Ct)).ExpectOk().Array(), client.ClientId, "Client options");
            var detail = await AdminHelpers.ClientDetailAsync(ctx, client.ClientId);
            Expect.EqualText(client.Name, detail["name"], "Client name");
            Expect.EqualText("general", detail["businessTypeCode"], "Business type");
            Expect.True(detail["isActive"]?.GetValue<bool>() == true, "A new client isn't active.");
            var counts = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/counts", ct: ctx.Ct)).ExpectOk();
            Expect.True(counts.Prop("members").GetValue<int>() >= 1, "Counts show no members for a client with an owner.");
            Expect.True(counts.Prop("services").GetValue<int>() >= 1, "Counts show no services although the run created one.");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Details of an unknown client");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("edit", "Editing a client saves every field (Arabic and emoji included)", async ctx =>
        {
            var client = await ctx.NamedClientAsync("adm-edit");
            try
            {
                var body = AdminHelpers.ClientBody(client);
                body["name"] = client.Name + " عربي 😀";
                body["operatingHours"] = "Lun-Ven 9h-17h";
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}", body, ctx.Ct)).ExpectOk();
                var detail = await AdminHelpers.ClientDetailAsync(ctx, client.ClientId);
                Expect.EqualText(client.Name + " عربي 😀", detail["name"], "Saved name");
                Expect.EqualText("Asia/Beirut", detail["timezone"], "Saved timezone");
                Expect.EqualText("LB", detail["countryCode"], "Saved country");
                Expect.EqualText("Lun-Ven 9h-17h", detail["operatingHours"], "Saved operating hours");
            }
            finally { await AdminHelpers.RestoreClientAsync(ctx, client); }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("edit-validation", "Editing refuses an empty name and an unknown timezone; an empty timezone becomes UTC", async ctx =>
        {
            var client = await ctx.NamedClientAsync("adm-edit");
            try
            {
                var body = AdminHelpers.ClientBody(client);
                body["name"] = "  ";
                Expect.Equal(400, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}", body, ctx.Ct)).Status, "Saving an empty name");

                body = AdminHelpers.ClientBody(client);
                body["timezone"] = "Mars/Olympus";
                var bad = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}", body, ctx.Ct);
                Expect.Equal(400, bad.Status, "Saving timezone Mars/Olympus");
                Expect.True(bad.Body.Contains("Mars/Olympus"), $"The timezone error doesn't name the bad value.{bad.Snippet(200)}");

                body["timezone"] = "";
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}", body, ctx.Ct)).ExpectOk();
                Expect.EqualText("UTC", (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["timezone"], "Timezone after saving it empty");

                Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{Guid.NewGuid()}", AdminHelpers.ClientBody(client), ctx.Ct)).Status, "Editing an unknown client");
            }
            finally { await AdminHelpers.RestoreClientAsync(ctx, client); }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("edit-too-long", "Over-long name, phone, email or website give 400, not 500", async ctx =>
        {
            var client = await ctx.NamedClientAsync("adm-edit");
            try
            {
                var problems = new List<string>();
                foreach (var (field, value) in new[]
                         {
                             ("name", client.Name + new string('x', 600)), ("phone", "+" + new string('1', 60)),
                             ("email", new string('a', 200) + "@" + TestContext.EmailDomain), ("website", "https://example.com/" + new string('p', 300)),
                         })
                {
                    var body = AdminHelpers.ClientBody(client);
                    body[field] = value;
                    var r = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}", body, ctx.Ct);
                    if (r.Status != 400) problems.Add($"{field} of {value.Length} chars -> {r.Status}");
                }
                Expect.True(problems.Count == 0, $"Over-long client fields aren't refused with a 400: {string.Join("; ", problems)}.");
            }
            finally { await AdminHelpers.RestoreClientAsync(ctx, client); }
        }, needs: Needs.Session | Needs.AdminKey, finding: "t6-admin-01")
        .Test("edit-bad-email", "Editing refuses an invalid email (400)", async ctx =>
            await ClientsHelpers.ExpectFieldRefusedAsync(ctx, "email", "not-an-email"), needs: Needs.Session | Needs.AdminKey, finding: "t6-admin-02")
        .Test("edit-bad-country", "Editing refuses a 3-letter country code (400)", async ctx =>
            await ClientsHelpers.ExpectFieldRefusedAsync(ctx, "countryCode", "ZZZ"), needs: Needs.Session | Needs.AdminKey, finding: "t6-admin-02")
        .Test("edit-bad-website", "Editing refuses a javascript: website (400)", async ctx =>
            await ClientsHelpers.ExpectFieldRefusedAsync(ctx, "website", "javascript:alert(1)"), needs: Needs.Session | Needs.AdminKey, finding: "t6-admin-02")
        .Test("default-language", "Default language accepts en/ar/fr and refuses anything else", async ctx =>
        {
            var client = await ctx.NamedClientAsync("adm-edit");
            try
            {
                foreach (var lang in new[] { "ar", "fr" })
                {
                    (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/default-language", new { language = lang }, ctx.Ct)).ExpectOk();
                    Expect.EqualText(lang, (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["defaultLanguage"], "Default language");
                }
                Expect.Equal(400, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/default-language", new { language = "xx" }, ctx.Ct)).Status, "Default language xx");
                Expect.EqualText("fr", (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["defaultLanguage"], "Default language after a refused change");
            }
            finally
            {
                await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/default-language", new { language = "en" }, CancellationToken.None);
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("activate-toggle", "Deactivating and reactivating a client shows in its details and the list filter", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-toggle");
            try
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/active", new { active = false }, ctx.Ct)).ExpectOk();
                Expect.True((await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["isActive"]?.GetValue<bool>() == false, "Client still active after deactivation.");
                Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients?search={Uri.EscapeDataString(client.Name)}&active=false", ct: ctx.Ct)).ExpectOk().Array("items"),
                    client.ClientId, "active=false filter after deactivation");
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/active", new { active = true }, ctx.Ct)).ExpectOk();
                Expect.True((await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["isActive"]?.GetValue<bool>() == true, "Client not active after reactivation.");
            }
            finally
            {
                await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/active", new { active = true }, CancellationToken.None);
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("deactivate-locks-out", "Deactivating a client locks out its users and API", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-suspend");
            Expect.True(await AdminHelpers.SignedInAsync(ctx, client.Http), "The owner's session didn't work before the test.");
            try
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/active", new { active = false }, ctx.Ct)).ExpectOk();
                var problems = new List<string>();
                if (!await AdminHelpers.WithinAsync(ctx, async () => !await AdminHelpers.SignedInAsync(ctx, client.Http)))
                    problems.Add("the owner's open session still loads the dashboard");
                var api = await client.Http.GetAsync("/api/services", ct: ctx.Ct);
                if (api.IsSuccess) problems.Add($"the owner's API calls still work (GET /api/services -> {api.Status})");
                if (await AdminHelpers.CanSignInAsync(ctx, client.Email, client.Password)) problems.Add("a fresh sign-in still succeeds");
                Expect.True(problems.Count == 0, $"The client is deactivated but nothing changed for its users: {string.Join("; ", problems)}.");
            }
            finally
            {
                await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/active", new { active = true }, CancellationToken.None);
            }
        }, needs: Needs.Session | Needs.AdminKey, finding: "t6-admin-04")
        .Test("business-type", "Business type change: preview, move to clinic and back, unknown type refused", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-btype");
            var preview = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type/preview?to=clinic", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("general", preview.Prop("fromCode"), "Preview from");
            Expect.EqualText("clinic", preview.Prop("toCode"), "Preview to");
            Expect.EqualText("general", (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["businessTypeCode"], "Type after a preview (must not change)");

            var unknown = await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type/preview?to=nope_type", ct: ctx.Ct);
            Expect.True(unknown.Status is 400 or 404, $"Preview to an unknown type answered {unknown.Status}.{unknown.Snippet(200)}");

            (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type", new { businessTypeCode = "clinic" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("clinic", (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["businessTypeCode"], "Type after the move");
            (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type", new { businessTypeCode = "general" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("general", (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))["businessTypeCode"], "Type after moving back");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("inbox-settings", "Inbox settings list a connected account and save its AI switches", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-inbox");
            var channel = await AdminHelpers.FakeTelegramAsync(ctx, client);
            var path = $"{AdminHelpers.Api}/clients/{client.ClientId}/inbox-settings";

            var settings = (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk();
            Expect.True(settings.Prop("planIncludesAiBot").GetValue<bool>() == false, "A trial client is shown with the AI bot included.");
            Expect.ContainsId(settings.Array("accounts"), channel.IntegrationId, "Inbox settings accounts", "channelIntegrationId");

            (await ctx.Admin.PutAsync($"{path}/{channel.IntegrationId}", new { aiEnabled = false, botId = (Guid?)null, aiBotNewConversations = false, aiBotReopenedConversations = true }, ctx.Ct)).ExpectOk();
            var account = (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk().Array("accounts")
                .First(a => a?["channelIntegrationId"]?.ToString() == channel.IntegrationId.ToString())!;
            Expect.True(account["aiBotNewConversations"]?.GetValue<bool>() == false, "aiBotNewConversations wasn't saved as false.");
            Expect.True(account["aiBotReopenedConversations"]?.GetValue<bool>() == true, "aiBotReopenedConversations wasn't saved as true.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database)
        .Test("inbox-settings-refused", "Inbox settings refuse AI on a plan without it, an unknown account or bot, and an unknown client", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-inbox2");
            var channel = await AdminHelpers.FakeTelegramAsync(ctx, client);
            var path = $"{AdminHelpers.Api}/clients/{client.ClientId}/inbox-settings";
            var on = await ctx.Admin.PutAsync($"{path}/{channel.IntegrationId}", new { aiEnabled = true, botId = (Guid?)null, aiBotNewConversations = true, aiBotReopenedConversations = true }, ctx.Ct);
            Expect.True(on.Status is 400 or 422, $"Turning AI on for a trial client without a bot answered {on.Status}.{on.Snippet(200)}");
            Expect.Equal(404, (await ctx.Admin.PutAsync($"{path}/{Guid.NewGuid()}", new { aiEnabled = false, botId = (Guid?)null, aiBotNewConversations = true, aiBotReopenedConversations = true }, ctx.Ct)).Status,
                "Saving AI settings of an unknown account");
            Expect.Equal(400, (await ctx.Admin.PutAsync($"{path}/{channel.IntegrationId}", new { aiEnabled = false, botId = Guid.NewGuid(), aiBotNewConversations = true, aiBotReopenedConversations = true }, ctx.Ct)).Status,
                "Saving AI settings with an unknown bot");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{Guid.NewGuid()}/inbox-settings", ct: ctx.Ct)).Status, "Inbox settings of an unknown client");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database)
        .Build();
}

/// <summary>Helpers for the client edit tests.</summary>
internal static class ClientsHelpers
{
    /// <summary>Saves one bad field on the private edit client and expects 400; restores the client either way.</summary>
    public static async Task ExpectFieldRefusedAsync(TestContext ctx, string field, string value)
    {
        var client = await ctx.NamedClientAsync("adm-edit");
        try
        {
            var body = AdminHelpers.ClientBody(client);
            body[field] = value;
            var r = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}", body, ctx.Ct);
            var saved = (await AdminHelpers.ClientDetailAsync(ctx, client.ClientId))[field]?.ToString();
            Expect.True(r.Status == 400,
                $"Client edit accepted {field} \"{value}\" (status {r.Status}, now saved as \"{saved}\") instead of refusing it with 400.");
        }
        finally { await AdminHelpers.RestoreClientAsync(ctx, client); }
    }
}
