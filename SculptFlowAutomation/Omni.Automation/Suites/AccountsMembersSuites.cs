using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Members of a test client: invites are inserted in the database (staging can't send email) and accepted
/// through the real AcceptInvite page, the way QA added members.</summary>
internal static partial class AccountsHelpers
{
    public sealed record Member(string Email, string Password, string FullName, string UserId, OmniClient Http);

    /// <summary>The run's shared member of the full-plan "team" client (for read-only member checks).</summary>
    public static Task<Member> SharedMemberAsync(TestContext ctx) =>
        PlatformHelpers.Once(ctx, "shared-member", async () => await NewMemberAsync(ctx, await PlatformHelpers.TeamAsync(ctx), "member"));

    /// <summary>A new member of <paramref name="client"/> (needs a free seat), signed in by accepting the invite.</summary>
    public static async Task<Member> NewMemberAsync(TestContext ctx, TestClient client, string label, string? fullName = null)
    {
        var email = SignupHelpers.NewEmail(ctx, label);
        var password = "Auto-Member-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        fullName ??= $"Automation {label}";
        var (_, token) = await PlatformHelpers.InsertInviteAsync(ctx, client, email, fullName);
        var http = ctx.NewHttp();
        var accepted = await AcceptAsync(ctx, http, token, fullName, password, password);
        if (accepted.Status != 302)
        {
            throw new TestFailure($"Accepting the invite for {email} failed (status {accepted.Status}): {PlatformHelpers.Alert(accepted)}");
        }
        var userId = (await MemberRowAsync(ctx, client, email))["userId"]!.ToString();
        ctx.Log($"Member {email} joined {client.Name}.");
        return new Member(email, password, fullName, userId, http);
    }

    public static Task<ApiResponse> AcceptAsync(TestContext ctx, OmniClient http, string token, string fullName, string password, string confirm) =>
        http.SubmitFormAsync(InvitePath(token), new Dictionary<string, string>
        {
            ["Token"] = token,
            ["FullName"] = fullName,
            ["Password"] = password,
            ["ConfirmPassword"] = confirm,
        }, ct: ctx.Ct);

    public static string InvitePath(string token) => "/Account/AcceptInvite?token=" + Uri.EscapeDataString(token);

    /// <summary>The member row (GET /api/members as the owner) for an email.</summary>
    public static async Task<System.Text.Json.Nodes.JsonNode> MemberRowAsync(TestContext ctx, TestClient client, string email)
    {
        var members = (await client.Http.GetAsync("/api/members", ct: ctx.Ct)).ExpectOk().Array();
        return members.FirstOrDefault(m => string.Equals(m?["email"]?.ToString(), email, StringComparison.OrdinalIgnoreCase))
               ?? throw new TestFailure($"{email} is not in {client.Name}'s member list.");
    }

    /// <summary>True when the (HTML-decoded) page contains the text: messages with quotes or apostrophes are encoded.</summary>
    public static bool Shows(ApiResponse page, string text) =>
        WebUtility.HtmlDecode(page.Body).Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The form action of the first "remove special date" button on the availability tab.</summary>
    public static string? DeleteExceptionAction(string html) =>
        DeleteExceptionRegex().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    [GeneratedRegex("action=\"([^\"]*handler=DeleteException[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteExceptionRegex();
}

/// <summary>Members and invites: the seat limit, invite validation, accepting an invite (DB token), dead links,
/// cancelling and resending, the owner's own account.</summary>
[Suite(10)]
public static class MembersSuite
{
    public static TestSuite Build() => new SuiteBuilder("members", "Members and invites", "Invites (DB tokens), acceptance, seats, cancel/resend and bad ids.")
        .Test("list", "The owner is listed as the active account owner", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var row = await AccountsHelpers.MemberRowAsync(ctx, client, client.Email);
            Expect.EqualText("client", row["role"], "Owner's role");
            Expect.EqualText("true", row["isActive"], "Owner is active");
        })
        .Test("trial-seat-limit", "On the trial plan (1 seat) the owner can't invite anyone (422)", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var response = await api.PostAsync("/api/members/invites", new { email = SignupHelpers.NewEmail(ctx, "seat"), fullName = "Seat test" }, ct: ctx.Ct);
            Expect.Equal(422, response.Status, $"Invite on the trial plan{response.Snippet(200)}");
            Expect.True(response.Json?["error"]?.ToString().Contains("limit", StringComparison.OrdinalIgnoreCase) == true, $"The refusal doesn't mention the plan limit.{response.Snippet(200)}");
        })
        .Test("invite-validation", "An invalid email or one that already has an account can't be invited", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var other = await ctx.OtherClientAsync();
            var invalid = await api.PostAsync("/api/members/invites", new { email = "not-an-email" }, ct: ctx.Ct);
            Expect.Equal(400, invalid.Status, "Invite 'not-an-email'");
            var taken = await api.PostAsync("/api/members/invites", new { email = other.Email.ToUpperInvariant() }, ct: ctx.Ct);
            Expect.Equal(400, taken.Status, "Invite an email that signs in to another client");
            Expect.True(taken.Json?["error"]?.ToString().Contains("already signs in", StringComparison.OrdinalIgnoreCase) == true, $"Unexpected refusal.{taken.Snippet(200)}");
        })
        .Test("invite-duplicate", "An email with an invite already waiting can't be invited twice", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var email = SignupHelpers.NewEmail(ctx, "dup");
            await PlatformHelpers.InsertInviteAsync(ctx, client, email);
            var response = await client.Http.PostAsync("/api/members/invites", new { email = email.ToUpperInvariant() }, ct: ctx.Ct);
            Expect.Equal(400, response.Status, "Second invite for the same email");
            Expect.True(response.Json?["error"]?.ToString().Contains("already has an invite", StringComparison.OrdinalIgnoreCase) == true, $"Unexpected refusal.{response.Snippet(200)}");
        }, needs: Needs.Session | Needs.Database)
        .Test("accept-validation", "The invite page shows the client and email, and refuses an empty name or mismatched passwords", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var email = SignupHelpers.NewEmail(ctx, "landing");
            var (_, token) = await PlatformHelpers.InsertInviteAsync(ctx, client, email, "Landing Person");
            var landing = (await ctx.Anonymous.GetAsync(AccountsHelpers.InvitePath(token), ct: ctx.Ct)).ExpectOk();
            Expect.True(landing.Body.Contains(email, StringComparison.OrdinalIgnoreCase), "The invite page doesn't show the invited email.");
            Expect.True(AccountsHelpers.Shows(landing, client.Name), "The invite page doesn't name the client.");

            var noName = await AccountsHelpers.AcceptAsync(ctx, ctx.NewHttp(), token, "", "Auto-Join-2026x", "Auto-Join-2026x");
            Expect.True(noName.Status == 200 && AccountsHelpers.Shows(noName, "Enter your full name"), $"Empty name answered {noName.Status}: \"{PlatformHelpers.Alert(noName)}\".");
            var mismatch = await AccountsHelpers.AcceptAsync(ctx, ctx.NewHttp(), token, "Landing Person", "Auto-Join-2026x", "Auto-Join-2026y");
            Expect.True(mismatch.Status == 200 && AccountsHelpers.Shows(mismatch, "don't match"), $"Mismatched passwords answered {mismatch.Status}: \"{PlatformHelpers.Alert(mismatch)}\".");
        }, needs: Needs.Database)
        .Test("accept-over-seats", "Accepting an invite when the plan has no free seat is refused and creates no user", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx); // trial: the owner holds the only seat
            var email = SignupHelpers.NewEmail(ctx, "noseat");
            var (_, token) = await PlatformHelpers.InsertInviteAsync(ctx, client, email);
            var response = await AccountsHelpers.AcceptAsync(ctx, ctx.NewHttp(), token, "No Seat", "Auto-Join-2026x", "Auto-Join-2026x");
            Expect.Equal(200, response.Status, "Accepting with no free seat");
            Expect.True(AccountsHelpers.Shows(response, "limit"), $"The refusal doesn't mention the seat limit: \"{PlatformHelpers.Alert(response)}\".");
            var users = await ctx.Db.ScalarAsync<long>("select count(*) from identity.users where lower(email) = lower(@email)", new { email }, ctx.Ct);
            Expect.Equal(0L, users, "Users created by the refused acceptance");
        }, needs: Needs.Database)
        .Test("accept", "Accepting an invite creates a member who lands signed in; the link then stops working", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var email = SignupHelpers.NewEmail(ctx, "join");
            var (_, token) = await PlatformHelpers.InsertInviteAsync(ctx, team, email, "Joining Person");
            var http = ctx.NewHttp();
            var weak = await AccountsHelpers.AcceptAsync(ctx, http, token, "Joining Person", "abcdef", "abcdef");
            Expect.True(weak.Status == 200 && PlatformHelpers.HasPasswordField(weak.Body) && PlatformHelpers.Alert(weak).Length > 0,
                $"A weak password answered {weak.Status}: \"{PlatformHelpers.Alert(weak)}\".");

            var ok = await AccountsHelpers.AcceptAsync(ctx, http, token, "Joining Person", "Auto-Join-2026x", "Auto-Join-2026x");
            Expect.Equal(302, ok.Status, $"Accepting the invite ({PlatformHelpers.Alert(ok)})");
            Expect.Equal("/dashboard", ok.Location, "Where accepting goes");
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, http), "The new member's session");
            var row = await AccountsHelpers.MemberRowAsync(ctx, team, email);
            Expect.EqualText("member", row["role"], "New user's role");
            Expect.EqualText("true", row["isActive"], "New member is active");

            var reuse = (await ctx.Anonymous.GetAsync(AccountsHelpers.InvitePath(token), ct: ctx.Ct)).ExpectOk();
            Expect.True(!PlatformHelpers.HasPasswordField(reuse.Body) && reuse.Body.Contains("no longer works", StringComparison.OrdinalIgnoreCase), "A used invite link still shows the form.");
            var again = await AccountsHelpers.AcceptAsync(ctx, ctx.NewHttp(), token, "Hijacker", "Auto-Join-2026z", "Auto-Join-2026z");
            Expect.True(again.Status == 200 && !PlatformHelpers.HasPasswordField(again.Body), $"Reusing the invite answered {again.Status} {again.Location}.");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Test("dead-links", "Expired, cancelled and made-up invite links say they no longer work", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var (_, expired) = await PlatformHelpers.InsertInviteAsync(ctx, client, SignupHelpers.NewEmail(ctx, "expired"), minutes: -5);
            var (cancelledId, cancelled) = await PlatformHelpers.InsertInviteAsync(ctx, client, SignupHelpers.NewEmail(ctx, "cancelled"));
            (await client.Http.DeleteAsync($"/api/members/invites/{cancelledId}", ctx.Ct)).ExpectStatus(204);
            foreach (var (label, token) in new[] { ("expired", expired), ("cancelled", cancelled), ("made-up", PlatformHelpers.NewToken()) })
            {
                var page = (await ctx.Anonymous.GetAsync(AccountsHelpers.InvitePath(token), ct: ctx.Ct)).ExpectOk();
                Expect.True(!PlatformHelpers.HasPasswordField(page.Body) && page.Body.Contains("no longer works", StringComparison.OrdinalIgnoreCase),
                    $"The {label} invite link still shows the form.");
            }
        }, needs: Needs.Session | Needs.Database)
        .Test("revoke", "Cancelling an invite removes it from the list; cancelling twice is harmless", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var (id, _) = await PlatformHelpers.InsertInviteAsync(ctx, client, SignupHelpers.NewEmail(ctx, "revoke"));
            var listed = (await client.Http.GetAsync("/api/members/invites", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId(listed, id, "Open invites before cancelling");
            (await client.Http.DeleteAsync($"/api/members/invites/{id}", ctx.Ct)).ExpectStatus(204);
            (await client.Http.DeleteAsync($"/api/members/invites/{id}", ctx.Ct)).ExpectStatus(204);
            Expect.LacksId((await client.Http.GetAsync("/api/members/invites", ct: ctx.Ct)).ExpectOk().Array(), id, "Open invites after cancelling");
        }, needs: Needs.Session | Needs.Database)
        .Test("resend-expired-full", "Resending an expired invite needs a free seat again (422 on a full plan)", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var (id, _) = await PlatformHelpers.InsertInviteAsync(ctx, client, SignupHelpers.NewEmail(ctx, "resend"), minutes: -60);
            var response = await client.Http.PostAsync($"/api/members/invites/{id}/resend", ct: ctx.Ct);
            Expect.Equal(422, response.Status, $"Resending an expired invite with no free seat{response.Snippet(200)}");
        }, needs: Needs.Session | Needs.Database)
        .Test("seats", "Open invites and members hold seats; a plan with too few seats refuses invites and acceptance", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            await AccountsHelpers.SharedMemberAsync(ctx); // owner + member = the 2 seats of the "inbox" plan
            var email = SignupHelpers.NewEmail(ctx, "seats");
            var (_, token) = await PlatformHelpers.InsertInviteAsync(ctx, team, email);
            try
            {
                await ctx.SetPlanAsync(team, "inbox");
                var invite = await team.Http.PostAsync("/api/members/invites", new { email = SignupHelpers.NewEmail(ctx, "seats2") }, ct: ctx.Ct);
                Expect.Equal(422, invite.Status, $"Invite with every seat taken{invite.Snippet(200)}");
                var refused = await AccountsHelpers.AcceptAsync(ctx, ctx.NewHttp(), token, "Seat Person", "Auto-Join-2026x", "Auto-Join-2026x");
                Expect.True(refused.Status == 200 && AccountsHelpers.Shows(refused, "limit"), $"Accepting with no free seat answered {refused.Status}: \"{PlatformHelpers.Alert(refused)}\".");
            }
            finally
            {
                await ctx.SetPlanAsync(team, "business");
            }
            var accepted = await AccountsHelpers.AcceptAsync(ctx, ctx.NewHttp(), token, "Seat Person", "Auto-Join-2026x", "Auto-Join-2026x");
            Expect.Equal(302, accepted.Status, $"Accepting after the plan grew again ({PlatformHelpers.Alert(accepted)})");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Test("owner-self", "The owner can't deactivate their own account", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var owner = await PlatformHelpers.OwnerUserIdAsync(ctx, client);
            var response = await client.Http.PostAsync($"/api/members/{owner}/deactivate", ct: ctx.Ct);
            Expect.Equal(400, response.Status, "Owner deactivating themselves");
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, client.Http), "Owner session afterwards");
        })
        .Test("bad-ids", "Unknown member and invite ids answer 404", async ctx =>
        {
            var api = await ctx.ApiAsync();
            Expect.Equal(404, (await api.PostAsync("/api/members/not-a-user/deactivate", ct: ctx.Ct)).Status, "Deactivate 'not-a-user'");
            Expect.Equal(404, (await api.PostAsync($"/api/members/{Guid.NewGuid()}/reactivate", ct: ctx.Ct)).Status, "Reactivate an unknown user");
            Expect.Equal(404, (await api.PostAsync($"/api/members/invites/{Guid.NewGuid()}/resend", ct: ctx.Ct)).Status, "Resend an unknown invite");
            Expect.Equal(404, (await api.DeleteAsync($"/api/members/invites/{Guid.NewGuid()}", ctx.Ct)).Status, "Cancel an unknown invite");
        })
        .Build();
}

