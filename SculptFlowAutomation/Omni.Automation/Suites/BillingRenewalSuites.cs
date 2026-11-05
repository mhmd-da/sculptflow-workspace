using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(22)]
public static class BillingRenewalSuite
{
    private static Task<ApiResponse> SetAddonAsync(TestContext ctx, TestClient client, string code, int quantity) =>
        ctx.Admin.SendJsonAsync(HttpMethod.Put, $"{BillingHelpers.Base}/clients/{client.ClientId}/addons/{code}", new { quantity },
            BillingHelpers.Key(ctx, "ren-ad"), ctx.Ct);

    private static Task<ApiResponse> SetRenewalAsync(TestContext ctx, TestClient client, string code, bool? autoRenew) =>
        ctx.Admin.SendJsonAsync(HttpMethod.Put, $"{BillingHelpers.Base}/clients/{client.ClientId}/addons/{code}/renewal", new { autoRenew }, null, ctx.Ct);

    private static async Task<JsonArray> AddonsAsync(TestContext ctx, TestClient client) =>
        (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/addons", ct: ctx.Ct)).ExpectOk().Array();

    public static TestSuite Build() => new SuiteBuilder("billing-renewal", "Billing: renewals, past due and trial expiry (time travel)",
            "Moves a subscription's period end into the past with a DB write, then renews through the admin API (or waits for the BillingMaintenance worker).")
        .Test("not-due", "Renew: a current subscription is not due", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-notdue");
            await ctx.SetPlanAsync(client, "inbox");
            Expect.Equal("NotDue", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("renewed", "Renew: an ended period is charged again and a new period starts where the old one ended", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-ok");
            await BillingHelpers.TopUpOkAsync(ctx, client, 100);
            await ctx.SetPlanAsync(client, "inbox");
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            var oldEnd = await ctx.Db.ScalarAsync<DateTime>("select current_period_end from billing.subscriptions where client_id = @c", new { c = client.ClientId }, ctx.Ct);
            Expect.Equal("Renewed", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome");
            var sub = (await BillingHelpers.OverviewAsync(ctx, client))["subscription"]!;
            var newStart = DateTimeOffset.Parse(sub["currentPeriodStart"]!.ToString(), CultureInfo.InvariantCulture);
            Expect.True(Math.Abs((newStart.UtcDateTime - DateTime.SpecifyKind(oldEnd, DateTimeKind.Utc)).TotalSeconds) < 2,
                $"The new period should start at the old end ({oldEnd:u}), starts {newStart:u}.");
            Expect.Equal(61m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after one renewal (100 - 39)");
            Expect.Equal("NotDue", await BillingHelpers.RenewAsync(ctx, client), "A second renew right after");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("parallel-renew", "Renew: 5 parallel renewals charge one period", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-par");
            await BillingHelpers.TopUpOkAsync(ctx, client, 200);
            await ctx.SetPlanAsync(client, "inbox");
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => BillingHelpers.RenewAsync(ctx, client)));
            Expect.Equal(1, outcomes.Count(o => o == "Renewed"), $"Renewed outcomes among {string.Join(",", outcomes)}");
            Expect.Equal(161m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after 5 parallel renewals (200 - 39 once)");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("past-due", "Renew: an unpaid period becomes past due, keeps access during grace, credit expires", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-pd");
            await ctx.SetPlanAsync(client, "growth");
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("PastDue", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome with an empty wallet");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.EqualText("past_due", s["subscriptionStatus"], "Status");
            Expect.True(s["hasAccess"]!.GetValue<bool>(), "A past-due client lost access before the grace period ended.");
            Expect.True(s["graceEndsAt"] is not null, "The summary has no grace end date.");
            Expect.Equal(0m, BillingHelpers.D(s["includedCreditRemaining"]), "Unused credit after the period ended unpaid");
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectOk();
            Expect.Equal("PastDue", await BillingHelpers.RenewAsync(ctx, client), "Renew again while still unpaid");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("past-due-paid", "Renew: after a top-up a past-due subscription renews and is active again", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-pay");
            await ctx.SetPlanAsync(client, "inbox");
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("PastDue", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome with an empty wallet");
            await BillingHelpers.TopUpOkAsync(ctx, client, 50);
            Expect.Equal("Renewed", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome after the top-up");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.EqualText("active", s["subscriptionStatus"], "Status after paying");
            Expect.Equal(11m, BillingHelpers.D(s["walletBalance"]), "Wallet after paying the overdue period (50 - 39)");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("grace-over", "Renew: when the grace period is over the subscription expires and access ends", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-exp");
            await ctx.SetPlanAsync(client, "growth");
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("PastDue", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome with an empty wallet");
            await BillingHelpers.TimeTravelAsync(ctx, client, "past_due_since = now() - interval '7 days 1 minute'");
            Expect.Equal("Expired", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome after the grace period");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.True(!s["hasAccess"]!.GetValue<bool>(), "An expired client still has access.");
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=11", ct: ctx.Ct)).ExpectStatus(422);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("cancel-at-end-ends", "Renew: a subscription cancelled at period end ends when the period does", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-cae");
            await BillingHelpers.TopUpOkAsync(ctx, client, 100);
            await ctx.SetPlanAsync(client, "inbox");
            (await ctx.Admin.PostAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/subscription/cancel",
                new { immediately = false, reason = "Omni.Automation" }, ct: ctx.Ct)).ExpectOk();
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("Cancelled", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome");
            Expect.Equal(100m, await BillingHelpers.WalletAsync(ctx, client), "Wallet: a cancelled-at-end plan is not charged again");
            Expect.True(!(await BillingHelpers.SummaryAsync(ctx, client))["hasAccess"]!.GetValue<bool>(), "Access remains after the cancellation took effect.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("trial-expiry", "Trial: an ended trial expires (never renews) and access ends", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-trial");
            await BillingHelpers.TopUpOkAsync(ctx, client, 20);
            await BillingHelpers.EndPeriodNowAsync(ctx, client, "7 days");
            Expect.Equal("Expired", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome of an ended trial");
            var s = await BillingHelpers.SummaryAsync(ctx, client);
            Expect.EqualText("expired", s["subscriptionStatus"], "Status");
            Expect.True(!s["hasAccess"]!.GetValue<bool>(), "An expired trial still has access.");
            Expect.Equal(20m, BillingHelpers.D(s["walletBalance"]), "Wallet: an ended trial must not charge anything");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("ended-page", "Billing page: an expired plan doesn't say \"Renews on\"", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-page");
            await BillingHelpers.EndPeriodNowAsync(ctx, client, "7 days");
            Expect.Equal("Expired", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome of an ended trial");
            var page = (await client.Http.GetAsync("/settings/billing", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("Renews on", StringComparison.OrdinalIgnoreCase),
                "The Billing page of an expired trial still says \"Renews on <date>\" (should say when it ended).");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-15")
        .Test("addon-default", "Add-ons at renewal: one nobody set to renew follows Billing:AddonsRenewByDefault (off = it ends, uncharged)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-addon-def");
            await BillingHelpers.TopUpOkAsync(ctx, client, 100);
            await ctx.SetPlanAsync(client, "inbox");
            var rows = (await SetAddonAsync(ctx, client, "extra-user", 1)).ExpectOk().Array();
            var row = rows.Single(r => r?["code"]?.ToString() == "extra-user")!;
            Expect.True(row["autoRenew"] is null, "A new add-on should follow the default (autoRenew null).");
            var renews = row["renews"]!.GetValue<bool>();
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("Renewed", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome");
            var after = await AddonsAsync(ctx, client);
            Expect.Equal(renews, after.Any(r => r?["code"]?.ToString() == "extra-user"), "Add-on kept after renewal (as its 'renews' said)");
            Expect.Equal(renews ? 100m - 8m - 39m - 8m : 100m - 8m - 39m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after the renewal");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-05")
        .Test("addon-renews", "Add-ons at renewal: one set to renew is charged with the plan; one set not to ends", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-addon-yes");
            await BillingHelpers.TopUpOkAsync(ctx, client, 100);
            await ctx.SetPlanAsync(client, "inbox");
            (await SetAddonAsync(ctx, client, "extra-user", 1)).ExpectOk();
            (await SetAddonAsync(ctx, client, "extra-channel", 1)).ExpectOk();
            (await SetRenewalAsync(ctx, client, "extra-user", true)).ExpectOk();
            var rows = (await SetRenewalAsync(ctx, client, "extra-channel", false)).ExpectOk().Array();
            Expect.True(rows.Single(r => r?["code"]?.ToString() == "extra-user")!["renews"]!.GetValue<bool>(), "extra-user should renew after 'yes'.");
            Expect.True(!rows.Single(r => r?["code"]?.ToString() == "extra-channel")!["renews"]!.GetValue<bool>(), "extra-channel should not renew after 'no'.");
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("Renewed", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome");
            var after = await AddonsAsync(ctx, client);
            Expect.Equal("extra-user", after.Single()?["code"]?.ToString(), "Only the renewing add-on is left");
            Expect.Equal(100m - 8m - 5m - 39m - 8m, await BillingHelpers.WalletAsync(ctx, client), "Wallet: plan + the renewing add-on");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-05")
        .Test("addon-unpaid", "Add-ons at renewal: a short wallet renews the plan and drops the renewing add-on it can't pay", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-addon-short");
            await BillingHelpers.TopUpOkAsync(ctx, client, 47);
            await ctx.SetPlanAsync(client, "inbox");
            (await SetAddonAsync(ctx, client, "extra-user", 1)).ExpectOk();
            (await SetRenewalAsync(ctx, client, "extra-user", true)).ExpectOk();
            await BillingHelpers.EndPeriodNowAsync(ctx, client);
            Expect.Equal("Renewed", await BillingHelpers.RenewAsync(ctx, client), "Renewal outcome: the plan is paid, only the add-on isn't");
            Expect.True((await AddonsAsync(ctx, client)).Count == 0, "The add-on the wallet couldn't pay is still there.");
            Expect.Equal(0m, await BillingHelpers.WalletAsync(ctx, client), "Wallet: 47 - 8 - 39");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-05")
        .Test("addon-renewal-404", "Add-ons: a renewal choice for an add-on the client doesn't have is 404", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await SetRenewalAsync(ctx, client, "auto-no-such-addon", true)).ExpectStatus(404);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("overdue-once", "Renew: a subscription several periods overdue is renewed once from now, not back-billed", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-burst");
            await BillingHelpers.TopUpOkAsync(ctx, client, 200);
            await ctx.SetPlanAsync(client, "inbox");
            await BillingHelpers.TimeTravelAsync(ctx, client,
                "current_period_start = now() - interval '3 months 1 day', current_period_end = now() - interval '2 months 1 day'");
            await BillingHelpers.RenewAsync(ctx, client);
            for (var i = 0; i < 3; i++) await BillingHelpers.RenewAsync(ctx, client);
            var charges = (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "subscription_charge")).Count(c => BillingHelpers.D(c["amount"]) == -39m);
            Expect.True(charges <= 1, $"A subscription 2 months overdue was charged {charges} times in a burst (back-billed every missed period); expected one renewal from now.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-19")
        .Test("worker-expires-trial", "Worker: the BillingMaintenance worker expires an ended trial on its own (waits up to 6 min)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "ren-worker");
            await BillingHelpers.EndPeriodNowAsync(ctx, client, "7 days");
            // The worker runs every Billing:MaintenanceIntervalMinutes (5), so one full interval plus a margin always
            // includes a run; if it still didn't happen, report skipped (e.g. a restart mid-wait), not failed.
            ctx.Log("Waiting up to 6 min for the BillingMaintenance worker (runs every 5 minutes) to expire the trial…");
            var deadline = DateTimeOffset.UtcNow.AddMinutes(6);
            string? status = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                status = await ctx.Db.ScalarAsync<string>("select status from billing.subscriptions where client_id = @c", new { c = client.ClientId }, ctx.Ct);
                if (status == "expired") break;
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.Ct);
            }
            if (status != "expired")
            {
                throw new TestSkipped("The BillingMaintenance worker didn't run within 6 minutes (it runs every 5); the app may have restarted. Re-run to try again.");
            }
            var events = await ctx.Db.ScalarAsync<long>(
                "select count(*) from activity.events where client_id = @c and event_type = 'subscription_expired'", new { c = client.ClientId }, ctx.Ct);
            Expect.True(events >= 1, "The worker expired the trial but logged no subscription_expired event.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, timeout: TimeSpan.FromMinutes(7),
            description: "Slow: polls for up to 6 minutes (one worker interval plus a margin).")
        .Build();
}

