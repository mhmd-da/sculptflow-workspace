using System.Globalization;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(23)]
public static class BillingWalletSuite
{
    public static TestSuite Build() => new SuiteBuilder("billing-wallet", "Billing: wallet top-ups and adjustments",
            "Idempotency-Key replay and conflict, concurrency on money writes, validation, the ledger and reconciliation.")
        .Test("needs-key", "Top-up: no Idempotency-Key is 400", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 10 }, null)).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("top-up", "Top-up: money lands in the wallet with actor, reason and reference in the ledger", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-top");
            var r = (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 25.5m, reference = $"AUTO-{ctx.Tag}", reason = "Automation payment" },
                BillingHelpers.Key(ctx, "t"))).ExpectOk();
            Expect.Equal(25.5m, BillingHelpers.D(r.Prop("balanceAfter")), "balanceAfter");
            Expect.True(!r.Prop("duplicate").GetValue<bool>(), "A first top-up was reported as a duplicate.");
            var row = (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "wallet_top_up")).Single();
            Expect.EqualText("omni-automation", row["actor"], "Ledger actor");
            Expect.EqualText("Automation payment", row["reason"], "Ledger reason");
            Expect.EqualText($"AUTO-{ctx.Tag}", row["reference"], "Ledger reference");
            Expect.Equal(25.5m, BillingHelpers.D((await client.Http.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk().Prop("walletBalance")),
                "Wallet the client sees");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("replay", "Top-up: replaying the same key (also padded with spaces) posts once and says duplicate", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-replay");
            var key = BillingHelpers.KeyValue(ctx, "t");
            var body = new { amount = 10, reason = "Automation replay" };
            (await BillingHelpers.TopUpAsync(ctx, client, body, new() { ["Idempotency-Key"] = key })).ExpectOk();
            var again = (await BillingHelpers.TopUpAsync(ctx, client, body, new() { ["Idempotency-Key"] = key })).ExpectOk();
            Expect.True(again.Prop("duplicate").GetValue<bool>(), "The replay was not reported as a duplicate.");
            (await BillingHelpers.TopUpAsync(ctx, client, body, new() { ["Idempotency-Key"] = $"  {key}  " })).ExpectOk();
            Expect.Equal(10m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after three sends of one top-up");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("replay-conflict", "Top-up: the same key with a different amount is 422", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-conflict");
            var key = BillingHelpers.Key(ctx, "t");
            (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 10 }, key)).ExpectOk();
            (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 11 }, key)).ExpectStatus(422);
            Expect.Equal(10m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a conflicting replay");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("parallel-same-key", "Top-up: 10 parallel requests with one key post once", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-par1");
            var key = BillingHelpers.Key(ctx, "t");
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
                BillingHelpers.TopUpAsync(ctx, client, new { amount = 5, reason = "Automation parallel same key" }, key)));
            Expect.True(results.Count(r => r.Status == 200) >= 1, $"No parallel top-up succeeded: {string.Join(",", results.Select(r => r.Status))}.");
            Expect.Equal(5m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after 10 parallel same-key top-ups of 5");
            Expect.Equal(1, (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "wallet_top_up")).Count, "Top-up ledger rows");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing | Risk.Shared, description: "A small burst of 10 requests.")
        .Test("parallel-distinct-keys", "Top-up: 10 parallel requests with distinct keys all post", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-par2");
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
                BillingHelpers.TopUpAsync(ctx, client, new { amount = 1, reason = "Automation parallel distinct" }, BillingHelpers.Key(ctx, $"p{i}"))));
            Expect.True(results.All(r => r.Status == 200), $"Statuses: {string.Join(",", results.Select(r => r.Status))}.");
            Expect.Equal(10m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after 10 parallel top-ups of 1");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing | Risk.Shared, description: "A small burst of 10 requests.")
        .Test("invalid-amounts", "Top-up: zero, negative, text and missing amounts are 400", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            foreach (var (what, json) in new[] { ("0", "{\"amount\":0}"), ("-5", "{\"amount\":-5}"), ("\"abc\"", "{\"amount\":\"abc\"}"), ("null", "{\"amount\":null}") })
            {
                var r = await ctx.Admin.PostRawAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/wallet/top-ups", json, "application/json",
                    BillingHelpers.Key(ctx, "bad"), ctx.Ct);
                Expect.True(r.Status == 400, $"Top-up of {what}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
            Expect.Equal(0m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after refused top-ups");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("long-key", "Top-up: an Idempotency-Key over 150 characters is 400", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 1 }, new() { ["Idempotency-Key"] = new string('x', 151) })).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("rounds-to-zero", "Top-up: an amount that rounds to 0 (0.0000001) is 400, not 500", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var r = await BillingHelpers.TopUpAsync(ctx, client, new { amount = 0.0000001m }, BillingHelpers.Key(ctx, "tiny"));
            Expect.True(r.Status == 400, $"A top-up of 0.0000001 answered {r.Status} (expected 400 with a clear message).{r.Snippet(150)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-05")
        .Test("overflow", "Top-up: an absurd amount (1,000,000,000,000) is 400, not 500", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-huge");
            var r = await BillingHelpers.TopUpAsync(ctx, client, new { amount = 1000000000000m }, BillingHelpers.Key(ctx, "huge"));
            Expect.True(r.Status == 400, $"A top-up of 1,000,000,000,000 answered {r.Status} (expected 400: amount above a sane maximum).{r.Snippet(150)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-05",
            description: "1e12 overflows numeric(18,6) today, so nothing is stored; 999,999,999,999 would be stored on an empty wallet and inflate the month's report.")
        .Test("sub-cent", "Top-up: wallet money keeps currency precision (0.1234567 is refused or stored as 0.12)", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-cent");
            var r = await BillingHelpers.TopUpAsync(ctx, client, new { amount = 0.1234567m }, BillingHelpers.Key(ctx, "cent"));
            if (r.Status == 400) return;
            r.ExpectOk();
            var wallet = await BillingHelpers.WalletAsync(ctx, client);
            Expect.True(wallet == decimal.Round(wallet, 2), $"A top-up of 0.1234567 was stored as {wallet} (more than 2 decimals of wallet money).");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-23")
        .Test("text-reason", "Top-up: HTML, Arabic and emoji in reason/reference are stored as text", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-text");
            const string reason = "<img src=x onerror=alert(1)> عربي 😀";
            (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 1, reference = "<script>alert(1)</script>", reason },
                BillingHelpers.Key(ctx, "t"))).ExpectOk();
            var row = (await BillingHelpers.LedgerOfTypeAsync(ctx, client, "wallet_top_up")).Single();
            Expect.EqualText(reason, row["reason"], "Stored reason");
            var page = (await client.Http.GetAsync("/settings/billing", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("<img src=x onerror", StringComparison.OrdinalIgnoreCase), "The Billing page renders the reason as raw HTML.");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("adjust-validation", "Adjustment: missing reason, zero, unknown or missing balance type are 400", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var cases = new (string What, object Body)[]
            {
                ("no reason", new { amount = 5, balanceType = "wallet" }),
                ("zero", new { amount = 0, balanceType = "wallet", reason = "x" }),
                ("unknown type", new { amount = 5, balanceType = "gold", reason = "x" }),
                ("no type", new { amount = 5, reason = "x" }),
            };
            foreach (var (what, body) in cases)
            {
                var r = await BillingHelpers.AdjustAsync(ctx, client, body, BillingHelpers.Key(ctx, "a"));
                Expect.True(r.Status == 400, $"Adjustment with {what}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 5, balanceType = "wallet", reason = "x" }, null)).ExpectStatus(400);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("adjust", "Adjustment: credit and debit the wallet; balance type is case- and space-insensitive", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-adj");
            await BillingHelpers.TopUpOkAsync(ctx, client, 10);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -4, balanceType = " WALLET ", reason = "Automation debit" }, BillingHelpers.Key(ctx, "a"))).ExpectOk();
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 3, balanceType = "included_credit", reason = "Automation goodwill credit" }, BillingHelpers.Key(ctx, "a"))).ExpectOk();
            Expect.Equal(6m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after -4");
            Expect.Equal(3m, await BillingHelpers.CreditAsync(ctx, client), "Credit after +3");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("overdraw", "Adjustment: a debit beyond the wallet or the credit is 422 and changes nothing", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-over");
            await BillingHelpers.TopUpOkAsync(ctx, client, 10);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 3, balanceType = "included_credit", reason = "Automation credit" }, BillingHelpers.Key(ctx, "a"))).ExpectOk();
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -1000, balanceType = "wallet", reason = "Automation overdraw" }, BillingHelpers.Key(ctx, "a"))).ExpectStatus(422);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -4, balanceType = "included_credit", reason = "Automation overdraw credit" }, BillingHelpers.Key(ctx, "a"))).ExpectStatus(422);
            Expect.Equal(10m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after refused debits");
            Expect.Equal(3m, await BillingHelpers.CreditAsync(ctx, client), "Credit after refused debits");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("adjust-replay-conflict", "Adjustment: the same key with another balance type is 422", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-adjrep");
            var key = BillingHelpers.Key(ctx, "a");
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 3, balanceType = "included_credit", reason = "Automation" }, key)).ExpectOk();
            var replay = (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 3, balanceType = "included_credit", reason = "Automation" }, key)).ExpectOk();
            Expect.True(replay.Prop("duplicate").GetValue<bool>(), "The adjustment replay was not reported as a duplicate.");
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 3, balanceType = "wallet", reason = "Automation" }, key)).ExpectStatus(422);
            Expect.Equal(3m, await BillingHelpers.CreditAsync(ctx, client), "Credit after the replays");
            Expect.Equal(0m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after the conflicting replay");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("key-namespaces", "Idempotency: one key used for a top-up and an adjustment counts as two operations", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-ns");
            var key = BillingHelpers.Key(ctx, "shared");
            (await BillingHelpers.TopUpAsync(ctx, client, new { amount = 2, reason = "Automation" }, key)).ExpectOk();
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = 2, balanceType = "wallet", reason = "Automation" }, key)).ExpectOk();
            Expect.Equal(4m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a top-up and an adjustment sharing a key");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("parallel-debits", "Adjustment: 5 parallel debits where only 2 fit — 2 succeed, the wallet never goes negative", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-debit");
            await BillingHelpers.TopUpOkAsync(ctx, client, 30);
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
                BillingHelpers.AdjustAsync(ctx, client, new { amount = -10.01m, balanceType = "wallet", reason = $"Automation parallel debit {i}" }, BillingHelpers.Key(ctx, $"d{i}"))));
            var statuses = string.Join(",", results.Select(r => r.Status));
            Expect.Equal(2, results.Count(r => r.Status == 200), $"Successful debits ({statuses})");
            Expect.Equal(3, results.Count(r => r.Status == 422), $"Refused debits ({statuses})");
            Expect.Equal(9.98m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after the parallel debits (30 - 2 x 10.01)");
            await BillingHelpers.ExpectBalancedAsync(ctx, client);
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing | Risk.Shared, description: "A small burst of 5 requests.")
        .Test("adjust-rounds-to-zero", "Adjustment: an amount that rounds to 0 (0.0000001) is 400, not 500", async ctx =>
        {
            var client = await BillingHelpers.ReadOnlyClientAsync(ctx);
            var r = await BillingHelpers.AdjustAsync(ctx, client, new { amount = 0.0000001m, balanceType = "wallet", reason = "Automation tiny" }, BillingHelpers.Key(ctx, "tiny"));
            Expect.True(r.Status == 400, $"An adjustment of 0.0000001 answered {r.Status} (expected 400).{r.Snippet(150)}");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t2-billing-05")
        .Test("client-transactions", "Ledger: the client sees its top-ups and adjustments; the admin ledger pages", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-tx");
            await BillingHelpers.TopUpOkAsync(ctx, client, 5);
            (await BillingHelpers.AdjustAsync(ctx, client, new { amount = -1, balanceType = "wallet", reason = "Automation" }, BillingHelpers.Key(ctx, "a"))).ExpectOk();
            var tx = (await client.Http.GetAsync("/api/billing/transactions?pageSize=50", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.True(tx.Any(t => t?["entryType"]?.ToString() == "wallet_top_up"), "The client doesn't see its top-up.");
            Expect.True(tx.Any(t => t?["entryType"]?.ToString() == "manual_adjustment"), "The client doesn't see the adjustment.");
            var page = (await ctx.Admin.GetAsync($"{BillingHelpers.Base}/clients/{client.ClientId}/ledger?page=1&pageSize=1", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(1, page.Array("items").Count, "Ledger rows on a page of size 1");
            Expect.True(page.Prop("total").GetValue<int>() >= 2, "Ledger total is lower than the posted rows.");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("ledger-no-update", "Ledger: a posted row can't be edited in the database", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-immut");
            await BillingHelpers.TopUpOkAsync(ctx, client, 5);
            try
            {
                await ctx.Db.ExecuteForClientAsync(client.ClientId, "update billing.ledger_entries set amount = 500 where client_id = @clientId", ct: ctx.Ct);
            }
            catch (Npgsql.PostgresException)
            {
                Expect.Equal(5m, await BillingHelpers.WalletAsync(ctx, client), "Wallet after a refused ledger edit");
                return;
            }
            throw new TestFailure("UPDATE of billing.ledger_entries was allowed: the ledger is not append-only.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing)
        .Test("ledger-no-delete", "Ledger: a posted row can't be deleted in the database", async ctx =>
        {
            var client = await BillingHelpers.NewClientAsync(ctx, "wal-nodel");
            await BillingHelpers.TopUpOkAsync(ctx, client, 5);
            int deleted;
            try
            {
                deleted = await ctx.Db.ExecuteForClientAsync(client.ClientId,
                    "delete from billing.ledger_entries where client_id = @clientId and entry_type = 'wallet_top_up'", ct: ctx.Ct);
            }
            catch (Npgsql.PostgresException)
            {
                return;
            }
            throw new TestFailure($"DELETE on billing.ledger_entries removed {deleted} row(s): only UPDATE is blocked, so ledger history can be erased.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, risk: Risk.Billing, finding: "t2-billing-20",
            description: "Deletes the test client's own top-up row when the bug is present; the client is deleted with the run.")
        .Build();
}

