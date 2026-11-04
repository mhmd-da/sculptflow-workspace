using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(24)]
public static class BillingAddonsSuite
{
    private static Task<ApiResponse> SetAddonAsync(TestContext ctx, TestClient client, string code, int quantity, Dictionary<string, string>? key) =>
        ctx.Admin.SendJsonAsync(HttpMethod.Put, $"{BillingHelpers.Base}/clients/{client.ClientId}/addons/{code}", new { quantity }, key, ctx.Ct);

    public static TestSuite Build() => new SuiteBuilder("billing-addons", "Billing: add-ons, seat limit and bot limit",
            "Add-ons charged from the wallet raise plan limits; the seat (max_users) and AI bot (max_ai_bots) limits are enforced.")
        .Test("catalogue", "Add-ons: the catalogue lists the four add-ons with prices", async ctx =>
        {
            var items = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/addons", ct: ctx.Ct)).ExpectOk().Array();
            foreach (var code in new[] { "extra-user", "extra-channel", "extra-ai-bot", "extra-whatsapp-number" })
            {
                Expect.True(items.Any(a => a?["code"]?.ToString() == code), $"Add-on {code} is missing from the catalogue.");
            }
        }, needs: Needs.AdminKey)
        .Test("validation", "Add-ons: no key 400, bad quantities 400, unknown add-on 404", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await SetAddonAsync(ctx, client, "extra-user", 1, null)).ExpectStatus(400);
            (await SetAddonAsync(ctx, client, "extra-user", -1, BillingHelpers.Key(ctx, "ad"))).ExpectStatus(400);
            (await SetAddonAsync(ctx, client, "extra-user", 1001, BillingHelpers.Key(ctx, "ad"))).ExpectStatus(400);
            (await SetAddonAsync(ctx, client, "auto-no-such-addon", 1, BillingHelpers.Key(ctx, "ad"))).ExpectStatus(404);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("insufficient-wallet", "Add-ons: a wallet that can't pay is 422", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await SetAddonAsync(ctx, client, "extra-user", 1, BillingHelpers.Key(ctx, "ad"))).ExpectStatus(422);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("buy", "Add-ons: buying 2 extra users charges 2 x 8.00 and raises the seat limit", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-buy");
            await BillingHelpers.TopUpOkAsync(ctx, client, 30);
            var rows = (await SetAddonAsync(ctx, client, "extra-user", 2, BillingHelpers.Key(ctx, "ad"))).ExpectOk().Array();
            Expect.True(rows.Any(r => r?["code"]?.ToString() == "extra-user" && r["quantity"]?.GetValue<int>() == 2), "The client's add-ons don't show 2 extra users.");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.Equal(14m, BillingHelpers.D(s["walletBalance"]), "Wallet after 2 extra users (30 - 16)");
            Expect.Equal("3", BillingHelpers.Ent(s, "max_users"), "Seat limit (trial 1 + 2 extra)");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("replay-and-reduce", "Add-ons: a replay charges nothing; reducing refunds nothing; re-adding charges the delta", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-replay");
            await BillingHelpers.TopUpOkAsync(ctx, client, 40);
            var key = BillingHelpers.Key(ctx, "ad");
            (await SetAddonAsync(ctx, client, "extra-user", 2, key)).ExpectOk();
            (await SetAddonAsync(ctx, client, "extra-user", 2, key)).ExpectOk();
            Expect.Equal(24m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a replayed purchase (40 - 16 once)");
            (await SetAddonAsync(ctx, client, "extra-user", 1, BillingHelpers.Key(ctx, "ad"))).ExpectOk();
            Expect.Equal(24m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after reducing to 1 (no refund, as documented)");
            (await SetAddonAsync(ctx, client, "extra-user", 2, BillingHelpers.Key(ctx, "ad"))).ExpectOk();
            Expect.Equal(16m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after going back to 2 (one more unit charged)");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("parallel", "Add-ons: 5 parallel \"set to 3\" requests charge 3 units once", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-par");
            await BillingHelpers.TopUpOkAsync(ctx, client, 50);
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => SetAddonAsync(ctx, client, "extra-user", 3, BillingHelpers.Key(ctx, $"p{i}"))));
            Expect.True(results.Any(r => r.Status == 200), $"Statuses: {string.Join(",", results.Select(r => r.Status))}.");
            Expect.Equal(26m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after 5 parallel set-to-3 (50 - 24 once)");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing | Risk.Shared, description: "A small burst of 5 requests.")
        .Test("key-reuse-other-quantity", "Add-ons: reusing a key with another quantity is refused, not silently ignored", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-reuse");
            await BillingHelpers.TopUpOkAsync(ctx, client, 60);
            var key = BillingHelpers.Key(ctx, "ad");
            (await SetAddonAsync(ctx, client, "extra-user", 2, key)).ExpectOk();
            var second = await SetAddonAsync(ctx, client, "extra-user", 5, key);
            Expect.True(second.Status == 422,
                $"Reusing an Idempotency-Key with quantity 5 answered {second.Status} while the quantity stayed 2; expected 422.{second.Snippet(200)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-18")
        .Test("unlimited-plan", "Add-ons: units of an already unlimited limit are refused and not charged", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-unl");
            await ctx.SetPlanAsync(client, "business");
            await BillingHelpers.TopUpOkAsync(ctx, client, 20);
            var r = await SetAddonAsync(ctx, client, "extra-channel", 1, BillingHelpers.Key(ctx, "ad"));
            var wallet = await BillingHelpers.WalletAsync(ctx, client);
            Expect.True(r.Status == 422 && wallet == 20m,
                $"An extra channel on business (max_channel_connections = unlimited) answered {r.Status} and the wallet is {wallet} (was 20): nothing should be charged for capacity that is already unlimited.");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-06")
        .Test("cancelled-subscription", "Add-ons: adding units needs an active subscription (422 when cancelled)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-canc");
            await BillingHelpers.TopUpOkAsync(ctx, client, 20);
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/cancel", new { immediately = true }, ct: ctx.Ct)).ExpectOk();
            (await SetAddonAsync(ctx, client, "extra-user", 1, BillingHelpers.Key(ctx, "ad"))).ExpectStatus(422);
            Expect.Equal(20m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a refused add-on");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("seat-limit", "Seats: on the trial (1 seat) inviting a member is refused with 422", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var r = await client.Http.PostAsync("/api/members/invites",
                new { email = $"member-{ctx.Tag}-{Guid.NewGuid():N}@{TestContext.EmailDomain}", fullName = "Auto member" }, ct: ctx.Ct);
            r.ExpectStatus(422);
        })
        .Test("seat-addon", "Seats: after buying an extra user the seat limit no longer refuses an invite", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-seat");
            await BillingHelpers.TopUpOkAsync(ctx, client, 10);
            (await SetAddonAsync(ctx, client, "extra-user", 1, BillingHelpers.Key(ctx, "ad"))).ExpectOk();
            var r = await client.Http.PostAsync("/api/members/invites",
                new { email = $"member-{ctx.Tag}-{Guid.NewGuid():N}@{TestContext.EmailDomain}", fullName = "Auto member" }, ct: ctx.Ct);
            Expect.True(r.Status != 422, $"The invite was still refused by the seat limit after buying an extra user.{r.Snippet(200)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, description: "The invite email may fail (email isn't configured on staging); only the seat check matters.")
        .Test("bot-limit", "AI bots: ai-pro allows 2 active bots; activating a third is refused", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-bots");
            await ctx.SetPlanAsync(client, "ai-pro");
            var bots = new List<Guid>();
            for (var i = 0; i < 3; i++) bots.Add(await BillingHelpers.CreateBotAsync(ctx, client));
            foreach (var bot in bots)
            {
                var r = await client.Http.SubmitFormAsync("/Bots", new Dictionary<string, string> { ["id"] = bot.ToString(), ["status"] = "active" },
                    handler: "Status", ct: ctx.Ct);
                Expect.True(r.Status == 302, $"Activating bot {bot} answered {r.Status}.");
            }
            var active = await ctx.Db.ScalarAsync<long>("select count(*) from crm.bots where client_id = @c and status = 'active'", new { c = client.ClientId }, ctx.Ct);
            Expect.Equal(2L, active, "Active bots after activating 3 on ai-pro (max_ai_bots = 2)");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("bot-addon", "AI bots: an extra-ai-bot add-on allows a third active bot", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "add-bot3");
            await ctx.SetPlanAsync(client, "ai-pro");
            await BillingHelpers.TopUpOkAsync(ctx, client, 15);
            (await SetAddonAsync(ctx, client, "extra-ai-bot", 1, BillingHelpers.Key(ctx, "ad"))).ExpectOk();
            for (var i = 0; i < 3; i++)
            {
                var bot = await BillingHelpers.CreateBotAsync(ctx, client);
                (await client.Http.SubmitFormAsync("/Bots", new Dictionary<string, string> { ["id"] = bot.ToString(), ["status"] = "active" },
                    handler: "Status", ct: ctx.Ct)).ExpectStatus(302);
            }
            var active = await ctx.Db.ScalarAsync<long>("select count(*) from crm.bots where client_id = @c and status = 'active'", new { c = client.ClientId }, ctx.Ct);
            Expect.Equal(3L, active, "Active bots with ai-pro + 1 extra AI bot");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Build();
}

