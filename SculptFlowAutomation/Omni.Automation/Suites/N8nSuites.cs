using System.Text.Json.Nodes;
using Omni.Automation.Core;

namespace Omni.Automation.Suites;

/// <summary>Engine settings (QA t9-n8n): which engine (n8n workflow or in-app code) answers contacts, writes benchmark
/// questions and syncs calendars. Read-only checks of the platform settings.</summary>
[Suite(70)]
public static class N8nEngineSettingsSuite
{
    private static readonly (string Section, string Key)[] Engines = [("Ai", "BotEngine"), ("Benchmark", "GeneratorEngine"), ("Calendars", "SyncEngine")];

    public static TestSuite Build() => new SuiteBuilder("n8n", "n8n engine settings", "The three n8n-or-code engine switches in the platform settings.")
        .Test("effective-code", "Engine settings: the AI, benchmark and calendar engines run on code (the n8n workflows don't work end to end)", async ctx =>
        {
            foreach (var (section, key) in Engines)
            {
                var setting = await N8nHelpers.SettingAsync(ctx, section, key);
                Expect.Equal("code", setting["effectiveValue"]?.ToString(), $"Effective {section}/{key}");
                var choices = setting["choices"]?.AsArray().Select(c => c?.ToString()).ToList() ?? [];
                Expect.True(choices.Count == 0 || (choices.Contains("n8n") && choices.Contains("code")), $"{section}/{key} offers {string.Join(", ", choices)}.");
            }
        }, needs: Needs.AdminKey)
        .Test("timeouts", "Engine settings: a benchmark generation nobody answers is given up after 5 minutes, a calendar sync after 10", async ctx =>
        {
            // QA t9-02 (30 minutes blocked new generations) and t9-03 (a sync nobody answered stayed pending forever).
            Expect.Equal("5", (await N8nHelpers.SettingAsync(ctx, "Benchmark", "GenerationTimeoutMinutes"))["defaultValue"]?.ToString(), "Benchmark/GenerationTimeoutMinutes default");
            Expect.Equal("10", (await N8nHelpers.SettingAsync(ctx, "Calendars", "SyncAnswerTimeoutMinutes"))["defaultValue"]?.ToString(), "Calendars/SyncAnswerTimeoutMinutes default");
        }, needs: Needs.AdminKey, finding: "t9-03")
        .Test("default-code", "Engine settings: the built-in default of each engine is code, so a fresh deploy doesn't start on n8n", async ctx =>
        {
            var defaults = new List<string>();
            foreach (var (section, key) in Engines)
            {
                defaults.Add($"{section}/{key} = {(await N8nHelpers.SettingAsync(ctx, section, key))["defaultValue"]}");
            }
            Expect.True(defaults.All(d => d.EndsWith("= code")),
                $"Defaults: {string.Join(", ", defaults)}. ConfigDefaults and seed-config.sql default to n8n, so a fresh database or a reset setting puts the engine on the n8n workflow, which doesn't answer (t9-01/02).");
        }, needs: Needs.AdminKey, finding: "t4-ai-04",
            description: "dr-a/dr-b: the n8n default (ConfigDefaults.cs, seed-config.sql) is the real bug behind t4-ai-04; the plan's fix is to default to code.")
        .Build();
}

/// <summary>The engines switched to n8n for one check each (Risk.Shared: every client is on n8n meanwhile), always switched
/// back in finally. t9-01: the bot never answers on n8n (the deployed workflow is the old SculptFlow one, per dr-b). t9-02: the
/// benchmark generator never calls back. Calendar sync: the documented n8n hand-off (dr-b on t9-03).</summary>
[Suite(71)]
public static class N8nEngineRunSuite
{
    private const Needs Ai = Needs.Session | Needs.AdminKey;

