using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Settings → Business info on a private client: details, name rules, input checks, the default language,
/// HTML in the name, opening hours and special dates.</summary>
[Suite(12)]
public static class BusinessInfoSuite
{
    public static TestSuite Build() => new SuiteBuilder("business-info", "Business info", "Business details, default language, opening hours and special dates.")
        .Test("save-details", "The owner saves the business details and sees them on the page", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var fields = Details(client, website: $"https://example.com/{ctx.Tag}", address: $"Automation street {ctx.Tag}");
            var response = await client.Http.SubmitFormAsync("/settings/business-info", fields, ct: ctx.Ct);
            Expect.Equal(302, response.Status, $"Saving details ({PlatformHelpers.Alert(response)})");
            var page = (await client.Http.GetAsync("/settings/business-info", ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains($"https://example.com/{ctx.Tag}", StringComparison.Ordinal), "The saved website isn't on the page.");
            Expect.True(page.Body.Contains($"Automation street {ctx.Tag}", StringComparison.Ordinal), "The saved address isn't on the page.");
        })
        .Test("name-rules", "An empty or over-long business name is refused and the name is kept", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var empty = await client.Http.SubmitFormAsync("/settings/business-info", Details(client, name: ""), ct: ctx.Ct);
            Expect.True(empty.Status == 200 && AccountsHelpers.Shows(empty, "Enter the"), $"An empty name answered {empty.Status}: \"{PlatformHelpers.Alert(empty)}\".");
            var tooLong = await client.Http.SubmitFormAsync("/settings/business-info", Details(client, name: TestContext.ClientNamePrefix + new string('x', 200)), ct: ctx.Ct);
            Expect.True(tooLong.Status == 200 && AccountsHelpers.Shows(tooLong, "200 characters or fewer"), $"A 213-character name answered {tooLong.Status}: \"{PlatformHelpers.Alert(tooLong)}\".");
            var page = (await client.Http.GetAsync("/settings/business-info", ct: ctx.Ct)).ExpectOk();
            Expect.True(AccountsHelpers.Shows(page, client.Name), "The business name changed after the refused saves.");
        })
        .Test("invalid-email", "An invalid business email is refused", async ctx =>
            await ExpectRefusedDetailAsync(ctx, "email 'not-an-email'", c => Details(c, email: "not-an-email")), finding: "t1-accounts-12")
        .Test("unsafe-website", "A javascript: website is refused (http/https only)", async ctx =>
            await ExpectRefusedDetailAsync(ctx, "website 'javascript:alert(1)'", c => Details(c, website: "javascript:alert(1)")), finding: "t1-accounts-12")
        .Test("long-address", "A 5000-character address is refused (length limits like sign-up's)", async ctx =>
            await ExpectRefusedDetailAsync(ctx, "a 5000-character address", c => Details(c, address: new string('a', 5000))), finding: "t1-accounts-12")
        .Test("html-name", "A business name with HTML is shown as text in the sidebar, Business info and Members", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            const string payload = "<img src=x onerror=alert('omni')>";
            try
            {
                (await client.Http.SubmitFormAsync("/settings/business-info", Details(client, name: $"{client.Name} {payload}"), ct: ctx.Ct)).ExpectStatus(302);
                var problems = new List<string>();
                foreach (var path in new[] { "/dashboard", "/settings/business-info", "/Members", "/settings/billing" })
                {
                    var page = (await client.Http.GetAsync(path, ct: ctx.Ct)).ExpectOk();
                    if (page.Body.Contains(payload, StringComparison.Ordinal)) problems.Add(path);
                }
                Expect.True(problems.Count == 0, "Pages that render the business name as HTML: " + string.Join(", ", problems));
            }
            finally
            {
                await client.Http.SubmitFormAsync("/settings/business-info", Details(client), ct: ctx.Ct);
            }
        })
        .Test("name-bidi", "The client name in the sidebar is isolated for right-to-left text (dir=auto or <bdi>)", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var page = (await client.Http.GetAsync("/Members", ct: ctx.Ct)).ExpectOk();
            var tag = Regex.Match(page.Body, "<span[^>]*class=\"au-brand__sub\"[^>]*>(.{0,300})", RegexOptions.Singleline);
            Expect.True(tag.Success, "The sidebar has no client name element (au-brand__sub).");
            var isolated = tag.Value.Contains("dir=\"auto\"", StringComparison.OrdinalIgnoreCase) || tag.Groups[1].Value.TrimStart().StartsWith("<bdi", StringComparison.OrdinalIgnoreCase);
            Expect.True(isolated, "The sidebar client name isn't isolated (no dir=\"auto\" / <bdi>), so '[QA] Accounts' shows as 'Accounts [QA]' in Arabic.");
        }, finding: "t1-accounts-14")
        .Test("default-language", "The owner sets the client's default language; an unknown code changes nothing", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            try
            {
                var bad = await client.Http.SubmitFormAsync("/settings/business-info", new Dictionary<string, string> { ["Language"] = "xx" }, handler: "SaveLanguage", ct: ctx.Ct);
                Expect.Equal(200, bad.Status, "Saving default language 'xx'");
                Expect.True(PlatformHelpers.HtmlTag((await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk().Body).Contains("lang=\"en\""), "Language 'xx' changed the dashboard language.");

                var fr = await client.Http.SubmitFormAsync("/settings/business-info", new Dictionary<string, string> { ["Language"] = "fr" }, handler: "SaveLanguage", ct: ctx.Ct);
                Expect.Equal(302, fr.Status, $"Saving default language 'fr' ({PlatformHelpers.Alert(fr)})");
                // The owner has no language of their own, so the client default applies to them too.
                var tag = PlatformHelpers.HtmlTag((await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk().Body);
                Expect.True(tag.Contains("lang=\"fr\""), $"The dashboard didn't switch to the new default language: {tag}");
            }
            finally
            {
                await client.Http.SubmitFormAsync("/settings/business-info", new Dictionary<string, string> { ["Language"] = "en" }, handler: "SaveLanguage", ct: ctx.Ct);
            }
        })
        .Test("availability-save", "Opening hours, timezone and booking rules save and show on the availability tab", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            try
            {
                await ctx.Fixtures.SaveAvailabilityAsync(client, timezone: "Asia/Dubai", start: "09:15", end: "17:45", noticeHours: 3, durationMinutes: 45, bufferMinutes: 10, maxAdvanceDays: 30,
                    openDays: new[] { false, true, true, true, true, true, false });
                var page = (await client.Http.GetAsync("/settings/business-info?tab=availability", ct: ctx.Ct)).ExpectOk();
                Expect.True(Regex.IsMatch(page.Body, "value=\"Asia/Dubai\"[^>]*selected|selected[^>]*value=\"Asia/Dubai\""), "The saved timezone isn't selected.");
                Expect.True(page.Body.Contains("09:15", StringComparison.Ordinal) && page.Body.Contains("17:45", StringComparison.Ordinal), "The saved hours aren't shown.");
            }
            finally
            {
                await ctx.Fixtures.SaveAvailabilityAsync(client);
            }
        })
        .Test("availability-invalid", "Invalid opening hours and booking rules are refused, each with its own message", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var cases = new (string Label, string Expected, Action<Dictionary<string, string>> Change)[]
            {
                ("unknown timezone", "Unknown time zone", f => f["TimezoneId"] = "Mars/Olympus"),
                ("length 4 minutes", "between 5 and 480", f => f["DefaultDurationMinutes"] = "4"),
                ("length 481 minutes", "between 5 and 480", f => f["DefaultDurationMinutes"] = "481"),
                ("gap 241 minutes", "between 0 and 240", f => f["BufferMinutes"] = "241"),
                ("notice 721 hours", "between 0 and 720", f => f["NoticeHours"] = "721"),
                ("advance 0 days", "between 1 and 365", f => f["MaxAdvanceDays"] = "0"),
                ("advance 366 days", "between 1 and 365", f => f["MaxAdvanceDays"] = "366"),
                ("Monday ends before it starts", "after the start time", f => { f["Days[1].Start"] = "17:00"; f["Days[1].End"] = "09:00"; }),
                ("open Monday without times", "enter a start and end time", f => { f["Days[1].Start"] = ""; f["Days[1].End"] = ""; }),
            };
            var problems = new List<string>();
            foreach (var (label, expected, change) in cases)
            {
                var fields = AvailabilityFields();
                change(fields);
                var response = await client.Http.SubmitFormAsync("/settings/business-info", fields, handler: "SaveAvailability", ct: ctx.Ct);
                if (response.Status != 200 || !AccountsHelpers.Shows(response, expected)) problems.Add($"{label}: {response.Status} \"{PlatformHelpers.Alert(response)}\"");
            }
            Expect.True(problems.Count == 0, "Invalid availability not refused as expected: " + string.Join("; ", problems));
            var page = (await client.Http.GetAsync("/settings/business-info?tab=availability", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("value=\"Mars/Olympus\"", StringComparison.Ordinal), "A refused timezone was saved.");
        })
        .Test("special-dates", "Special dates can be added and removed; invalid ones are refused", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var date = DateTime.UtcNow.Date.AddDays(40).ToString("yyyy-MM-dd");
            var problems = new List<string>();
            foreach (var (label, expected, fields) in new (string, string, Dictionary<string, string>)[]
                     {
                         ("no date", "Pick a date", Exception("", closed: true)),
                         ("ends before it starts", "after the start time", Exception(date, closed: false, start: "15:00", end: "10:00")),
                         ("different hours without times", "Enter a start and end time", Exception(date, closed: false)),
                         ("201-character reason", "200 characters or fewer", Exception(date, closed: true, reason: new string('r', 201))),
                     })
            {
                var response = await client.Http.SubmitFormAsync("/settings/business-info?tab=availability", fields, handler: "AddException", ct: ctx.Ct);
                if (response.Status != 200 || !AccountsHelpers.Shows(response, expected)) problems.Add($"{label}: {response.Status} \"{PlatformHelpers.Alert(response)}\"");
            }
            Expect.True(problems.Count == 0, "Invalid special dates not refused as expected: " + string.Join("; ", problems));

            var reason = $"Automation holiday {ctx.Tag}";
            var added = await client.Http.SubmitFormAsync("/settings/business-info?tab=availability", Exception(date, closed: true, reason: reason), handler: "AddException", ct: ctx.Ct);
            Expect.Equal(302, added.Status, $"Adding a closed day ({PlatformHelpers.Alert(added)})");
            var page = (await client.Http.GetAsync("/settings/business-info?tab=availability", ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains(reason, StringComparison.Ordinal), "The added special date isn't listed.");
            var action = AccountsHelpers.DeleteExceptionAction(page.Body) ?? throw new TestFailure("The special date has no remove button.");
            var removed = await client.Http.SubmitFormAsync("/settings/business-info?tab=availability", new Dictionary<string, string>(), postTo: action, ct: ctx.Ct);
            Expect.Equal(302, removed.Status, "Removing the special date");
            var after = (await client.Http.GetAsync("/settings/business-info?tab=availability", ct: ctx.Ct)).ExpectOk();
            Expect.True(!after.Body.Contains(reason, StringComparison.Ordinal), "The removed special date is still listed.");
        })
        .Build();

    /// <summary>The details form with the client's normal values, overriding what's given.</summary>
    internal static Dictionary<string, string> Details(TestClient client, string? name = null, string? email = null, string? website = null, string? address = null) => new()
    {
        ["Name"] = name ?? client.Name,
        ["Phone"] = "+15550100100",
        ["Email"] = email ?? client.Email,
        ["Website"] = website ?? "https://example.com",
        ["Address"] = address ?? "1 Automation Way",
        ["OperatingHours"] = "Mon-Fri 9-5",
        ["BookingInfo"] = "",
    };

    /// <summary>A details save that must be refused; if it was saved, the normal values are put back.</summary>
    private static async Task ExpectRefusedDetailAsync(TestContext ctx, string what, Func<TestClient, Dictionary<string, string>> fields)
    {
        var client = await PlatformHelpers.SettingsClientAsync(ctx);
        var response = await client.Http.SubmitFormAsync("/settings/business-info", fields(client), ct: ctx.Ct);
        if (response.Status == 302)
        {
            await client.Http.SubmitFormAsync("/settings/business-info", Details(client), ct: ctx.Ct);
            throw new TestFailure($"Business info saved {what} without any check.");
        }
        Expect.Equal(200, response.Status, $"Saving {what}");
    }

    private static Dictionary<string, string> AvailabilityFields()
    {
        var fields = new Dictionary<string, string>
        {
            ["TimezoneId"] = "UTC", ["DefaultDurationMinutes"] = "30", ["BufferMinutes"] = "0", ["NoticeHours"] = "0", ["MaxAdvanceDays"] = "60",
        };
        for (var day = 0; day < 7; day++)
        {
            fields[$"Days[{day}].DayOfWeek"] = day.ToString();
            fields[$"Days[{day}].IsOpen"] = "true";
            fields[$"Days[{day}].Start"] = "06:00";
            fields[$"Days[{day}].End"] = "22:00";
        }
        return fields;
    }

    private static Dictionary<string, string> Exception(string date, bool closed, string start = "", string end = "", string reason = "") => new()
    {
        ["ExceptionDate"] = date,
        ["ExceptionClosed"] = closed ? "true" : "false",
        ["ExceptionStart"] = start,
        ["ExceptionEnd"] = end,
        ["ExceptionReason"] = reason,
    };
}

