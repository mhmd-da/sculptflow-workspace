using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>
/// Shared steps of the billing suites: the platform-admin billing API (/api/platform-admin/billing/*), wallet and
/// ledger reads, DB time travel of a subscription period, and the AI-reply charge path (a bot test session answered
/// through the ingest reply route, so no model runs). Every money test works on its own freshly registered client.
/// </summary>
internal static class BillingHelpers
{
    public const string Base = "/api/platform-admin/billing";

    /// <summary>A fresh Idempotency-Key header for one intended money operation.</summary>
    public static Dictionary<string, string> Key(TestContext ctx, string label) =>
        new() { ["Idempotency-Key"] = KeyValue(ctx, label) };

    public static string KeyValue(TestContext ctx, string label) => $"auto-{ctx.Tag}-{label}-{Guid.NewGuid():N}";

    /// <summary>A private test client for a test that moves money or changes the plan.</summary>
    public static Task<TestClient> NewClientAsync(TestContext ctx, string label) => ctx.RegisterClientAsync(label);

    /// <summary>A client this file only reads from (never plan-changed or topped up), shared by the run.</summary>
    public static Task<TestClient> ReadOnlyClientAsync(TestContext ctx) => ctx.NamedClientAsync("bill-ro");

    public static Task<ApiResponse> TopUpAsync(TestContext ctx, TestClient client, object body, Dictionary<string, string>? headers) =>
        ctx.Admin.PostAsync($"{Base}/clients/{client.ClientId}/wallet/top-ups", body, headers, ctx.Ct);

    /// <summary>Adds money to the wallet and fails unless it was accepted.</summary>
    public static async Task TopUpOkAsync(TestContext ctx, TestClient client, decimal amount) =>
        (await TopUpAsync(ctx, client, new { amount, reference = $"auto-{ctx.Tag}", reason = "Omni.Automation top-up" }, Key(ctx, "top")))
        .ExpectOk();

    public static Task<ApiResponse> AdjustAsync(TestContext ctx, TestClient client, object body, Dictionary<string, string>? headers) =>
        ctx.Admin.PostAsync($"{Base}/clients/{client.ClientId}/wallet/adjustments", body, headers, ctx.Ct);

    public static Task<ApiResponse> StartPlanAsync(TestContext ctx, TestClient client, string planCode, Dictionary<string, string>? headers,
        bool chargeFirstPeriod = true) =>
        ctx.Admin.PostAsync($"{Base}/clients/{client.ClientId}/subscription",
            new { planCode, chargeFirstPeriod, reason = $"Omni.Automation {ctx.Tag}" }, headers, ctx.Ct);

    public static async Task<string> RenewAsync(TestContext ctx, TestClient client) =>
        (await ctx.Admin.PostAsync($"{Base}/clients/{client.ClientId}/subscription/renew", ct: ctx.Ct)).ExpectOk().Str("outcome");

    /// <summary>The admin overview of a client (summary, subscription, reconciliation).</summary>
    public static async Task<JsonNode> OverviewAsync(TestContext ctx, TestClient client) =>
        (await ctx.Admin.GetAsync($"{Base}/clients/{client.ClientId}", ct: ctx.Ct)).ExpectOk().RequireJson();

    public static async Task<JsonNode> SummaryAsync(TestContext ctx, TestClient client) =>
        (await OverviewAsync(ctx, client))["summary"] ?? throw new TestFailure("The admin overview has no summary.");

    public static async Task<decimal> WalletAsync(TestContext ctx, TestClient client) => D((await SummaryAsync(ctx, client))["walletBalance"]);

    public static async Task<decimal> CreditAsync(TestContext ctx, TestClient client) => D((await SummaryAsync(ctx, client))["includedCreditRemaining"]);

    /// <summary>The client's ledger rows, newest first.</summary>
    public static async Task<List<JsonNode>> LedgerAsync(TestContext ctx, TestClient client) =>
        (await ctx.Admin.GetAsync($"{Base}/clients/{client.ClientId}/ledger?pageSize=200", ct: ctx.Ct)).ExpectOk().Array("items")
        .Where(n => n is not null).Select(n => n!).ToList();

    public static async Task<List<JsonNode>> LedgerOfTypeAsync(TestContext ctx, TestClient client, string entryType) =>
        (await LedgerAsync(ctx, client)).Where(r => r["entryType"]?.ToString() == entryType).ToList();

    public static async Task<List<JsonNode>> UsageAsync(TestContext ctx, TestClient client) =>
        (await ctx.Admin.GetAsync($"{Base}/clients/{client.ClientId}/usage?pageSize=200", ct: ctx.Ct)).ExpectOk().Array("items")
        .Where(n => n is not null).Select(n => n!).ToList();