/// <summary>Member vs owner (decision D5: only the owner manages members; members can use everything else), and what
/// deactivating a member does to their sessions.</summary>
[Suite(11)]
public static class MemberRolesSuite
{
    public static TestSuite Build() => new SuiteBuilder("member-roles", "Member roles and deactivation", "D5: members can't manage members but can use everything else; deactivation ends access.")
        .Test("manage-members-forbidden", "A member can't invite, resend, cancel, deactivate or reactivate (403)", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var member = await AccountsHelpers.SharedMemberAsync(ctx);
            var owner = await PlatformHelpers.OwnerUserIdAsync(ctx, team);
            var (invite, _) = await PlatformHelpers.InsertInviteAsync(ctx, team, SignupHelpers.NewEmail(ctx, "byowner"));
            var problems = new List<string>();
            foreach (var (method, path, body) in new (string, string, object?)[]
                     {
                         ("POST", "/api/members/invites", new { email = SignupHelpers.NewEmail(ctx, "bymember") }),
                         ("POST", $"/api/members/invites/{invite}/resend", null),
                         ("DELETE", $"/api/members/invites/{invite}", null),
                         ("POST", $"/api/members/{owner}/deactivate", null),
                         ("POST", $"/api/members/{member.UserId}/reactivate", null),
                     })
            {
                var response = await member.Http.SendJsonAsync(new HttpMethod(method), path, body, ct: ctx.Ct);
                if (response.Status != 403) problems.Add($"{method} {path} -> {response.Status}");
                else if (response.Json?["error"]?.ToString().Contains("Only the account owner", StringComparison.OrdinalIgnoreCase) != true) problems.Add($"{method} {path}: unexpected message{response.Snippet(150)}");
            }
            Expect.True(problems.Count == 0, "Member-management calls a member wasn't refused: " + string.Join("; ", problems));
            Expect.ContainsId((await team.Http.GetAsync("/api/members/invites", ct: ctx.Ct)).ExpectOk().Array(), invite, "The owner's invite after the member's attempts");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Test("uses-everything-else", "A member can use the rest: contacts, inbox, services, bookings, business details, billing, settings", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var member = await AccountsHelpers.SharedMemberAsync(ctx);
            var http = member.Http;
            var problems = new List<string>();
            foreach (var path in new[] { "/api/contacts", "/api/conversations", "/api/services", "/api/bookings", "/api/billing/summary", "/api/members", "/api/notifications" })
            {
                var response = await http.GetAsync(path, ct: ctx.Ct);
                if (response.Status != 200) problems.Add($"GET {path} -> {response.Status}");
            }
            var service = await http.PostAsync("/api/services", new { name = $"Member service {ctx.Tag}", code = $"auto-{ctx.Tag}-msvc", bookingDuration = 30 }, ct: ctx.Ct);
            if (service.Status != 201) problems.Add($"POST /api/services -> {service.Status}");
            foreach (var path in new[] { "/dashboard", "/inbox", "/Members", "/Services", "/Campaigns", "/settings/business-info", "/settings/billing", "/settings/auto-respond", "/settings/reminders" })
            {
                var page = await http.GetAsync(path, ct: ctx.Ct);
                if (page.Status != 200) problems.Add($"{path} -> {page.Status} {page.Location}");
            }
            var details = await http.SubmitFormAsync("/settings/business-info", new Dictionary<string, string> { ["Name"] = team.Name, ["Phone"] = "+15550100999" }, ct: ctx.Ct);
            if (details.Status != 302) problems.Add($"Saving business details -> {details.Status} {PlatformHelpers.Alert(details)}");
            Expect.True(problems.Count == 0, "A member was refused something D5 allows: " + string.Join("; ", problems));
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Test("owner-only-data", "Only the owner can export or delete a contact's data and change the client's default language", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var member = await AccountsHelpers.SharedMemberAsync(ctx);
            var contact = await PlatformHelpers.IngestContactAsync(ctx, team, $"Auto owner-only {ctx.Tag}");
            Expect.Equal(403, (await member.Http.GetAsync($"/api/contacts/{contact}/export", ct: ctx.Ct)).Status, "Member exporting a contact");
            Expect.Equal(403, (await member.Http.SendJsonAsync(HttpMethod.Delete, $"/api/contacts/{contact}", new { confirmation = $"Auto owner-only {ctx.Tag}" }, ct: ctx.Ct)).Status, "Member deleting a contact");
            Expect.Equal(200, (await team.Http.GetAsync($"/api/contacts/{contact}/export", ct: ctx.Ct)).Status, "Owner exporting the contact");

            var page = (await member.Http.GetAsync("/settings/business-info", ct: ctx.Ct)).ExpectOk();
            Expect.True(AccountsHelpers.Shows(page, "Only the account owner can change this"), "The member's business-info page doesn't say the default language is owner-only.");
            var language = await member.Http.SubmitFormAsync("/settings/business-info", new Dictionary<string, string> { ["Language"] = "fr" }, handler: "SaveLanguage", ct: ctx.Ct);
            Expect.True(language.Status == 200 && AccountsHelpers.Shows(language, "Only the account owner"), $"A member saving the default language answered {language.Status}.");
            var ownerPage = (await team.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            Expect.True(PlatformHelpers.HtmlTag(ownerPage.Body).Contains("lang=\"en\""), "The client's default language changed after the member's attempt.");
        }, needs: Needs.Session | Needs.Database | Needs.IngestKey | Needs.AdminKey, risk: Risk.Billing)
        .Test("deactivate", "Deactivating a member ends their open session and sign-in; reactivating restores it", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var member = await AccountsHelpers.NewMemberAsync(ctx, team, "deact");
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, member.Http), "Member session before deactivation");

            (await team.Http.PostAsync($"/api/members/{member.UserId}/deactivate", ct: ctx.Ct)).ExpectStatus(204);
            Expect.Equal(401, await PlatformHelpers.SessionStatusAsync(ctx, member.Http), "The member's open session after deactivation (API)");
            var page = await member.Http.GetAsync("/dashboard", ct: ctx.Ct);
            Expect.True(page.Status == 302 && page.Location?.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) == true, $"The member's open session after deactivation (page) answered {page.Status} {page.Location}.");
            var (_, signIn) = await ctx.SignInAsync(member.Email, member.Password);
            Expect.Equal(200, signIn.Status, "Deactivated member signing in");
            // Decision S7: every refusal shows the wrong-password text, so no account state leaks.
            Expect.True(PlatformHelpers.Alert(signIn).Contains("don't match", StringComparison.OrdinalIgnoreCase), $"Unexpected message: \"{PlatformHelpers.Alert(signIn)}\".");
            Expect.EqualText("false", (await AccountsHelpers.MemberRowAsync(ctx, team, member.Email))["isActive"], "Member listed as active");
            (await team.Http.PostAsync($"/api/members/{member.UserId}/deactivate", ct: ctx.Ct)).ExpectStatus(204);

