using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(33)]
public static class OpsRemindersSuite
{
    private const string Label = "ops-remind";

    private static async Task<TestClient> RemindersOnAsync(TestContext ctx)
    {
        var client = await OpsHelpers.ClientAsync(ctx, Label);
        var r = await client.Http.SubmitFormAsync("/settings/reminders", new Dictionary<string, string>
        {
            ["Enabled"] = "true", ["FirstHoursBefore"] = "2", ["SecondHoursBefore"] = "1",
        }, ct: ctx.Ct);
        if (r.Status != 302) throw new TestFailure($"Saving reminder settings answered {r.Status}: {TestContext.PageError(r.Body) ?? "no error shown"}.");
        return client;
    }

    /// <summary>A booking 90 minutes ahead (first reminder, 2 h before, is due now). <paramref name="madeEarlier"/> moves its
    /// creation two days back (DB) so it doesn't count as made inside the reminder window.</summary>
    private static async Task<Guid> DueBookingAsync(TestContext ctx, TestClient client, Guid contact, bool madeEarlier)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(90);
        var id = (await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddMinutes(30))).ExpectStatus(201).Id();
        if (madeEarlier)
        {
            await ctx.Db.ExecuteForClientAsync(client.ClientId,
                "update scheduling.bookings set created_at = now() - interval '2 days' where id = @id and client_id = @clientId", new { id }, ctx.Ct);
        }
        return id;
    }

    private static Task<List<Dictionary<string, object?>>> RowsAsync(TestContext ctx, Guid booking) =>
        ctx.Db.QueryAsync("select kind, status, reason from scheduling.booking_reminders where booking_id = @b", new { b = booking }, ctx.Ct);

    private static Task<List<Dictionary<string, object?>>> FirstRowAsync(TestContext ctx, Guid booking) =>
        ctx.EventuallyAsync(async () =>
        {
            var rows = await RowsAsync(ctx, booking);
            return rows.Any(r => (string?)r["kind"] == "first" && (string?)r["status"] != "sending") ? rows : null;
        }, $"the reminder worker to handle booking {booking}", TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5));

    public static TestSuite Build() => new SuiteBuilder("ops-reminders", "Operations: booking reminders",
            "Reminder settings and the BookingReminderWorker (runs every minute) on a client with no channel connected, so nothing is ever sent.")
        .Test("settings", "Reminders: settings save through the form", async ctx =>
        {
            var client = await RemindersOnAsync(ctx);
            var row = (await ctx.Db.QueryAsync("select is_enabled, first_hours_before, second_hours_before from scheduling.reminder_settings where client_id = @c",
                new { c = client.ClientId }, ctx.Ct)).Single();
            Expect.Equal(true, row["is_enabled"], "Reminders enabled");
            Expect.Equal(2, row["first_hours_before"], "First reminder hours");
            Expect.Equal(1, row["second_hours_before"], "Second reminder hours");
            (await client.Http.GetAsync("/settings/reminders", ct: ctx.Ct)).ExpectOk();
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing)
        .Test("settings-validation", "Reminders: 200 hours before is refused", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            var r = await client.Http.SubmitFormAsync("/settings/reminders", new Dictionary<string, string>
            {
                ["Enabled"] = "true", ["FirstHoursBefore"] = "200", ["SecondHoursBefore"] = "1",
            }, ct: ctx.Ct);
            Expect.True(r.Status == 200 && TestContext.PageError(r.Body) is not null, $"Saving 200 hours answered {r.Status} without an error.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("rows", "Reminders: the worker writes one skip row per due booking with the reason (no phone, inside window, no channel)", async ctx =>
        {
            var client = await RemindersOnAsync(ctx);
            var withPhone = await DueBookingAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "rem-phone"), madeEarlier: true);
            var noPhone = await DueBookingAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "rem-nophone", phone: false), madeEarlier: true);
            var inside = await DueBookingAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "rem-inside"), madeEarlier: false);
            ctx.Log("Waiting for the BookingReminderWorker (every minute)…");
            var phoneRows = await FirstRowAsync(ctx, withPhone);
            var noPhoneRows = await FirstRowAsync(ctx, noPhone);
            var insideRows = await FirstRowAsync(ctx, inside);
            string Reason(List<Dictionary<string, object?>> rows) => $"{rows.First(r => (string?)r["kind"] == "first")["status"]}/{rows.First(r => (string?)r["kind"] == "first")["reason"]}";
            Expect.True(Reason(phoneRows).StartsWith("skipped/"), $"With no channel the reminder should be skipped, got {Reason(phoneRows)}.");
            Expect.Equal("skipped/no_conversation", Reason(noPhoneRows), "Reminder for a contact without a phone");
            Expect.Equal("skipped/made_inside_window", Reason(insideRows), "Reminder for a booking made inside the window");
            foreach (var booking in new[] { withPhone, noPhone, inside })
            {
                var firsts = (await RowsAsync(ctx, booking)).Count(r => (string?)r["kind"] == "first");
                Expect.Equal(1, firsts, $"First-reminder rows for booking {booking}");
            }
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, description: "Slow: waits up to 90 s for the worker (runs every minute).")
        .Test("no-side-effects", "Reminders: a skipped reminder doesn't create an empty WhatsApp conversation", async ctx =>
        {
            var client = await RemindersOnAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "rem-side");
            var booking = await DueBookingAsync(ctx, client, contact, madeEarlier: true);
            await FirstRowAsync(ctx, booking);
            var empty = await ctx.Db.ScalarAsync<long>("""
                select count(*) from crm.conversations cv
                where cv.contact_id = @c and not exists (select 1 from crm.messages m where m.conversation_id = cv.id)
                """, new { c = contact }, ctx.Ct);
            Expect.True(empty == 0, $"Skipping the reminder (no channel connected) created {empty} empty conversation(s) for the contact; they show up as \"waiting\" on the dashboard.");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-06", description: "Slow: waits up to 90 s for the worker (runs every minute).")
        .Build();
}