    /// <summary>Fails unless the cached balances equal the ledger sums (and reserved equals the open reservations).</summary>
    public static async Task ExpectBalancedAsync(TestContext ctx, TestClient client)
    {
        var rec = (await ctx.Admin.GetAsync($"{Base}/clients/{client.ClientId}/reconciliation", ct: ctx.Ct)).ExpectOk();
        Expect.True(rec.Prop("isBalanced").GetValue<bool>(), $"Reconciliation is not balanced for {client.Name}.{rec.Snippet()}");
    }

    /// <summary>Moves the client's subscription in time with a DB write (e.g. "current_period_end = now() - interval '1 minute'"),
    /// then drops its cached entitlements so the app sees the change.</summary>
    public static async Task TimeTravelAsync(TestContext ctx, TestClient client, string set)
    {
        var rows = await ctx.Db.ExecuteForClientAsync(client.ClientId, $"update billing.subscriptions set {set} where client_id = @clientId", ct: ctx.Ct);
        Expect.Equal(1, rows, "Subscription rows moved in time");
        await EvictEntitlementsAsync(ctx, client);
    }

    /// <summary>Drops the client's cached entitlements (404 = nothing was cached, which is fine).</summary>
    public static async Task EvictEntitlementsAsync(TestContext ctx, TestClient client)
    {
        var key = Uri.EscapeDataString($"billing:entitlements:{client.ClientId:N}");
        var r = await ctx.Admin.DeleteAsync($"/api/platform-admin/cache/key?key={key}", ctx.Ct);
        if (r.Status is not (200 or 204 or 404)) throw new TestFailure($"Evicting the entitlements cache failed (status {r.Status}).{r.Snippet(200)}");
    }

    /// <summary>Puts the period end one minute in the past (the period started one month/<paramref name="length"/> earlier).</summary>
    public static Task EndPeriodNowAsync(TestContext ctx, TestClient client, string length = "1 month") =>
        TimeTravelAsync(ctx, client, $"current_period_start = now() - interval '{length} 1 minute', current_period_end = now() - interval '1 minute'");

    public static decimal D(JsonNode? node) =>
        node is null ? 0m : decimal.Parse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string Ent(JsonNode summary, string key) =>
        summary["entitlements"]?.AsArray().FirstOrDefault(e => e?["key"]?.ToString() == key)?["value"]?.ToString() ?? "(missing)";

    // ---- AI reply charging without running the model ---------------------------------------------------------------

    /// <summary>Creates a bot from the "support" template through the Bots page and returns its id.</summary>
    public static async Task<Guid> CreateBotAsync(TestContext ctx, TestClient client)
    {
        var response = await client.Http.SubmitFormAsync("/Bots", new Dictionary<string, string> { ["template"] = "support" }, handler: "Create", ct: ctx.Ct);
        var match = System.Text.RegularExpressions.Regex.Match(response.Location ?? string.Empty, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (response.Status != 302 || !match.Success)
        {
            throw new TestFailure($"Creating a bot on the Bots page failed (status {response.Status}, location {response.Location ?? "none"}): {TestContext.PageError(response.Body) ?? "no error shown"}.");
        }
        return Guid.Parse(match.Value);
    }

    /// <summary>A bot test session inserted in the DB (starting one through the UI would run the model).</summary>
    public static async Task<Guid> NewTestSessionAsync(TestContext ctx, TestClient client, Guid botId)
    {
        var id = Guid.NewGuid();
        await ctx.Db.ExecuteForClientAsync(client.ClientId,
            "insert into crm.bot_test_sessions (id, client_id, bot_id) values (@id, @clientId, @botId)", new { id, botId }, ctx.Ct);
        return id;
    }

    /// <summary>Posts a bot reply into a test session the way the n8n workflow does (ingest key + bot/test headers).
    /// It is charged as ai_reply like a live reply; no model runs.</summary>
    public static Task<ApiResponse> PostTestReplyAsync(TestContext ctx, TestClient client, Guid botId, Guid sessionId, string content) =>
        ctx.Ingest.PostAsync($"/api/ai/conversations/{sessionId}/reply?clientId={client.ClientId}", new { content },
            new Dictionary<string, string> { ["X-Omni-Bot-Id"] = botId.ToString(), ["X-Omni-Test-Session"] = sessionId.ToString() }, ctx.Ct);

    /// <summary>A client on ai-pro (20 included credit, not charged) with a bot and a test session, ready to be charged for replies.</summary>
    public static async Task<(TestClient Client, Guid Bot, Guid Session)> AiReplySetupAsync(TestContext ctx, string label)
    {
        var client = await NewClientAsync(ctx, label);
        await ctx.SetPlanAsync(client, "ai-pro");
        var bot = await CreateBotAsync(ctx, client);
        var session = await NewTestSessionAsync(ctx, client, bot);
        return (client, bot, session);
    }

    /// <summary>The settled/failed ai_reply usage rows of the client.</summary>
    public static async Task<List<JsonNode>> AiReplyUsageAsync(TestContext ctx, TestClient client) =>
        (await UsageAsync(ctx, client)).Where(u => u["eventType"]?.ToString() == "ai_reply").ToList();
}

