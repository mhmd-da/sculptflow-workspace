using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(20)]
public static class BillingPlansSuite
{
    public static TestSuite Build() => new SuiteBuilder("billing-plans", "Billing: plans and the trial",
            "The plan catalogue, what a new client gets (the 7-day trial) and the client's own billing view.")
        .Test("catalogue", "Plans: the seeded plans are listed and active", async ctx =>
        {
            var plans = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/plans", ct: ctx.Ct)).ExpectOk().Array();
            foreach (var code in new[] { "trial", "inbox", "inbox-bookings", "growth", "ai-pro", "business" })
            {
                var plan = plans.FirstOrDefault(p => p?["code"]?.ToString() == code)
                    ?? throw new TestFailure($"Plan \"{code}\" is missing from the catalogue.");
                Expect.True(plan["isActive"]?.GetValue<bool>() == true, $"Plan \"{code}\" is not active.");
            }
            var trial = plans.First(p => p?["code"]?.ToString() == "trial")!;
            Expect.EqualText("7", trial["trialDays"], "Trial length in days");
            Expect.Equal(0m, BillingHelpers.D(trial["price"]), "Trial price");
        }, needs: Needs.AdminKey)
        .Test("plan-detail", "Plans: one plan by code, unknown code is 404", async ctx =>
        {
            var growth = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/plans/growth", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("true", growth.Prop("entitlements")["bookings"], "growth includes bookings");
            Expect.EqualText("true", growth.Prop("entitlements")["campaigns"], "growth includes campaigns");
            (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/plans/auto-no-such-plan", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Needs.AdminKey)
        .Test("entitlement-catalogue", "Plans: the entitlement catalogue lists features and limits", async ctx =>
        {
            var items = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/entitlements", ct: ctx.Ct)).ExpectOk().Array();
            string? Kind(string key) => items.FirstOrDefault(i => i?["key"]?.ToString() == key)?["kind"]?.ToString();
            Expect.Equal("feature", Kind("bookings"), "Kind of bookings");
            Expect.Equal("feature", Kind("ai_assistant"), "Kind of ai_assistant");
            Expect.Equal("limit", Kind("max_users"), "Kind of max_users");
            Expect.Equal("limit", Kind("max_ai_bots"), "Kind of max_ai_bots");
        }, needs: Needs.AdminKey)
        .Test("plan-validation", "Plans: invalid plans are refused with 400", async ctx =>
        {
            var code = $"auto-{ctx.Tag}-bad";
            var cases = new (string What, object Body)[]
            {
                ("bad code", new { code = "Bad Code!", name = "x", price = 1, billingPeriod = "month", includedUsageCredit = 0 }),
                ("existing code", new { code = "trial", name = "x", price = 1, billingPeriod = "month", includedUsageCredit = 0 }),
                ("negative price", new { code, name = "x", price = -1, billingPeriod = "month", includedUsageCredit = 0 }),
                ("3-decimal price", new { code, name = "x", price = 1.001, billingPeriod = "month", includedUsageCredit = 0 }),
                ("weekly period", new { code, name = "x", price = 1, billingPeriod = "week", includedUsageCredit = 0 }),
                ("paid trial", new { code, name = "x", price = 5, billingPeriod = "month", includedUsageCredit = 0, trialDays = 7 }),
                ("unknown rate card", new { code, name = "x", price = 1, billingPeriod = "month", includedUsageCredit = 0, rateCardCode = "auto-no-such-card" }),
                ("no name", new { code, name = "", price = 1, billingPeriod = "month", includedUsageCredit = 0 }),
            };
            foreach (var (what, body) in cases)
            {
                var r = await ctx.Admin.PostAsync($"{BillingHelpers.Base}/plans", body, ct: ctx.Ct);
                Expect.True(r.Status == 400, $"Plan with {what}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
        }, needs: Needs.AdminKey, risk: Risk.Billing, description: "Nothing is created: every request is invalid (a regression would add a plan to the global catalogue).")
        .Test("put-keeps-trial", "Plans: a PUT without trialDays can't turn the trial (it has subscribers) into a free plan that never ends", async ctx =>
        {
            var trial = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/plans/trial", ct: ctx.Ct)).ExpectOk();
            var days = trial.Prop("trialDays").GetValue<int>();
            var subscribers = trial.Prop("subscribers").GetValue<int>();
            if (subscribers == 0) throw new TestSkipped("The trial plan has no subscribers here, so switching it is an ordinary edit.");
            // Name, price and period only, as a script would send them: no trialDays, no isActive.
            var r = await ctx.Admin.PutAsync($"{BillingHelpers.Base}/plans/trial",
                new { code = "trial", name = trial.Str("name"), price = 0, billingPeriod = trial.Str("billingPeriod") }, ctx.Ct);
            if (r.Status < 300)
            {
                // The bug: put the trial back as it was before failing.
                await ctx.Admin.PutAsync($"{BillingHelpers.Base}/plans/trial", new
                {
                    code = "trial", name = trial.Str("name"), description = trial.Prop("description")?.ToString(), price = 0,
                    billingPeriod = trial.Str("billingPeriod"), includedUsageCredit = BillingHelpers.D(trial.Prop("includedUsageCredit")),
                    rateCardCode = trial.Prop("rateCardCode")?.ToString(), isActive = trial.Prop("isActive").GetValue<bool>(),
                    sortOrder = trial.Prop("sortOrder").GetValue<int>(), trialDays = days,
                }, ctx.Ct);
                throw new TestFailure($"PUT plans/trial without trialDays answered {r.Status}: the {subscribers} trial subscribers would renew at 0 for ever (restored).");
            }
            Expect.True(r.Status == 422, $"PUT plans/trial without trialDays answered {r.Status}; expected 422.{r.Snippet(200)}");
            var after = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/plans/trial", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(days.ToString(), after.Prop("trialDays")?.ToString(), "Trial days after the refused PUT");
        }, needs: Needs.AdminKey, risk: Risk.Billing | Risk.Shared, finding: "t2-billing-17",
            description: "Sends a PUT to the shared trial plan that must be refused; if the bug lets it through, the plan is restored at once.")
        .Test("signup-trial", "Trial: a new client starts on the 7-day trial with an empty wallet", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var s = (await client.Http.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("trial", s.Prop("planCode"), "Plan at sign-up");
            Expect.EqualText("active", s.Prop("subscriptionStatus"), "Subscription status");
            Expect.True(s.Prop("hasAccess").GetValue<bool>(), "A new trial client has no access.");
            Expect.EqualText("7", s.Prop("trialDays"), "Trial days");
            Expect.Equal(0m, BillingHelpers.D(s.Prop("walletBalance")), "Wallet at sign-up");
            var end = DateTimeOffset.Parse(s.Str("currentPeriodEnd"), CultureInfo.InvariantCulture);
            var days = (end - DateTimeOffset.UtcNow).TotalDays;
            Expect.True(days is > 6 and <= 7.01, $"The trial should end about 7 days from sign-up, but ends {end:u} ({days:0.0} days).");
        })
        .Test("trial-entitlements", "Trial: no bookings, campaigns or AI; one seat", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var s = (await client.Http.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk().RequireJson();
            Expect.Equal("false", BillingHelpers.Ent(s, "bookings"), "Trial bookings");
            Expect.Equal("false", BillingHelpers.Ent(s, "campaigns"), "Trial campaigns");
            Expect.Equal("false", BillingHelpers.Ent(s, "ai_assistant"), "Trial AI assistant");
            Expect.Equal("1", BillingHelpers.Ent(s, "max_users"), "Trial seats");
        })
        .Test("client-usage-transactions", "Client billing: usage and transactions lists answer for the owner", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await client.Http.GetAsync("/api/billing/usage?page=1&pageSize=10", ct: ctx.Ct)).ExpectOk().Array("items");
            var tx = (await client.Http.GetAsync("/api/billing/transactions?page=1&pageSize=10", ct: ctx.Ct)).ExpectOk();
            tx.Array("items");
            Expect.True(tx.Prop("pageSize").GetValue<int>() == 10, "Transactions page size was not honoured.");
        })
        .Test("billing-page", "Client billing: the Billing page shows the trial plan", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var page = (await client.Http.GetAsync("/settings/billing", ct: ctx.Ct)).ExpectOk();
            page.ExpectBodyContains("Free Trial");
        })
        .Test("signed-out", "Client billing: signed out gets 401 from the API and the sign-in page for the Billing page", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectStatus(401);
            var page = await ctx.Anonymous.GetAsync("/settings/billing", ct: ctx.Ct);
            Expect.True(page.Status == 302 && (page.Location ?? "").Contains("/Account/Login", StringComparison.OrdinalIgnoreCase),
                $"Signed-out /settings/billing should redirect to sign-in; got {page.Status} {page.Location}.");
        }, needs: Needs.None)
        .Test("admin-key-required", "Admin billing API: no key or a wrong key is refused, a signed-in client is refused", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await ctx.Anonymous.GetAsync($"{BillingHelpers.Base}/plans", ct: ctx.Ct)).ExpectRefused();
            var wrong = ctx.NewHttp(new Dictionary<string, string> { ["X-Platform-Admin-Key"] = "auto-wrong-key" });
            (await wrong.GetAsync($"{BillingHelpers.Base}/plans", ct: ctx.Ct)).ExpectRefused();
            var r = await client.Http.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/wallet/top-ups", new { amount = 100 },
                BillingHelpers.Key(ctx, "self"), ctx.Ct);
            r.ExpectRefused();
            Expect.Equal(0m, BillingHelpers.D((await client.Http.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk().Prop("walletBalance")),
                "Wallet after a client tried to top itself up");
        })
        .Build();
}