[Suite(34)]
public static class OpsCalendarSuite
{
    private static async Task<ApiResponse> ConnectAsync(TestContext ctx, TestClient client, string provider) =>
        await client.Http.GetAsync($"/calendar-oauth/{provider}/connect", ct: ctx.Ct);

    private static Task<ApiResponse> CallbackAsync(TestContext ctx, TestClient client, string provider, string query) =>
        client.Http.GetAsync($"/calendar-oauth/{provider}/callback{query}", ct: ctx.Ct);

    public static TestSuite Build() => new SuiteBuilder("ops-calendar", "Operations: calendar integrations (no real account)",
            "Connect redirects, forged OAuth callbacks and the n8n sync-callback, without ever completing an OAuth sign-in.")
        .Test("list", "Calendars: the list answers for a bookings plan; nothing is connected", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal2");
            var rows = (await client.Http.GetAsync("/api/calendar-integrations", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(rows.All(r => r?["status"]?.ToString() != "connected"), "A fresh client shows a connected calendar.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("google-connect", "Calendars: Google connect redirects to Google's consent screen with a client id and state", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal");
            var r = await ConnectAsync(ctx, client, "google");
            Expect.True(r.Status == 302 && (r.Location ?? "").StartsWith("https://accounts.google.com/", StringComparison.OrdinalIgnoreCase),
                $"Google connect answered {r.Status} to {r.Location}.");
            Expect.True(!string.IsNullOrEmpty(OpsHelpers.QueryValue(r.Location!, "client_id")), "The Google consent URL has no client_id.");
            Expect.True(!string.IsNullOrEmpty(OpsHelpers.QueryValue(r.Location!, "state")), "The Google consent URL has no state.");
            Expect.EqualText("pending", (await OpsHelpers.CalendarRowAsync(ctx, client, "google"))?["status"], "Google status after starting connect");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("unknown-provider", "Calendars: an unknown provider goes back to the settings page", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal2");
            var r = await ConnectAsync(ctx, client, "yahoo");
            Expect.True(r.Status == 302 && (r.Location ?? "").Contains("/settings/calendar-integrations", StringComparison.OrdinalIgnoreCase),
                $"Connecting \"yahoo\" answered {r.Status} to {r.Location}.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("outlook-not-configured", "Calendars: Outlook connect never sends the user to Microsoft with an empty client id", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal2");
            var r = await ConnectAsync(ctx, client, "outlook");
            var location = r.Location ?? "";
            var toMicrosoft = location.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);
            Expect.True(!toMicrosoft || !string.IsNullOrEmpty(OpsHelpers.QueryValue(location, "client_id")),
                "Outlook connect redirects to Microsoft with client_id= empty (Outlook isn't configured): the user lands on a Microsoft error page.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-13")
        .Test("tampered-state", "Calendars: a callback with a tampered state never connects", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal");
            await ConnectAsync(ctx, client, "google");
            var r = await CallbackAsync(ctx, client, "google", "?state=tampered&code=abc");
            Expect.True(r.Status == 302, $"The forged callback answered {r.Status}.");
            Expect.True((await OpsHelpers.CalendarRowAsync(ctx, client, "google"))?["status"]?.ToString() != "connected", "A forged callback connected the calendar.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("other-clients-state", "Calendars: another client's OAuth state can't be replayed", async ctx =>
        {
            var owner = await OpsHelpers.ClientAsync(ctx, "ops-cal");
            var attacker = await OpsHelpers.ClientAsync(ctx, "ops-cal3");
            var connect = await ConnectAsync(ctx, owner, "google");
            var state = OpsHelpers.QueryValue(connect.Location ?? "", "state") ?? throw new TestFailure("No state in the Google consent URL.");
            (await CallbackAsync(ctx, attacker, "google", $"?state={Uri.EscapeDataString(state)}&code=abc")).ExpectStatus(302);
            Expect.True((await OpsHelpers.CalendarRowAsync(ctx, attacker, "google"))?["status"]?.ToString() != "connected", "Another client's state connected the attacker.");
            Expect.EqualText("pending", (await OpsHelpers.CalendarRowAsync(ctx, owner, "google"))?["status"], "The owner's pending connection after the replay");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("stray-callback", "Calendars: a stray callback with nothing pending doesn't flip the calendar to \"error\"", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal4");
            (await CallbackAsync(ctx, client, "google", "")).ExpectStatus(302);
            (await CallbackAsync(ctx, client, "google", "?state=tampered&code=abc")).ExpectStatus(302);
            var status = (await OpsHelpers.CalendarRowAsync(ctx, client, "google"))?["status"]?.ToString() ?? "(no row)";
            Expect.True(status != "error", "An unsolicited /calendar-oauth/google/callback (no connect pending) set the Google calendar to \"error\" (\"Action required\").");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-14")
        .Test("provider-error-escaped", "Calendars: a provider error with HTML is stored as text and shown escaped", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal5");
            var connect = await ConnectAsync(ctx, client, "google");
            var state = OpsHelpers.QueryValue(connect.Location ?? "", "state") ?? throw new TestFailure("No state in the Google consent URL.");
            const string description = "<script>alert('cal')</script>";
            (await CallbackAsync(ctx, client, "google", $"?state={Uri.EscapeDataString(state)}&error=access_denied&error_description={Uri.EscapeDataString(description)}"))
                .ExpectStatus(302);
            var row = await OpsHelpers.CalendarRowAsync(ctx, client, "google");
            Expect.EqualText("error", row?["status"], "Status after the provider refused");
            var page = (await client.Http.GetAsync("/settings/calendar-integrations", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains(description), "The calendar settings page renders the provider's error description as raw HTML.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("not-connected-actions", "Calendars: select, sync, refresh and disconnect on a calendar that isn't connected don't crash", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal2");
            var calls = new (string Path, object? Body)[]
            {
                ("/api/calendar-integrations/google/select-calendar", new { calendarId = "primary" }),
                ("/api/calendar-integrations/google/sync-enabled", new { enabled = true }),
                ("/api/calendar-integrations/google/refresh-calendars", null),
                ("/api/calendar-integrations/outlook/disconnect", null),
                ("/api/calendar-integrations/yahoo/disconnect", null),
            };
            foreach (var (path, body) in calls)
            {
                var r = await client.Http.PostAsync(path, body ?? new { }, ct: ctx.Ct);
                Expect.True(r.Status < 500, $"POST {path} on a calendar that isn't connected answered {r.Status}.{r.Snippet(200)}");
            }
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("trial-connect", "Calendars: on a plan without bookings, connect goes back with an error and nothing is pending", async ctx =>
        {
            var client = await ctx.NamedClientAsync("ops-trial");
            var r = await ConnectAsync(ctx, client, "google");
            Expect.True(r.Status == 302 && (r.Location ?? "").Contains("/settings/calendar-integrations", StringComparison.OrdinalIgnoreCase),
                $"Google connect on the trial answered {r.Status} to {r.Location}.");
            var pending = await ctx.Db.ScalarAsync<long>("select count(*) from scheduling.calendar_integrations where client_id = @c and status = 'pending'",
                new { c = client.ClientId }, ctx.Ct);
            Expect.Equal(0L, pending, "Pending calendar connections on the trial");
        }, needs: Needs.Session | Needs.Database)
        .Test("sync-callback-key", "Sync callback: no key or a wrong key is 401", async ctx =>
        {
            var body = new { clientId = Guid.NewGuid(), bookingId = Guid.NewGuid(), calendarIntegrationId = Guid.NewGuid(), requestId = Guid.NewGuid(), success = false, errorMessage = "x" };
            (await ctx.Anonymous.PostAsync("/api/calendar-integrations/sync-callback", body, ct: ctx.Ct)).ExpectStatus(401);
            var wrong = ctx.NewHttp(new Dictionary<string, string> { ["X-Ingest-Key"] = "auto-wrong-key" });
            (await wrong.PostAsync("/api/calendar-integrations/sync-callback", body, ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Test("sync-callback-unknown", "Sync callback: unknown ids are accepted and change nothing; a malformed body is 400", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, "ops-cal2");
            var body = new { clientId = client.ClientId, bookingId = Guid.NewGuid(), calendarIntegrationId = Guid.NewGuid(), requestId = Guid.NewGuid(), success = false, errorMessage = "Automation" };
            (await ctx.Ingest.PostAsync("/api/calendar-integrations/sync-callback", body, ct: ctx.Ct)).ExpectOk();
            var rows = (await client.Http.GetAsync("/api/calendar-integrations", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(rows.All(r => r?["lastProblemMessage"]?.ToString() != "Automation"), "A callback for unknown ids changed a calendar.");
            (await ctx.Ingest.PostRawAsync("/api/calendar-integrations/sync-callback", "{\"bookingId\":\"zzz\"}", "application/json", ct: ctx.Ct)).ExpectStatus(400);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Build();
}

