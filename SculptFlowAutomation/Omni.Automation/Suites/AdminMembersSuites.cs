using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Members admin: list, details, lock, password, membership, email confirmed, invites.</summary>
[Suite(83)]
public static class AdminMembersSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-members", "Admin: members",
            "Client and member users through the members API. Each test that changes a user works on its own private client.")
        .Test("list-get", "Members list and details show a client's owner; unknown and admin ids are 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var items = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array("items");
            var owner = items.FirstOrDefault(m => m?["email"]?.ToString() == client.Email)
                ?? throw new TestFailure("The owner isn't in the client's members list.");
            Expect.EqualText("client", owner["role"], "Owner role");
            Expect.True(owner["membershipActive"]?.GetValue<bool>() == true, "Owner membership isn't active.");
            AdminHelpers.NoSecretProperties(items, "The members list");

            var byEmail = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members?search={Uri.EscapeDataString(client.Email)}", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.True(byEmail.Any(m => m?["email"]?.ToString() == client.Email), "Searching members by email doesn't find the owner.");

            var userId = owner["userId"]!.ToString();
            var detail = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members/{userId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText(client.ClientId.ToString(), detail.Prop("clientId"), "Member's client");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Unknown member");

            var anyAdmin = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/admins", ct: ctx.Ct)).ExpectOk().Array().First()!["id"]!.ToString();
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members/{anyAdmin}", ct: ctx.Ct)).Status, "An admin's id on the members API");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("lock", "Locking a user ends their open session and refuses sign-in; unlocking lets them back", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-lock");
            var userId = await AdminHelpers.OwnerIdAsync(ctx, client);
            try
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/locked", new { value = true }, ctx.Ct)).ExpectOk();
                Expect.True(await AdminHelpers.WithinAsync(ctx, async () => !await AdminHelpers.SignedInAsync(ctx, client.Http)), "The locked user's open session still works.");
                Expect.True(!await AdminHelpers.CanSignInAsync(ctx, client.Email, client.Password), "A locked user can still sign in.");
                var row = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members/{userId}", ct: ctx.Ct)).ExpectOk();
                Expect.True(row.Prop("loginLocked").GetValue<bool>(), "Member details don't show the lock.");

                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/locked", new { value = false }, ctx.Ct)).ExpectOk();
                Expect.True(await AdminHelpers.CanSignInAsync(ctx, client.Email, client.Password), "The user can't sign in after being unlocked.");
            }
            finally
            {
                await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/locked", new { value = false }, CancellationToken.None);
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("password", "Setting a member password: weak refused; a valid one ends the old session and replaces the old password", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-pw");
            var userId = await AdminHelpers.OwnerIdAsync(ctx, client);
            foreach (var weak in new[] { "", "abc", "abcdef" })
            {
                Expect.Equal(400, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/password", new { password = weak }, ctx.Ct)).Status, $"Member password \"{weak}\"");
            }
            Expect.True(await AdminHelpers.SignedInAsync(ctx, client.Http), "A refused password change ended the session.");

            var password = AdminHelpers.NewPassword();
            (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/password", new { password }, ctx.Ct)).ExpectOk();
            Expect.True(await AdminHelpers.WithinAsync(ctx, async () => !await AdminHelpers.SignedInAsync(ctx, client.Http)), "The open session survived a password change.");
            Expect.True(await AdminHelpers.CanSignInAsync(ctx, client.Email, password), "Can't sign in with the new password.");
            Expect.True(!await AdminHelpers.CanSignInAsync(ctx, client.Email, client.Password), "The old password still works.");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("membership", "Turning a membership off ends the session; turning it back on lets the user in", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-member");
            var userId = await AdminHelpers.OwnerIdAsync(ctx, client);
            try
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/membership-active", new { active = false }, ctx.Ct)).ExpectOk();
                Expect.True(await AdminHelpers.WithinAsync(ctx, async () => !await AdminHelpers.SignedInAsync(ctx, client.Http)), "The session survived turning the membership off.");
                Expect.True((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members/{userId}", ct: ctx.Ct)).ExpectOk().Prop("membershipActive").GetValue<bool>() == false,
                    "Member details still show the membership active.");
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/membership-active", new { active = true }, ctx.Ct)).ExpectOk();
                Expect.True(await AdminHelpers.CanSignInAsync(ctx, client.Email, client.Password), "Can't sign in after the membership was turned back on.");
            }
            finally
            {
                await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/membership-active", new { active = true }, CancellationToken.None);
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("email-confirmed", "The email-confirmed flag can be switched off and on", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-email");
            var userId = await AdminHelpers.OwnerIdAsync(ctx, client);
            foreach (var value in new[] { false, true })
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{userId}/email-confirmed", new { value }, ctx.Ct)).ExpectOk();
                var row = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/members/{userId}", ct: ctx.Ct)).ExpectOk();
                Expect.Equal(value, row.Prop("emailConfirmed").GetValue<bool>(), "emailConfirmed after the change");
            }
            Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/members/{Guid.NewGuid()}/email-confirmed", new { value = true }, ctx.Ct)).Status, "Unknown member");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("invites", "Open invites are listed and can be cancelled", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-invite");
            var ownerId = await AdminHelpers.OwnerIdAsync(ctx, client);
            var inviteId = Guid.NewGuid();
            // Email isn't configured on staging, so the invite token is inserted the way the QA pass did (hash of a random token).
            var hash = Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(32))).ToLowerInvariant();
            await ctx.Db.ExecuteForClientAsync(client.ClientId, """
                insert into identity.account_tokens (id, purpose, user_id, client_id, email, full_name, token_hash, created_by_user_id, expires_at, created_at)
                values (@id, 'invite', null, @clientId, @email, 'Automation Invitee', @hash, @owner, now() + interval '7 days', now())
                """, new { id = inviteId, email = $"auto-{ctx.Tag}-invitee@{TestContext.EmailDomain}", hash, owner = ownerId }, ctx.Ct);

            var path = $"{AdminHelpers.Api}/members/invites";
            Expect.ContainsId((await ctx.Admin.GetAsync($"{path}?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array(), inviteId, "Open invites");
            (await ctx.Admin.DeleteAsync($"{path}/{inviteId}", ctx.Ct)).ExpectOk();
            Expect.LacksId((await ctx.Admin.GetAsync($"{path}?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array(), inviteId, "Open invites after cancelling");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database)
        .Build();
}
