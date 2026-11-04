using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(26)]
public static class BillingUsageSuite
{
    public static TestSuite Build() => new SuiteBuilder("billing-usage", "Billing: AI reply usage and refunds",
            "ai_reply charging through a bot test session answered via the ingest reply route (no model runs): credit first, then the wallet; refusal at zero balance; refunds.")
        .Test("charged-from-credit", "Usage: a bot reply is charged as ai_reply from the included credit", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-credit");
            var before = await BillingHelpers.CreditAsync(ctx, client);
            (await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation reply")).ExpectOk();
            var usage = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Single();
            Expect.EqualText("settled", usage["chargeStatus"], "Usage status");
            var amount = BillingHelpers.D(usage["amount"]);
            Expect.True(amount > 0, "The ai_reply usage has no amount.");
            Expect.Equal(amount, BillingHelpers.D(usage["creditAmount"]), "Part paid from credit");
            Expect.Equal(before - amount, await BillingHelpers.CreditAsync(ctx, client), "Credit after one reply");
            var clientUsage = (await client.Http.GetAsync("/api/billing/usage", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.True(clientUsage.Count >= 1, "The client doesn't see its AI reply usage.");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing)
        .Test("credit-spill", "Usage: when the credit runs short the rest is taken from the wallet (exact to 6 decimals)", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-spill");
            var card = $"auto-{ctx.Tag}-spill";
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/rate-cards", new { code = card, name = "[Automation] spill", clientId = client.ClientId }, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/rate-cards/{card}/rates", new { eventType = "ai_reply", unit = "reply", providerCost = 0, clientRate = 0.033333m }, ct: ctx.Ct)).ExpectOk();
            var credit = await BillingHelpers.CreditAsync(ctx, client);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -(credit - 0.016667m), balanceType = "included_credit", reason = "Automation: leave 0.016667 credit" },
                BillingHelpers.Key(ctx, "a"))).ExpectOk();
            await BillingHelpers.TopUpOkAsync(ctx, client, 1);
            (await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation reply")).ExpectOk();
            var usage = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Single();
            Expect.Equal(0.016667m, BillingHelpers.D(usage["creditAmount"]), "Part paid from credit");
            Expect.Equal(0.016666m, BillingHelpers.D(usage["walletAmount"]), "Part paid from the wallet");
            Expect.Equal(0.983334m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after the reply");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing)
        .Test("zero-balance", "Usage: with no credit and no wallet the reply is refused and recorded as failed insufficient_funds", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-zero");
            var credit = await BillingHelpers.CreditAsync(ctx, client);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -credit, balanceType = "included_credit", reason = "Automation: drain credit" },
                BillingHelpers.Key(ctx, "a"))).ExpectOk();
            await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation reply");
            var usage = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Single();
            Expect.EqualText("failed", usage["chargeStatus"], "Usage status at zero balance");
            Expect.EqualText("insufficient_funds", usage["failureReason"], "Failure reason");
            var messages = await ctx.Db.ScalarAsync<long>("select count(*) from crm.bot_test_messages where session_id = @s and role = 'bot'", new { s = session }, ctx.Ct);
            Expect.Equal(0L, messages, "Bot lines stored for a refused reply");
            Expect.Equal(0m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a refused reply");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing)
        .Test("retry-not-double-charged", "Usage: a retried reply call (same body twice) is charged once", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-retry");
            (await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation retried reply")).ExpectOk();
            (await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation retried reply")).ExpectOk();
            var charged = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Count(u => u["chargeStatus"]?.ToString() == "settled");
            Expect.True(charged == 1, $"The same reply posted twice (an n8n retry) was charged {charged} times and stored twice; expected one charge.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-02")
        .Test("refund", "Refund: a settled usage is refunded to its source once; a second refund changes nothing", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-refund");
            var before = await BillingHelpers.CreditAsync(ctx, client);
            (await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation reply")).ExpectOk();
            var usage = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Single();
            var path = $"{BillingHelpers.Base}/clients/{client.ClientId}/usage/{usage["id"]}/refund";
            (await ctx.Admin.PostAsync(path, new { reason = "" }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync(path, new { reason = "Automation refund" }, ct: ctx.Ct)).ExpectOk();
            Expect.Equal(before, await BillingHelpers.CreditAsync(ctx, client), "Credit after the refund");
            var again = await ctx.Admin.PostAsync(path, new { reason = "Automation refund again" }, ct: ctx.Ct);
            Expect.True(again.Status is 200 or 422, $"A second refund answered {again.Status}.");
            Expect.Equal(before, await BillingHelpers.CreditAsync(ctx, client), "Credit after a second refund");
            Expect.Equal(1, (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "usage_refund")).Count, "Refund ledger rows");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing)
        .Test("refund-guards", "Refund: a failed usage is 422, another client's usage is 404", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-rguard");
            var credit = await BillingHelpers.CreditAsync(ctx, client);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -credit, balanceType = "included_credit", reason = "Automation: drain credit" },
                BillingHelpers.Key(ctx, "a"))).ExpectOk();
            await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation reply");
            var failed = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Single();
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/usage/{failed["id"]}/refund", new { reason = "Automation" }, ct: ctx.Ct))
                .ExpectStatus(422);
            var other = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{other.ClientId}/usage/{failed["id"]}/refund", new { reason = "Automation" }, ct: ctx.Ct))
                .ExpectStatus(404);
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing)
        .Test("parallel-refunds", "Refund: 4 parallel refunds of one usage refund it once", async ctx =>
        {
            var (client, bot, session) = await BillingHelpers.AiReplySetupAsync(ctx, "use-rpar");
            var before = await BillingHelpers.CreditAsync(ctx, client);
            (await BillingHelpers.PostTestReplyAsync(ctx, client, bot, session, "Automation reply")).ExpectOk();
            var usage = (await BillingHelpers.AiReplyUsageAsync(ctx, client)).Single();
            var path = $"{BillingHelpers.Base}/clients/{client.ClientId}/usage/{usage["id"]}/refund";
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ctx.Admin.PostAsync(path, new { reason = $"Automation parallel refund {i}" }, ct: ctx.Ct)));
            Expect.Equal(before, await BillingHelpers.CreditAsync(ctx, client), "Credit after 4 parallel refunds");
            Expect.Equal(1, (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "usage_refund")).Count, "Refund ledger rows");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing | Risk.Shared, description: "A small burst of 4 requests.")
        .Build();
}