            (await team.Http.PostAsync($"/api/members/{member.UserId}/reactivate", ct: ctx.Ct)).ExpectStatus(204);
            var again = await PlatformHelpers.SignedInAsync(ctx, member.Email, member.Password);
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, again), "Reactivated member's new session");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Test("deactivated-live-events", "A deactivated member's open Inbox connection stops receiving the client's live events", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var member = await AccountsHelpers.NewMemberAsync(ctx, team, "hub");
            var contact = await PlatformHelpers.IngestContactAsync(ctx, team, $"Auto hub {ctx.Tag}");
            var conversation = await PlatformHelpers.ConversationAsync(ctx, team.Http, contact);
            using var memberSession = new RawSession(ctx);
            await memberSession.SignInAsync(member.Email, member.Password);
            using var ownerSession = new RawSession(ctx);
            await ownerSession.SignInAsync(team.Email, team.Password);
            await using var memberHub = await HubProbe.ConnectAsync(ctx, memberSession);
            await using var ownerHub = await HubProbe.ConnectAsync(ctx, ownerSession);

            (await team.Http.PostAsync($"/api/members/{member.UserId}/deactivate", ct: ctx.Ct)).ExpectStatus(204);
            (await team.Http.PostAsync($"/api/conversations/{conversation}/close", ct: ctx.Ct)).ExpectStatus(200, 204);
            var id = conversation.ToString();
            Expect.True(await ownerHub.WaitForAsync(e => e.Contains(id, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(15)),
                "Control failed: the owner's own connection got no event, so the check means nothing.");
            await Task.Delay(1500, ctx.Ct);
            var leaked = memberHub.Events.Where(e => e.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList();
            Expect.True(leaked.Count == 0, $"The deactivated member's open connection still received the client's events: {string.Join(" | ", leaked)}");
        }, needs: Needs.Session | Needs.Database | Needs.IngestKey | Needs.AdminKey, risk: Risk.Billing,
            description: "Doc-review retest (dr-c): InboxHub adds a connection to the client group only when it connects; the cookie check runs on HTTP requests, not on an open WebSocket. Confirmed open on staging (found while automating).", finding: "t11-auto-02")
        .Test("own-language", "A member can switch their own language without changing anyone else's", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var member = await AccountsHelpers.SharedMemberAsync(ctx);
            try
            {
                var set = await member.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string> { ["language"] = "fr", ["returnUrl"] = "/Members" }, postTo: "/language", ct: ctx.Ct);
                Expect.Equal(302, set.Status, "Member picking French");
                Expect.Equal("/Members", set.Location, "Where the language picker goes");
                var mine = (await member.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
                Expect.True(PlatformHelpers.HtmlTag(mine.Body).Contains("lang=\"fr\""), $"The member's dashboard isn't in French: {PlatformHelpers.HtmlTag(mine.Body)}");
                var owners = (await team.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
                Expect.True(PlatformHelpers.HtmlTag(owners.Body).Contains("lang=\"en\""), $"The owner's dashboard changed language: {PlatformHelpers.HtmlTag(owners.Body)}");
            }
            finally
            {
                await member.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string> { ["language"] = "", ["returnUrl"] = "/dashboard" }, postTo: "/language", ct: ctx.Ct);
            }
            var back = (await member.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            Expect.True(PlatformHelpers.HtmlTag(back.Body).Contains("lang=\"en\""), $"Clearing the member's language didn't return to the client default: {PlatformHelpers.HtmlTag(back.Body)}");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Test("html-name", "A member name with HTML is shown as text on the Members page", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            const string payload = "<script>alert('omni')</script>";
            var member = await AccountsHelpers.NewMemberAsync(ctx, team, "xss", $"{payload} Auto {ctx.Tag}");
            var page = (await team.Http.GetAsync("/Members", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains(payload, StringComparison.Ordinal), "The Members page renders the member's name as HTML.");
            Expect.True(page.Body.Contains(member.Email, StringComparison.OrdinalIgnoreCase), "The new member isn't on the Members page.");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Build();
}