[Suite(21)]
public static class BillingSubscriptionsSuite
{
    public static TestSuite Build() => new SuiteBuilder("billing-subs", "Billing: subscribe, upgrade, downgrade, cancel",
            "Plan changes through the platform-admin API on private clients: money math, idempotency, cancel/resume.")
        .Test("needs-key", "Subscribe: no Idempotency-Key is 400", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await BillingHelpers.StartPlanAsync(ctx, client, "growth", null)).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("unknown-plan", "Subscribe: an unknown plan is 400 and nothing changes", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await BillingHelpers.StartPlanAsync(ctx, client, "auto-no-such-plan", BillingHelpers.Key(ctx, "s"))).ExpectStatus(400);
            Expect.EqualText("trial", (await BillingHelpers.SummaryAsync(ctx, client))["planCode"], "Plan after a refused change");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("insufficient-wallet", "Subscribe: a wallet that can't pay the plan is 422 and nothing is charged", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-poor");
            await BillingHelpers.TopUpOkAsync(ctx, client, 10);
            (await BillingHelpers.StartPlanAsync(ctx, client, "growth", BillingHelpers.Key(ctx, "s"))).ExpectStatus(422);
            Expect.Equal(10m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a refused upgrade");
            Expect.EqualText("trial", (await BillingHelpers.SummaryAsync(ctx, client))["planCode"], "Plan after a refused upgrade");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("upgrade", "Upgrade: the full price is charged from the wallet and the plan's credit granted", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-up");
            await BillingHelpers.TopUpOkAsync(ctx, client, 150);
            var sub = (await BillingHelpers.StartPlanAsync(ctx, client, "growth", BillingHelpers.Key(ctx, "s"))).ExpectOk();
            Expect.EqualText("growth", sub.Prop("planCode"), "Plan in the answer");
            Expect.EqualText("active", sub.Prop("status"), "Status in the answer");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.Equal(51m, BillingHelpers.D(s["walletBalance"]), "Wallet after paying growth (150 - 99)");
            Expect.Equal(10m, BillingHelpers.D(s["includedCreditRemaining"]), "Included credit of growth");
            Expect.Equal("true", BillingHelpers.Ent(s, "bookings"), "Bookings after the upgrade");
            var charges = await BillingHelpers.LedgerOfTypeAsync(ctx, client, "subscription_charge");
            Expect.True(charges.Any(c => BillingHelpers.D(c["amount"]) == -99m), "No -99.00 subscription_charge row in the ledger.");
            Expect.True(charges.Any(c => c["actor"]?.ToString() == "omni-automation"), "The charge doesn't record the admin actor.");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("downgrade", "Downgrade: charges the new price now, old credit expires (no proration, as documented)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-down");
            await BillingHelpers.TopUpOkAsync(ctx, client, 150);
            (await BillingHelpers.StartPlanAsync(ctx, client, "growth", BillingHelpers.Key(ctx, "s1"))).ExpectOk();
            (await BillingHelpers.StartPlanAsync(ctx, client, "inbox", BillingHelpers.Key(ctx, "s2"))).ExpectOk();
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.EqualText("inbox", s["planCode"], "Plan after the downgrade");
            Expect.Equal(12m, BillingHelpers.D(s["walletBalance"]), "Wallet after growth then inbox (150 - 99 - 39)");
            Expect.Equal(0m, BillingHelpers.D(s["includedCreditRemaining"]), "Growth's credit after moving to inbox");
            Expect.Equal("false", BillingHelpers.Ent(s, "bookings"), "Bookings on inbox");
            Expect.True((await BillingHelpers.LedgerOfTypeAsync(ctx, client, "included_credit_expiry")).Any(r => BillingHelpers.D(r["amount"]) == -10m),
                "The old plan's 10.00 credit was not expired in the ledger.");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, description: "docs/billing.md: no proration on plan changes (t2-billing-10 is by design).")
        .Test("replay", "Subscribe: replaying the same key changes nothing and charges once", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-replay");
            await BillingHelpers.TopUpOkAsync(ctx, client, 100);
            var key = BillingHelpers.Key(ctx, "s");
            (await BillingHelpers.StartPlanAsync(ctx, client, "inbox", key)).ExpectOk();
            (await BillingHelpers.StartPlanAsync(ctx, client, "inbox", key)).ExpectOk();
            Expect.Equal(61m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a replayed subscribe (100 - 39 once)");
            Expect.Equal(1, (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "subscription_charge")).Count(c => BillingHelpers.D(c["amount"]) == -39m),
                "Number of -39.00 charges");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("parallel-same-key", "Subscribe: 5 parallel requests with one key charge once", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-par");
            await BillingHelpers.TopUpOkAsync(ctx, client, 100);
            var key = BillingHelpers.Key(ctx, "s");
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => BillingHelpers.StartPlanAsync(ctx, client, "inbox", key)));
            Expect.True(results.Any(r => r.Status == 200), $"No parallel subscribe succeeded: {string.Join(",", results.Select(r => r.Status))}.");
            Expect.True(results.All(r => r.Status is 200 or 409 or 422), $"Unexpected statuses: {string.Join(",", results.Select(r => r.Status))}.");
            Expect.Equal(61m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after 5 parallel same-key subscribes");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("key-reuse-other-plan", "Subscribe: reusing a key for a different plan is refused, not silently ignored", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-reuse");
            await BillingHelpers.TopUpOkAsync(ctx, client, 200);
            var key = BillingHelpers.Key(ctx, "s");
            (await BillingHelpers.StartPlanAsync(ctx, client, "inbox", key)).ExpectOk();
            var second = await BillingHelpers.StartPlanAsync(ctx, client, "growth", key);
            Expect.True(second.Status == 422,
                $"Reusing an Idempotency-Key for another plan answered {second.Status} (the caller believes the change happened, but the plan is still {(await BillingHelpers.SummaryAsync(ctx, client))["planCode"]}); expected 422.{second.Snippet(200)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-18")
        .Test("entitlements-cache", "Plan change: new entitlements apply immediately (cache cleared)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-cache");
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectStatus(422);
            await ctx.SetPlanAsync(client, "growth");
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectOk();
            await ctx.SetPlanAsync(client, "inbox");
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectStatus(422);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("cancel-at-end", "Cancel at period end: access stays until then; resume undoes it", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-cancel");
            await ctx.SetPlanAsync(client, "growth");
            var c = (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/cancel",
                new { immediately = false, reason = "Omni.Automation cancel at end" }, ct: ctx.Ct)).ExpectOk();
            Expect.True(c.Prop("cancelAtPeriodEnd").GetValue<bool>(), "cancelAtPeriodEnd is not set.");
            Expect.EqualText("active", c.Prop("status"), "Status right after cancel-at-end");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.True(s["hasAccess"]!.GetValue<bool>(), "Access was removed before the period ended.");
            Expect.True(s["cancelAtPeriodEnd"]!.GetValue<bool>(), "The summary doesn't show the scheduled cancellation.");
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/cancel",
                new { immediately = false }, ct: ctx.Ct)).ExpectOk();
            var r = (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/resume", ct: ctx.Ct)).ExpectOk();
            Expect.True(!r.Prop("cancelAtPeriodEnd").GetValue<bool>(), "Resume didn't clear cancelAtPeriodEnd.");
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/resume", ct: ctx.Ct)).ExpectOk();
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("cancel-now", "Cancel now: access ends, credit expires, resume and renew don't bring it back", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-cnow");
            await ctx.SetPlanAsync(client, "growth");
            var c = (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/cancel",
                new { immediately = true, reason = "Omni.Automation cancel now" }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("cancelled", c.Prop("status"), "Status after cancel now");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.True(!s["hasAccess"]!.GetValue<bool>(), "A cancelled client still has access.");
            Expect.Equal(0m, BillingHelpers.D(s["includedCreditRemaining"]), "Credit after cancel now");
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectStatus(422);
            var resumed = (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/resume", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("cancelled", resumed.Prop("status"), "Status after resuming a cancelled subscription");
            Expect.Equal("NotDue", await BillingHelpers.RenewAsync(ctx, client), "Renew of a cancelled subscription");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("cancel-empty-body", "Cancel: a request without a body is accepted (cancel at period end)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "sub-cbody");
            var r = await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/cancel", ct: ctx.Ct);
            Expect.True(r.Status == 200, $"POST .../subscription/cancel with no body answered {r.Status}; expected it to be accepted as a cancel at period end.{r.Snippet(200)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-25")
        .Test("unknown-client-writes", "Unknown client: top-up, add-on, cancel and renew answer 404, not 500", async ctx =>
        {
            var unknown = $"{BillingHelpers.Base}/clients/00000000-0000-0000-0000-000000000001";
            var results = new List<string>();
            var top = await ctx.Admin.PostAsync($"{unknown}/wallet/top-ups", new { amount = 1, reason = "auto" }, BillingHelpers.Key(ctx, "u"), ctx.Ct);
            results.Add($"top-up {top.Status}");
            var addon = await ctx.Admin.SendJsonAsync(HttpMethod.Put, $"{unknown}/addons/extra-user", new { quantity = 1 }, BillingHelpers.Key(ctx, "u"), ctx.Ct);
            results.Add($"add-on {addon.Status}");
            var cancel = await ctx.Admin.PostAsync($"{unknown}/subscription/cancel", new { immediately = true }, ct: ctx.Ct);
            results.Add($"cancel {cancel.Status}");
            var renew = await ctx.Admin.PostAsync($"{unknown}/subscription/renew", ct: ctx.Ct);
            results.Add($"renew {renew.Status}");
            Expect.True(new[] { top, addon, cancel, renew }.All(r => r.Status == 404),
                $"Billing writes for an unknown client should answer 404: {string.Join(", ", results)}.");
        }, needs: Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-14")
        .Test("unknown-client-reads", "Unknown client: the overview is 404", async ctx =>
        {
            (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/clients/00000000-0000-0000-0000-000000000001", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Needs.AdminKey)
        .Build();
}