    public static TestSuite Build() => new SuiteBuilder("n8n-run", "n8n engines in action", "Each engine switched to n8n for one message, generation or sync, then restored.")
        .Test("bot-reply", "n8n AI engine: a test-chat message gets a reply", async ctx =>
        {
            var bots = await AiHelpers.AiBotsAsync(ctx);
            await N8nHelpers.WithSettingAsync(ctx, "Ai", "BotEngine", "n8n", async () =>
            {
                var session = await AiHelpers.SendTestAsync(ctx, bots.Client, bots.Support, "Hello! What can you help me with?");
                var transcript = await AiHelpers.AnsweredOrNullAsync(ctx, bots.Client, bots.Support, session, TimeSpan.FromSeconds(90));
                var answer = transcript is null ? [] : AiHelpers.AnswerLines(transcript);
                Expect.True(answer.Any(l => l.Role == "bot"),
                    transcript is null
                        ? "With Ai/BotEngine = n8n the test chat got no reply in 90 s: the deployed workflow calls the AI tools without clientId and X-Omni-Bot-Id (every tool answers 400 BOT_CONTEXT_MISSING) and posts its reply to the member-only send route."
                        : $"With Ai/BotEngine = n8n the bot didn't reply: {AiHelpers.Describe(answer)}.");
            });
        }, needs: Ai, risk: Risk.Shared | Risk.External | Risk.Ai | Risk.Billing, finding: "t9-01")
        .Test("benchmark", "n8n benchmark generator: a generation calls back with questions", async ctx =>
        {
            var client = await AiHelpers.AiClientAsync(ctx);
            await AiHelpers.AiKnowledgeAsync(ctx);
            var basePath = $"/api/platform-admin/knowledge-benchmark/clients/{client.ClientId}";
            await N8nHelpers.WithSettingAsync(ctx, "Benchmark", "GeneratorEngine", "n8n", async () =>
            {
                var started = (await ctx.Admin.PostAsync($"{basePath}/cases/generate", null, ct: ctx.Ct)).ExpectStatus(200, 202);
                var id = started.Str("generationId");
                string status = started.Str("status");
                try
                {
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
                    while (status == "pending" && DateTimeOffset.UtcNow < deadline)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ctx.Ct);
                        var json = (await ctx.Admin.GetAsync($"{basePath}/generations/{id}", ct: ctx.Ct)).ExpectOk().RequireJson();
                        status = (json["summary"] ?? json)["status"]?.ToString() ?? "?";
                    }
                    Expect.Equal("completed", status == "pending" ? "pending after 90 s (no callback from n8n)" : status, "Generation status on n8n");
                }
                finally
                {
                    // A pending generation blocks the next one for 30 minutes: stop it.
                    if (status == "pending") await ctx.Admin.PostAsync($"{basePath}/generations/{id}/cancel", null, ct: CancellationToken.None);
                }
            });
        }, needs: Ai, risk: Risk.Shared | Risk.External | Risk.Ai | Risk.Billing, finding: "t9-02")
        .Test("calendar", "n8n calendar sync: a new booking on a connected calendar is handed to the workflow (pending, connection healthy)", async ctx =>
        {
            // dr-b on t9-03: no n8n calendar workflow reports provider errors, so with a fake token "pending, still healthy" is the
            // documented n8n result. What this checks is that the hand-off is accepted (no webhook warning) and recorded.
            var client = await ctx.RegisterClientAsync("n8n-cal");
            await ctx.SetPlanAsync(client, "inbox-bookings");
            var integration = Guid.NewGuid();
            await ctx.Db.ExecuteForClientAsync(client.ClientId, """
                insert into scheduling.calendar_integrations (id, client_id, provider, status, account_display_name, selected_calendar_id, selected_calendar_name,
                    sync_enabled, is_healthy, access_token, token_expires_at, created_at, updated_at)
                values (@integration, @clientId, 'google', 'connected', 'automation@omni-automation.test', 'primary', 'Automation calendar',
                    true, true, @token, now() + interval '1 day', now(), now())
                """, new { integration, token = $"auto-fake-token-{Guid.NewGuid():N}" }, ctx.Ct);
            await AiHelpers.DropCacheAsync(ctx, "scheduling:calendars:" + client.ClientId.ToString("N"));
            var contact = await ctx.Fixtures.NewContactAsync("calendar", client);
            var start = await AiHelpers.SlotOnDayAsync(ctx, client, 2, 3);

            await N8nHelpers.WithSettingAsync(ctx, "Calendars", "SyncEngine", "n8n", async () =>
            {
                var since = await AiHelpers.DbNowAsync(ctx);
                var booking = (await client.Http.PostAsync("/api/bookings", new
                {
                    clientId = client.ClientId, contactId = contact, serviceId = client.BaseServiceId, bookingType = "standard", scheduledStart = start,
                    notes = "Omni.Automation calendar sync on n8n",
                }, ct: ctx.Ct)).ExpectStatus(200, 201).Id();
                var sync = await ctx.EventuallyAsync(async () => (await ctx.Db.QueryAsync("""
                    select status, last_request_id, last_operation from scheduling.booking_calendar_syncs where booking_id = @booking and calendar_integration_id = @integration
                    """, new { booking, integration }, ctx.Ct)).FirstOrDefault(), "the booking's calendar sync row", TimeSpan.FromSeconds(30));
                await Task.Delay(TimeSpan.FromSeconds(5), ctx.Ct);
                Expect.True(sync["last_request_id"] is not null && sync["last_operation"]?.ToString() == "create",
                    $"The sync row has no create request (status {sync["status"]}, operation {sync["last_operation"]}).");
                var warnings = await ctx.Db.QueryAsync("""
                    select left(message, 200) as message from activity.app_logs
                    where occurred_at >= @since and message ilike '%calendar webhook%' and message ilike '%' || @integration || '%'
                    """, new { since, integration = integration.ToString() }, ctx.Ct);
                Expect.True(warnings.Count == 0, $"The n8n calendar webhook refused the hand-off: {string.Join(" | ", warnings.Select(w => w["message"]))}.");
                var healthy = await ctx.Db.ScalarAsync<bool>("select is_healthy from scheduling.calendar_integrations where id = @integration", new { integration }, ctx.Ct);
                Expect.True(healthy, "The connection was marked unhealthy right after the hand-off to n8n.");
            });
        }, needs: Ai | Needs.Database | Needs.IngestKey, risk: Risk.Shared | Risk.External | Risk.Billing)
        .Test("calendar-no-answer", "Calendar sync nobody answers: marked failed, the connection unhealthy and the client told", async ctx =>
        {
            // QA t9-03: with n8n not answering, the sync stayed pending forever and the connection looked healthy.
            var (client, integration, contact, start) = await FakeCalendarAsync(ctx, "n8n-cal-wd");
            await N8nHelpers.WithSettingAsync(ctx, "Calendars", "SyncAnswerTimeoutMinutes", "2", () =>
                N8nHelpers.WithSettingAsync(ctx, "Calendars", "SyncEngine", "n8n", async () =>
                {
                    var booking = await BookAsync(ctx, client, contact, start, "Omni.Automation unanswered calendar sync");
                    // The check runs every 2 minutes and fails what waited longer than the setting.
                    var row = await ctx.EventuallyAsync(async () => (await ctx.Db.QueryAsync("""
                        select s.status, i.is_healthy, i.last_problem_message from scheduling.booking_calendar_syncs s
                        join scheduling.calendar_integrations i on i.id = s.calendar_integration_id
                        where s.booking_id = @booking and s.calendar_integration_id = @integration and s.status = 'failed'
                        """, new { booking, integration }, ctx.Ct)).FirstOrDefault(), "the unanswered sync to be marked failed", TimeSpan.FromMinutes(6));
                    Expect.True(row["is_healthy"] is false, $"The sync failed but the connection still looks healthy ({row["last_problem_message"]}).");
                    var notices = await ctx.Db.ScalarAsync<long>(
                        "select count(*) from activity.notifications where client_id = @clientId and type = 'INTEGRATION_UNHEALTHY'", new { clientId = client.ClientId }, ctx.Ct);
                    Expect.True(notices >= 1, "The client wasn't told that the calendar sync got no answer.");
                }));
        }, needs: Ai | Needs.Database | Needs.IngestKey, risk: Risk.Shared | Risk.External | Risk.Billing, finding: "t9-03")
        .Test("calendar-cancel-no-event", "Calendar sync (code): cancelling a booking whose event was never made calls nobody and keeps the real problem", async ctx =>
        {
            // QA t9-06: the cancel was sent with no event id, Google answered 404, and that hid the 401 telling the client to reconnect.
            var (client, integration, contact, start) = await FakeCalendarAsync(ctx, "code-cal-cancel");
            await N8nHelpers.WithSettingAsync(ctx, "Calendars", "SyncEngine", "code", async () =>
            {
                var booking = await BookAsync(ctx, client, contact, start, "Omni.Automation calendar cancel without an event");
                var created = await ctx.EventuallyAsync(async () => (await ctx.Db.QueryAsync("""
                    select status, external_event_id from scheduling.booking_calendar_syncs
                    where booking_id = @booking and calendar_integration_id = @integration and status = 'failed'
                    """, new { booking, integration }, ctx.Ct)).FirstOrDefault(), "the create to fail on the fake token", TimeSpan.FromSeconds(60));
                Expect.True(created["external_event_id"] is null, "The failed create stored an event id.");
                var problem = await ctx.Db.ScalarAsync<string>("select last_problem_message from scheduling.calendar_integrations where id = @integration", new { integration }, ctx.Ct);

                (await OpsHelpers.SetStatusAsync(ctx, client, booking, "canceled")).ExpectOk();
                var canceled = await ctx.EventuallyAsync(async () => await ctx.Db.ScalarAsync<string>(
                    "select status from scheduling.booking_calendar_syncs where booking_id = @booking and calendar_integration_id = @integration",
                    new { booking, integration }, ctx.Ct) is "canceled" ? "canceled" : null, "the sync to be recorded as canceled", TimeSpan.FromSeconds(30));
                var after = await ctx.Db.ScalarAsync<string>("select last_problem_message from scheduling.calendar_integrations where id = @integration", new { integration }, ctx.Ct);
                Expect.True(after == problem && !(after ?? "").Contains("404"),
                    $"The connection's problem changed from \"{problem}\" to \"{after}\" by a cancel that had nothing to remove.");
            });
        }, needs: Ai | Needs.Database | Needs.IngestKey, risk: Risk.Shared | Risk.External | Risk.Billing, finding: "t9-06")
        .Build();

    /// <summary>A client on a plan with bookings, a Google connection with a fake token, a contact and a free slot.</summary>
    private static async Task<(TestClient Client, Guid Integration, Guid Contact, string Start)> FakeCalendarAsync(TestContext ctx, string label)
    {
        var client = await ctx.RegisterClientAsync(label);
        await ctx.SetPlanAsync(client, "inbox-bookings");
        var integration = Guid.NewGuid();
        await ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into scheduling.calendar_integrations (id, client_id, provider, status, account_display_name, selected_calendar_id, selected_calendar_name,
                sync_enabled, is_healthy, access_token, token_expires_at, created_at, updated_at)
            values (@integration, @clientId, 'google', 'connected', 'automation@omni-automation.test', 'primary', 'Automation calendar',
                true, true, @token, now() + interval '1 day', now(), now())
            """, new { integration, token = $"auto-fake-token-{Guid.NewGuid():N}" }, ctx.Ct);
        await AiHelpers.DropCacheAsync(ctx, "scheduling:calendars:" + client.ClientId.ToString("N"));
        var contact = await ctx.Fixtures.NewContactAsync("calendar", client);
        var start = await AiHelpers.SlotOnDayAsync(ctx, client, 2, 3);
        return (client, integration, contact, start);
    }

    private static async Task<Guid> BookAsync(TestContext ctx, TestClient client, Guid contact, string start, string notes) =>
        (await client.Http.PostAsync("/api/bookings", new
        {
            clientId = client.ClientId, contactId = contact, serviceId = client.BaseServiceId, bookingType = "standard", scheduledStart = start, notes,
        }, ct: ctx.Ct)).ExpectStatus(200, 201).Id();
}

/// <summary>Platform settings through the admin API, and a temporary switch that is always undone.</summary>
internal static class N8nHelpers
{
    public static async Task<JsonNode> SettingAsync(TestContext ctx, string section, string key)
    {
        var all = (await ctx.Admin.GetAsync("/api/platform-admin/settings", ct: ctx.Ct)).ExpectOk().Array();
        return all.FirstOrDefault(s => s?["section"]?.ToString() == section && s?["key"]?.ToString() == key)
            ?? throw new TestFailure($"The platform settings have no {section}/{key}.");
    }

    /// <summary>Sets <paramref name="section"/>/<paramref name="key"/> to <paramref name="value"/>, runs <paramref name="body"/>, then
    /// puts back exactly what was there (the stored value, or no row = the default) even when the body fails.</summary>
    public static async Task WithSettingAsync(TestContext ctx, string section, string key, string value, Func<Task> body)
    {
        var before = await SettingAsync(ctx, section, key);
        var stored = before["storedValue"]?.ToString();
        var effective = before["effectiveValue"]?.ToString();
        var path = $"/api/platform-admin/settings/{section}/{key}";
        try
        {
            (await ctx.Admin.PutAsync(path, new { value, note = $"Omni.Automation {ctx.Tag}: temporary switch for a test" }, ctx.Ct)).ExpectOk();
            ctx.Log($"Switched {section}/{key} from {effective} to {value}.");
            await body();
        }
        finally
        {
            var restore = stored is null
                ? await ctx.Admin.DeleteAsync(path, CancellationToken.None)
                : await ctx.Admin.PutAsync(path, new { value = stored, note = before["note"]?.ToString() ?? $"Omni.Automation {ctx.Tag}: restored" }, CancellationToken.None);
            var now = (await SettingAsync(ctx, section, key))["effectiveValue"]?.ToString();
            ctx.Log($"Restored {section}/{key} to {now} (status {restore.Status}).");
            if (now != effective) throw new TestFailure($"Couldn't restore {section}/{key}: it is {now}, it was {effective}. Fix it in the admin portal's Configuration page.");
        }
    }
}
