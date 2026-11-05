using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(25)]
public static class BillingRatesSuite
{
    private static string RatesPath(string card) => $"{BillingHelpers.Base}/rate-cards/{card}/rates";

    /// <summary>A client with its own rate card (code auto-{tag}-{label}).</summary>
    private static async Task<(TestClient Client, string Card)> ClientCardAsync(TestContext ctx, string label)
    {
        var client = await BillingHelpers.NewClientAsync(ctx, label);
        var card = $"auto-{ctx.Tag}-{label}";
        (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/rate-cards", new { code = card, name = $"[Automation] {label} {ctx.Tag}", clientId = client.ClientId }, ct: ctx.Ct))
            .ExpectOk();
        return (client, card);
    }

    private static Task<ApiResponse> AddRateAsync(TestContext ctx, string card, object body) => ctx.Admin.PostAsync(RatesPath(card), body, ct: ctx.Ct);

    private static Task<ApiResponse> QuoteAsync(TestContext ctx, TestClient client, string eventType, DateTimeOffset? at = null) =>
        ctx.Admin.GetAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/quote?eventType={eventType}" +
            (at is null ? "" : $"&at={Uri.EscapeDataString(at.Value.ToString("o"))}"), ct: ctx.Ct);

    public static TestSuite Build() => new SuiteBuilder("billing-rates", "Billing: rate cards, rates and quotes",
            "Client-specific rate cards (deleted with the test client), rate versions, validation, and which card prices a quote.")
        .Test("default-quote", "Quote: a client without its own card is priced by the default card (ai_reply)", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var q = (await QuoteAsync(ctx, client, "ai_reply")).ExpectOk();
            Expect.EqualText("default", q.Prop("rateCardCode"), "Card pricing ai_reply");
            Expect.True(BillingHelpers.D(q.Prop("unitPrice")) > 0, "The default ai_reply price is 0.");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("quote-errors", "Quote: an unpriced event is 404, a missing event type is refused", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await QuoteAsync(ctx, client, "auto_unpriced_event")).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/quote", ct: ctx.Ct)).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("client-card", "Rate cards: a client's own card prices its quotes before the plan/default card", async ctx =>
        {
            var (client, card) = await ClientCardAsync(ctx, "rate-own");
            (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0.004m, clientRate = 0.033333m, notes = "Omni.Automation" })).ExpectOk();
            var q = (await QuoteAsync(ctx, client, "ai_reply")).ExpectOk();
            Expect.EqualText(card, q.Prop("rateCardCode"), "Card pricing ai_reply");
            Expect.Equal(0.033333m, BillingHelpers.D(q.Prop("unitPrice")), "Client price");
            var overview = await BillingHelpers.OverviewAsync(ctx, client);
            Expect.EqualText(card, overview["customRateCardCode"], "Custom card on the admin overview");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("card-validation", "Rate cards: a second card for one client, an unknown client, or a default client card are 400", async ctx =>
        {
            var (client, _) = await ClientCardAsync(ctx, "rate-dup");
            var cards = $"{BillingHelpers.Base}/rate-cards";
            (await ctx.Admin.PostAsync(cards, new { code = $"auto-{ctx.Tag}-dup2", name = "[Automation] dup", clientId = client.ClientId }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync(cards, new { code = $"auto-{ctx.Tag}-unk", name = "[Automation] unknown", clientId = Guid.Parse("00000000-0000-0000-0000-000000000009") }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync(cards, new { code = $"auto-{ctx.Tag}-def", name = "[Automation] default", clientId = client.ClientId, isDefault = true }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync(cards, new { code = "default", name = "dup code" }, ct: ctx.Ct)).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing,
            description: "Every refused card would be global or misattributed; only the client's own card is created (deleted with the client).")
        .Test("rate-validation", "Rates: negative, 7 decimals, bad country/event/provider billing, past start, end before start are 400", async ctx =>
        {
            var (_, card) = await ClientCardAsync(ctx, "rate-val");
            var cases = new (string What, object Body)[]
            {
                ("negative price", new { eventType = "auto_evt", providerCost = 0, clientRate = -1 }),
                ("7 decimals", new { eventType = "auto_evt", providerCost = 0, clientRate = 0.0000001m }),
                ("3-letter country", new { eventType = "auto_evt", countryCode = "LBN", providerCost = 0, clientRate = 1 }),
                ("bad event type", new { eventType = "Auto Event!", providerCost = 0, clientRate = 1 }),
                ("unknown provider billing", new { eventType = "auto_evt", providerCost = 0, clientRate = 1, providerBilling = "nobody" }),
                ("start in the past", new { eventType = "auto_evt", providerCost = 0, clientRate = 1, effectiveFrom = DateTimeOffset.UtcNow.AddHours(-1) }),
                ("end before start", new { eventType = "auto_evt", providerCost = 0, clientRate = 1, effectiveTo = DateTimeOffset.UtcNow.AddMinutes(-1) }),
            };
            foreach (var (what, body) in cases)
            {
                var r = await AddRateAsync(ctx, card, body);
                Expect.True(r.Status == 400, $"Rate with {what}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
            (await AddRateAsync(ctx, "auto-no-such-card", new { eventType = "auto_evt", providerCost = 0, clientRate = 1 })).ExpectStatus(404);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("rate-overflow", "Rates: an absurd price (1e13) is 400, not 500", async ctx =>
        {
            var (_, card) = await ClientCardAsync(ctx, "rate-huge");
            var r = await AddRateAsync(ctx, card, new { eventType = "auto_huge", providerCost = 0, clientRate = 10000000000000m });
            Expect.True(r.Status == 400, $"A rate of 1e13 answered {r.Status} (expected 400).{r.Snippet(150)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-05")
        .Test("upcoming-version", "Rates: an upcoming version closes the current one at its start; quotes follow the date", async ctx =>
        {
            var (client, card) = await ClientCardAsync(ctx, "rate-next");
            (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0.004m, clientRate = 0.03m })).ExpectOk();
            // Whole seconds: the DB keeps microseconds, so a 100 ns-precise start would not compare equal on the second add.
            var now = DateTimeOffset.UtcNow;
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddDays(2);
            (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0.004m, clientRate = 0.05m, effectiveFrom = start })).ExpectOk();
            Expect.Equal(0.03m, BillingHelpers.D((await QuoteAsync(ctx, client, "ai_reply")).ExpectOk().Prop("unitPrice")), "Price now");
            Expect.Equal(0.05m, BillingHelpers.D((await QuoteAsync(ctx, client, "ai_reply", start.AddDays(1))).ExpectOk().Prop("unitPrice")), "Price after the new version starts");
            var rates = (await ctx.Admin.GetAsync($"{RatesPath(card)}?history=true", ct: ctx.Ct)).ExpectOk().Array();
            var current = rates.First(r => BillingHelpers.D(r?["clientRate"]) == 0.03m)!;
            Expect.True(current["effectiveTo"] is not null, "The current version was not closed at the upcoming version's start.");
            (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0, clientRate = 0.06m, effectiveFrom = start })).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("close-beyond-next", "Rates: ending the current version after the next one starts is refused (409)", async ctx =>
        {
            var (_, card) = await ClientCardAsync(ctx, "rate-close");
            var cur = (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0, clientRate = 0.03m })).ExpectOk().Id();
            (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0, clientRate = 0.05m, effectiveFrom = DateTimeOffset.UtcNow.AddDays(2) })).ExpectOk();
            var r = await ctx.Admin.PostAsync($"{BillingHelpers.Base}/rates/{cur}/close", new { effectiveTo = DateTimeOffset.UtcNow.AddDays(5) }, ct: ctx.Ct);
            Expect.True(r.Status is 409 or 400, $"Closing beyond the next version answered {r.Status}.{r.Snippet(200)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("end-upcoming", "Rates: an upcoming version can be cancelled (the portal's Cancel); the current price keeps applying", async ctx =>
        {
            var (client, card) = await ClientCardAsync(ctx, "rate-endnow");
            (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0, clientRate = 0.03m })).ExpectOk();
            var start = DateTimeOffset.UtcNow.AddDays(2);
            var upcoming = (await AddRateAsync(ctx, card, new { eventType = "ai_reply", unit = "reply", providerCost = 0, clientRate = 0.05m, effectiveFrom = start })).ExpectOk().Id();
            var r = await ctx.Admin.DeleteAsync($"{BillingHelpers.Base}/rates/{upcoming}", ctx.Ct);
            Expect.True(r.IsSuccess, $"Cancelling an upcoming rate answered {r.Status}: a scheduled price can never be withdrawn.{r.Snippet(200)}");
            var later = await QuoteAsync(ctx, client, "ai_reply", start.AddDays(1));
            Expect.True(later.IsSuccess && BillingHelpers.D(later.Prop("unitPrice")) == 0.03m,
                $"After ending the upcoming version, the price on {start.AddDays(1):d} should still be 0.03; got {later.Status}.{later.Snippet(200)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-04")
        .Test("provider-billing", "Provider billing: the four arrangements and the channel defaults are listed", async ctx =>
        {
            var r = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/provider-billing", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(4, r.Array("modes").Count, "Provider billing arrangements");
            Expect.True(r.Array("defaults").Any(d => d?["channel"]?.ToString() == "whatsapp"), "No default for WhatsApp.");
            (await ctx.Admin.SendJsonAsync(HttpMethod.Put, $"{BillingHelpers.Base}/channel-accounts/00000000-0000-0000-0000-000000000001/provider-billing",
                new { providerBilling = "customer_direct", reason = "Omni.Automation" }, ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Needs.AdminKey)
        .Test("report", "Report: the revenue report answers for a month and shows add-on revenue", async ctx =>
        {
            var r = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/report", ct: ctx.Ct)).ExpectOk();
            r.Prop("subscriptionRevenue");
            r.Prop("topUps");
            var json = r.RequireJson().AsObject();
            Expect.True(json.Any(p => p.Key.Contains("addon", StringComparison.OrdinalIgnoreCase)),
                "The billing report has no add-on revenue figure (addon_charge rows are left out).");
        }, needs: Needs.AdminKey, finding: "t2-billing-09")
        .Test("usage-paging", "Usage list: paging bounds are clamped and a bad date is 400", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var path = $"{BillingHelpers.Base}/clients/{client.ClientId}/usage";
            (await ctx.Admin.GetAsync($"{path}?page=0&pageSize=0", ct: ctx.Ct)).ExpectOk();
            var big = (await ctx.Admin.GetAsync($"{path}?page=-1&pageSize=100000", ct: ctx.Ct)).ExpectOk();
            Expect.True(big.Prop("pageSize").GetValue<int>() <= 500, "pageSize 100000 was not clamped.");
            (await ctx.Admin.GetAsync($"{path}?from=notadate", ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/accounts", ct: ctx.Ct)).ExpectOk();
        }, needs: Needs.Session | Needs.AdminKey)
        .Build();
}