[Suite(27)]
public static class BillingGatingSuite
{
    /// <summary>Opens a page and returns where it ended up: the page itself, or the not-included page it redirected to.</summary>
    private static async Task<string> LandingAsync(TestContext ctx, TestClient client, string path)
    {
        var r = await client.Http.GetAsync(path, ct: ctx.Ct);
        return r.Status is 301 or 302 ? r.Location ?? "" : r.Status == 200 ? path : $"status {r.Status}";
    }

    public static TestSuite Build() => new SuiteBuilder("billing-gating", "Billing: plan gating of features and pages",
            "What a plan doesn't include is refused by the API (422) and the page says so; plan changes apply at once.")
        .Test("trial-apis", "Gating: on the trial, bookings, calendar, calendar integrations and campaigns APIs answer 422", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await client.Http.GetAsync("/api/bookings", ct: ctx.Ct)).ExpectStatus(422);
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectStatus(422);
            (await client.Http.GetAsync("/api/calendar-integrations", ct: ctx.Ct)).ExpectStatus(422);
            (await client.Http.PostAsync("/api/bookings", new { contactId = Guid.NewGuid(), bookingType = "standard", scheduledStart = DateTimeOffset.UtcNow.AddDays(3) }, ct: ctx.Ct))
                .ExpectStatus(422);
            var campaign = await client.Http.PostAsync("/api/campaigns", new { name = "Automation gated", whatsAppTemplateId = Guid.NewGuid(), contactIds = Array.Empty<Guid>() }, ct: ctx.Ct);
            campaign.ExpectStatus(422);
        })
        .Test("trial-pages", "Gating: on the trial, Bots, Campaigns, Knowledge base and Services pages go to \"not included\"", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            foreach (var path in new[] { "/Bots", "/Campaigns", "/KnowledgeBase", "/Services" })
            {
                var landing = await LandingAsync(ctx, client, path);
                Expect.True(landing.Contains("/plan/not-included/", StringComparison.OrdinalIgnoreCase),
                    $"{path} on the trial should redirect to the not-included page; it ended at {landing}.");
            }
            (await client.Http.GetAsync("/plan/not-included/campaigns", ct: ctx.Ct)).ExpectOk();
        })
        .Test("trial-reports", "Gating: on the trial the Reports page says reports aren't included", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await client.Http.GetAsync("/reports", ct: ctx.Ct)).ExpectOk().ExpectBodyContains("included in your plan");
            (await client.Http.GetAsync("/dashboard/bookings", ct: ctx.Ct)).ExpectOk().ExpectBodyContains("included in your plan");
        })
        .Test("growth-reports", "Gating: growth gets basic reports; advanced reports stay locked", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "gate-rep");
            await ctx.SetPlanAsync(client, "growth");
            var page = (await client.Http.GetAsync("/reports", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("aren&#x27;t included in your plan") && !page.Body.Contains("aren't included in your plan"),
                "Reports say they aren't included on growth (reporting = true).");
            page.ExpectBodyContains("come with advanced reporting");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("growth", "Gating: growth includes bookings and campaigns, not the AI assistant", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "gate-growth");
            await ctx.SetPlanAsync(client, "growth");
            (await client.Http.GetAsync("/api/bookings", ct: ctx.Ct)).ExpectOk();
            (await client.Http.GetAsync("/api/campaigns", ct: ctx.Ct)).ExpectOk();
            Expect.Equal("/Campaigns", await LandingAsync(ctx, client, "/Campaigns"), "Where /Campaigns lands on growth");
            Expect.Equal("/Services", await LandingAsync(ctx, client, "/Services"), "Where /Services lands on growth");
            Expect.True((await LandingAsync(ctx, client, "/Bots")).Contains("/plan/not-included/", StringComparison.OrdinalIgnoreCase),
                "/Bots should be not-included on growth (no AI assistant).");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("inbox-no-campaigns", "Gating: inbox-bookings includes bookings but campaign creation is 422", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "gate-ib");
            await ctx.SetPlanAsync(client, "inbox-bookings");
            (await client.Http.GetAsync("/api/bookings", ct: ctx.Ct)).ExpectOk();
            (await client.Http.PostAsync("/api/campaigns", new { name = "Automation gated", whatsAppTemplateId = Guid.NewGuid(), contactIds = Array.Empty<Guid>() }, ct: ctx.Ct))
                .ExpectStatus(422);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("services-consistent", "Gating: the Services API and the Services page agree on the trial", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var api = await client.Http.PostAsync("/api/services", new { name = $"Automation gated {Guid.NewGuid():N}"[..40], bookingDuration = 30 }, ct: ctx.Ct);
            var page = await LandingAsync(ctx, client, "/Services");
            var pageGated = page.Contains("/plan/not-included/", StringComparison.OrdinalIgnoreCase);
            Expect.True(pageGated == (api.Status == 422),
                $"On the trial POST /api/services answered {api.Status} while the Services page {(pageGated ? "says services aren't included" : "is open")}: the API and the page should agree.");
        }, finding: "t2-billing-03,t5-ops-20")
        .Test("knowledge-api", "Gating: without the AI assistant, creating a knowledge document is 422", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var r = await client.Http.PostAsync("/api/knowledge", new { title = "Automation gated doc", category = "general", content = "Opening hours are 9 to 5." }, ct: ctx.Ct);
            Expect.True(r.Status == 422, $"POST /api/knowledge on a plan without the AI assistant answered {r.Status} (the document is embedded at Omni's cost); expected 422 like the Knowledge base page.{r.Snippet(150)}");
        }, risk: Risk.External, finding: "t2-billing-03", description: "While the bug is present the document is embedded (OpenAI cost).")
        .Test("website-api", "Gating: without the AI assistant, adding a website to crawl is 422", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var r = await client.Http.PostAsync("/api/knowledge/websites", new { url = "https://example.com", crawlMode = "single_page" }, ct: ctx.Ct);
            Expect.True(r.Status == 422, $"POST /api/knowledge/websites on a plan without the AI assistant answered {r.Status}; expected 422.{r.Snippet(150)}");
        }, risk: Risk.External, finding: "t2-billing-03", description: "While the bug is present example.com is crawled and embedded.")
        .Build();
}