/// <summary>Settings → Automatic replies, Reminders (validation only) and Inbox (AI per account).</summary>
[Suite(13)]
public static class ClientSettingsSuite
{
    public static TestSuite Build() => new SuiteBuilder("client-settings", "Auto-reply, reminders and inbox settings", "Form validation of the automatic-reply, reminder and inbox AI settings.")
        .Test("auto-reply-invalid", "Invalid automatic replies are refused, each with its own message", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var channel = await FirstAutoReplyChannelAsync(ctx, client);
            var cases = new (string Label, string Expected, Dictionary<string, string> Fields)[]
            {
                ("new reply on without text", "Write the message for a new conversation", Reply(channel, newOn: true)),
                ("unknown reopened mode", "Choose what happens when a conversation is reopened", Reply(channel, mode: "sometimes")),
                ("'same' without a new message", "needs a message for a new conversation", Reply(channel, mode: "same")),
                ("'custom' without its message", "Write the message for a reopened conversation", Reply(channel, mode: "custom")),
                ("1001-character message", "A message can be at most", Reply(channel, newOn: true, newMessage: new string('m', 1001))),
                ("unknown channel", "available on that channel", Reply("pigeon", newOn: true, newMessage: "Hello")),
            };
            var problems = new List<string>();
            foreach (var (label, expected, fields) in cases)
            {
                var response = await client.Http.SubmitFormAsync("/settings/auto-respond", fields, ct: ctx.Ct);
                if (response.Status != 200 || !AccountsHelpers.Shows(response, expected)) problems.Add($"{label}: {response.Status}");
            }
            Expect.True(problems.Count == 0, "Invalid automatic replies not refused as expected: " + string.Join("; ", problems));
        })
        .Test("auto-reply-save", "A valid automatic reply saves and shows on the page", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var channel = await FirstAutoReplyChannelAsync(ctx, client);
            var message = $"Thanks for writing, automation {ctx.Tag}";
            try
            {
                var saved = await client.Http.SubmitFormAsync("/settings/auto-respond", Reply(channel, newOn: true, newMessage: message, mode: "same"), ct: ctx.Ct);
                Expect.Equal(302, saved.Status, "Saving a valid automatic reply");
                var page = (await client.Http.GetAsync("/settings/auto-respond?tab=" + channel, ct: ctx.Ct)).ExpectOk();
                Expect.True(page.Body.Contains(message, StringComparison.Ordinal), "The saved message isn't on the page.");
            }
            finally
            {
                await client.Http.SubmitFormAsync("/settings/auto-respond", Reply(channel), ct: ctx.Ct);
            }
        })
        .Test("reminders-invalid", "Reminder hours outside 0-168 and someone else's template are refused; valid settings save", async ctx =>
        {
            var client = await PlatformHelpers.TeamAsync(ctx); // the form (and its messages) only shows on a plan with bookings
            var problems = new List<string>();
            foreach (var (label, expected, fields) in new (string, string, Dictionary<string, string>)[]
                     {
                         ("first 169 hours", "between 0 and", Reminders(first: "169")),
                         ("second -1 hours", "between 0 and", Reminders(second: "-1")),
                         ("a template that isn't theirs", "isn't one of yours", Reminders(template: Guid.NewGuid().ToString())),
                     })
            {
                var response = await client.Http.SubmitFormAsync("/settings/reminders", fields, ct: ctx.Ct);
                if (response.Status != 200 || !AccountsHelpers.Shows(response, expected)) problems.Add($"{label}: {response.Status} \"{PlatformHelpers.Alert(response)}\"");
            }
            Expect.True(problems.Count == 0, "Invalid reminder settings not refused as expected: " + string.Join("; ", problems));
            var valid = await client.Http.SubmitFormAsync("/settings/reminders", Reminders(first: "48", second: "3"), ct: ctx.Ct);
            Expect.Equal(302, valid.Status, "Saving valid reminder settings");
            var page = (await client.Http.GetAsync("/settings/reminders", ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains("value=\"48\"", StringComparison.Ordinal), "The saved first-reminder hours aren't shown.");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("inbox-ai-invalid", "Inbox settings: turning AI on without a bot, or with a bot that doesn't exist, is refused", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var channel = await ctx.Webhooks.FakeTelegramAsync(team);
            // The DB insert bypasses the app, which may have this client's channel list cached already.
            await ctx.Fixtures.EvictCacheAsync($"channels:integrations:{team.ClientId:N}");
            var page = (await team.Http.GetAsync("/settings/inbox", ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains(channel.IntegrationId.ToString(), StringComparison.OrdinalIgnoreCase), "The fake Telegram account isn't on the Inbox settings page.");
            var noBot = await team.Http.SubmitFormAsync("/settings/inbox", Inbox(channel.IntegrationId, aiOn: true, botId: null), ct: ctx.Ct);
            Expect.True(noBot.Status == 200 && AccountsHelpers.Shows(noBot, "Choose the bot that answers"), $"AI on without a bot answered {noBot.Status}: \"{PlatformHelpers.Alert(noBot)}\".");
            var unknownBot = await team.Http.SubmitFormAsync("/settings/inbox", Inbox(channel.IntegrationId, aiOn: true, botId: Guid.NewGuid()), ct: ctx.Ct);
            Expect.True(unknownBot.Status == 200 && AccountsHelpers.Shows(unknownBot, "doesn't exist anymore"), $"AI on with an unknown bot answered {unknownBot.Status}: \"{PlatformHelpers.Alert(unknownBot)}\".");
            var enabled = await ctx.Db.ScalarAsync<long>("select count(*) from crm.inbox_settings where channel_integration_id = @id and ai_enabled", new { id = channel.IntegrationId }, ctx.Ct);
            Expect.Equal(0L, enabled, "Inbox settings rows with AI on after the refused saves");
            var unknownAccount = await team.Http.SubmitFormAsync("/settings/inbox", Inbox(Guid.NewGuid(), aiOn: false, botId: null), ct: ctx.Ct);
            Expect.Equal(302, unknownAccount.Status, "Saving settings for an account that isn't connected");
        }, needs: Needs.Session | Needs.Database | Needs.AdminKey, risk: Risk.Billing)
        .Build();

    private static async Task<string> FirstAutoReplyChannelAsync(TestContext ctx, TestClient client)
    {
        var page = (await client.Http.GetAsync("/settings/auto-respond", ct: ctx.Ct)).ExpectOk();
        var match = Regex.Match(page.Body, "name=\"Replies\\[0\\]\\.Channel\" value=\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : throw new TestFailure("The automatic-reply page lists no channel.");
    }

    private static Dictionary<string, string> Reply(string channel, bool newOn = false, string newMessage = "", string mode = "off", string reopenedMessage = "") => new()
    {
        ["Replies[0].Channel"] = channel,
        ["Replies[0].NewConversationEnabled"] = newOn ? "true" : "false",
        ["Replies[0].NewConversationMessage"] = newMessage,
        ["Replies[0].ReopenedMode"] = mode,
        ["Replies[0].ReopenedMessage"] = reopenedMessage,
        ["Replies[0].SendWhenAiAnswers"] = "false",
    };

    private static Dictionary<string, string> Reminders(string first = "24", string second = "2", string? template = null)
    {
        var fields = new Dictionary<string, string> { ["Enabled"] = "false", ["FirstHoursBefore"] = first, ["SecondHoursBefore"] = second };
        if (template is not null) fields["Templates[en]"] = template;
        return fields;
    }

    private static Dictionary<string, string> Inbox(Guid account, bool aiOn, Guid? botId) => new()
    {
        ["ChannelIntegrationId"] = account.ToString(),
        ["AiEnabled"] = aiOn ? "true" : "false",
        ["BotId"] = botId?.ToString() ?? "",
        ["AiBotNewConversations"] = "true",
        ["AiBotReopenedConversations"] = "true",
    };
}

/// <summary>Language: the visitor's language (Accept-Language, the picker cookie), the signed-in user's own language
/// and API errors in it, and the picker's return URL.</summary>
[Suite(14)]
public static class LanguageSuite
{
    public static TestSuite Build() => new SuiteBuilder("language", "Language", "Visitor and user language, right-to-left Arabic, the language picker.")
        .Test("accept-language", "Signed-out pages follow the browser language (French, Arabic RTL) and fall back to English", async ctx =>
        {
            foreach (var (header, lang, rtl) in new[] { ("fr-FR,fr;q=0.9", "fr", false), ("ar", "ar", true), ("de-DE,de;q=0.9", "en", false) })
            {
                var http = ctx.NewHttp(new Dictionary<string, string> { ["Accept-Language"] = header });
                var tag = PlatformHelpers.HtmlTag((await http.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk().Body);
                Expect.True(tag.Contains($"lang=\"{lang}\""), $"Accept-Language {header}: expected lang=\"{lang}\", got {tag}");
                Expect.Equal(rtl, tag.Contains("dir=\"rtl\""), $"Accept-Language {header}: right-to-left");
            }
        }, needs: Needs.None)
        .Test("visitor-picker", "The sign-in page's language picker sets the language; an unknown code changes nothing", async ctx =>
        {
            var http = ctx.NewHttp();
            var picked = await http.SubmitFormAsync("/Account/Login", new Dictionary<string, string> { ["language"] = "ar", ["returnUrl"] = "/Account/Login" }, postTo: "/language", ct: ctx.Ct);
            Expect.Equal(302, picked.Status, "Picking Arabic");
            Expect.Equal("/Account/Login", picked.Location, "Where the picker goes");
            Expect.True(PlatformHelpers.HtmlTag((await http.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk().Body).Contains("lang=\"ar\""), "The sign-in page isn't in Arabic after picking it.");
            var bogus = await http.SubmitFormAsync("/Account/Login", new Dictionary<string, string> { ["language"] = "xx", ["returnUrl"] = "/Account/Login" }, postTo: "/language", ct: ctx.Ct);
            Expect.Equal(302, bogus.Status, "Picking 'xx'");
            Expect.True(PlatformHelpers.HtmlTag((await http.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk().Body).Contains("lang=\"ar\""), "An unknown code changed the language.");
            var noToken = await http.PostRawAsync("/language", "language=fr&returnUrl=%2F", "application/x-www-form-urlencoded", ct: ctx.Ct);
            Expect.Equal(400, noToken.Status, "Language POST without the token");
        }, needs: Needs.None)
        .Test("picker-return-url", "The language picker never redirects to another site", async ctx =>
        {
            var problems = new List<string>();
            foreach (var returnUrl in new[] { "https://evil.example", "//evil.example", "/\\evil.example" })
            {
                var response = await ctx.NewHttp().SubmitFormAsync("/Account/Login", new Dictionary<string, string> { ["language"] = "en", ["returnUrl"] = returnUrl }, postTo: "/language", ct: ctx.Ct);
                if (response.Status != 302 || !PlatformHelpers.IsSameSite(ctx, response.Location) || response.Location!.Contains("evil", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{returnUrl} -> {response.Status} {response.Location}");
            }
            Expect.True(problems.Count == 0, "The picker followed an external return URL: " + string.Join("; ", problems));
        }, needs: Needs.None)
        .Test("user-language", "A signed-in user's own language applies to pages and API errors, and clearing it returns to the client default", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            try
            {
                (await client.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string> { ["language"] = "fr", ["returnUrl"] = "/dashboard" }, postTo: "/language", ct: ctx.Ct)).ExpectStatus(302);
                var tag = PlatformHelpers.HtmlTag((await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk().Body);
                Expect.True(tag.Contains("lang=\"fr\""), $"The dashboard isn't in French: {tag}");
                var error = await client.Http.PostAsync("/api/members/invites", new { email = "not-an-email" }, ct: ctx.Ct);
                Expect.Equal(400, error.Status, "Invalid invite");
                Expect.EqualText("Saisissez une adresse e-mail valide.", error.Json?["error"], "API error text for a French user");
            }
            finally
            {
                await client.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string> { ["language"] = "", ["returnUrl"] = "/dashboard" }, postTo: "/language", ct: ctx.Ct);
            }
            var back = PlatformHelpers.HtmlTag((await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk().Body);
            Expect.True(back.Contains("lang=\"en\""), $"Clearing the language didn't return to the client default (English): {back}");
            var english = await client.Http.PostAsync("/api/members/invites", new { email = "not-an-email" }, ct: ctx.Ct);
            Expect.EqualText("Enter a valid email address.", english.Json?["error"], "API error text after clearing the language");
        })
        .Test("arabic-rtl", "A signed-in user who picks Arabic gets right-to-left pages", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            try
            {
                (await client.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string> { ["language"] = "ar", ["returnUrl"] = "/dashboard" }, postTo: "/language", ct: ctx.Ct)).ExpectStatus(302);
                foreach (var path in new[] { "/dashboard", "/Members", "/settings/business-info" })
                {
                    var tag = PlatformHelpers.HtmlTag((await client.Http.GetAsync(path, ct: ctx.Ct)).ExpectOk().Body);
                    Expect.True(tag.Contains("lang=\"ar\"") && tag.Contains("dir=\"rtl\""), $"{path} isn't Arabic right-to-left: {tag}");
                }
            }
            finally
            {
                await client.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string> { ["language"] = "", ["returnUrl"] = "/dashboard" }, postTo: "/language", ct: ctx.Ct);
            }
        })
        .Build();
}

/// <summary>Changing a client's business type (platform-admin API): the preview, and the contact-stage round trip
/// clinic → general → clinic with clinic-only stages (doc-review retest of rule S3).</summary>
[Suite(15)]
public static class BusinessTypeSuite
{
    public static TestSuite Build() => new SuiteBuilder("business-type", "Business type change", "Preview and round trip of a business-type change with clinic-only stages.")
        .Test("round-trip", "Clinic → general → clinic moves contacts by rule S3 (same key, else the category's first stage) and pages follow at once", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            try
            {
                await BusinessTypeHelpers.ChangeAsync(ctx, client, "clinic");
                var stages = new[] { "medical_question", "surgery_booked", "consultation_booked", "qualified" };
                var contacts = new Dictionary<string, Guid>();
                foreach (var stage in stages)
                {
                    var id = await PlatformHelpers.IngestContactAsync(ctx, client, $"Auto stage {stage} {ctx.Tag}");
                    (await client.Http.PostAsync($"/api/contacts/{id}/stage", new { stageKey = stage }, ct: ctx.Ct)).ExpectStatus(200, 204);
                    contacts[stage] = id;
                }

                var expectedGeneral = new Dictionary<string, string>
                {
                    ["medical_question"] = "needs_human", ["surgery_booked"] = "customer", ["consultation_booked"] = "booked", ["qualified"] = "qualified",
                };
                var preview = (await ctx.Admin.GetAsync($"/api/platform-admin/clients/{client.ClientId}/business-type/preview?to=general", ct: ctx.Ct)).ExpectOk();
                var moves = preview.Array("moves");
                foreach (var (from, to) in expectedGeneral)
                {
                    var move = moves.FirstOrDefault(m => m?["fromKey"]?.ToString() == from) ?? throw new TestFailure($"The preview has no move for {from}.");
                    Expect.EqualText(to, move["toKey"], $"Preview: where {from} goes");
                    Expect.EqualText("1", move["count"], $"Preview: contacts in {from}");
                }
                Expect.EqualText("clinic", preview.Json?["fromCode"], "Preview changes nothing (still clinic)");

                await BusinessTypeHelpers.ChangeAsync(ctx, client, "general");
                foreach (var (from, to) in expectedGeneral) Expect.EqualText(to, await StageAsync(ctx, client, contacts[from]), $"After clinic → general, the contact from {from}");
                var customer = (await client.Http.GetAsync($"/dashboard/contacts/{contacts["surgery_booked"]}", ct: ctx.Ct)).ExpectOk();
                Expect.True(customer.Body.Contains("Customer", StringComparison.Ordinal), "The contact page doesn't show the general label 'Customer' right after the change.");

                await BusinessTypeHelpers.ChangeAsync(ctx, client, "clinic");
                var expectedBack = new Dictionary<string, string>
                {
                    // medical_question is lossy by design: it became needs_human, which exists in both types.
                    ["medical_question"] = "needs_human", ["surgery_booked"] = "surgery_booked", ["consultation_booked"] = "consultation_booked", ["qualified"] = "qualified",
                };
                foreach (var (from, to) in expectedBack) Expect.EqualText(to, await StageAsync(ctx, client, contacts[from]), $"After general → clinic, the contact first in {from}");
                var procedure = (await client.Http.GetAsync($"/dashboard/contacts/{contacts["surgery_booked"]}", ct: ctx.Ct)).ExpectOk();
                Expect.True(procedure.Body.Contains("Procedure booked", StringComparison.Ordinal), "The contact page doesn't show the clinic label 'Procedure booked' right after changing back.");
            }
            finally
            {
                await BusinessTypeHelpers.ChangeAsync(ctx, client, "general");
            }
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey)
        .Test("unknown-type", "Changing to an unknown business type is refused and changes nothing", async ctx =>
        {
            var client = await PlatformHelpers.SettingsClientAsync(ctx);
            var response = await ctx.Admin.PutAsync($"/api/platform-admin/clients/{client.ClientId}/business-type", new { businessTypeCode = "no-such-type" }, ctx.Ct);
            Expect.Equal(404, response.Status, "Change to 'no-such-type'");
            var preview = (await ctx.Admin.GetAsync($"/api/platform-admin/clients/{client.ClientId}/business-type/preview?to=clinic", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("general", preview.Json?["fromCode"], "Business type after the refused change");
        }, needs: Needs.Session | Needs.AdminKey)
        .Build();

    private static async Task<string> StageAsync(TestContext ctx, TestClient client, Guid contact) =>
        (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
}
