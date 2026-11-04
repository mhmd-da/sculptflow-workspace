using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(36)]
public static class OpsDashboardSuite
{
    public static TestSuite Build() => new SuiteBuilder("ops-dashboard", "Operations: dashboard and reports",
            "Dashboard API figures, the dashboard and reports pages with every period option.")
        .Test("summary-counts", "Dashboard: a new booking is counted in \"bookings made\"", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("o"));
            var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("o"));
            var path = $"/api/dashboard/summary?from={from}&to={to}";
            var before = (await client.Http.GetAsync(path, ct: ctx.Ct)).ExpectOk().Prop("bookingsMade").GetValue<int>();
            await OpsHelpers.BookOkAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "dash"), OpsHelpers.At(9, 10));
            var after = (await client.Http.GetAsync(path, ct: ctx.Ct)).ExpectOk().Prop("bookingsMade").GetValue<int>();
            Expect.Equal(before + 1, after, "Bookings made after one more booking");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("api-lists", "Dashboard: attention, contacts, bookings and services endpoints answer", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            (await client.Http.GetAsync("/api/dashboard/attention", ct: ctx.Ct)).ExpectOk();
            (await client.Http.GetAsync("/api/dashboard/contacts?take=500&skip=-1", ct: ctx.Ct)).ExpectOk().Array("items");
            (await client.Http.GetAsync("/api/dashboard/bookings?search=Auto&take=5", ct: ctx.Ct)).ExpectOk().Array("items");
            (await client.Http.GetAsync("/api/dashboard/services", ct: ctx.Ct)).ExpectOk().Array();
            var swapped = $"/api/dashboard/summary?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("o"))}";
            Expect.True((await client.Http.GetAsync(swapped, ct: ctx.Ct)).Status < 500, "A reversed summary range crashed.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("dashboard-periods", "Dashboard page: all, this month, custom (also reversed) and an unknown period load", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            foreach (var q in new[] { "Period=all", "Period=this_month", "Period=custom&From=2026-12-01&To=2026-11-01", "Period=custom&From=abc&To=", "Period=bogus" })
            {
                (await client.Http.GetAsync($"/dashboard?{q}", ct: ctx.Ct)).ExpectOk();
            }
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("dashboard-extreme", "Dashboard page: a custom period from 1900 to 9999 doesn't crash", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var r = await client.Http.GetAsync("/dashboard?Period=custom&From=1900-01-01&To=9999-12-31", ct: ctx.Ct);
            Expect.True(r.Status == 200, $"/dashboard with a 1900-9999 custom period answered {r.Status} (ArgumentOutOfRangeException); the dates should be clamped or rejected.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-15")
        .Test("reports-advanced", "Reports: business gets the advanced reports for every period option", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var all = (await client.Http.GetAsync("/reports?Period=all", ct: ctx.Ct)).ExpectOk();
            all.ExpectBodyContains("Response times");
            Expect.True(!all.Body.Contains("come with advanced reporting"), "Advanced reports are locked on the business plan.");
            foreach (var q in new[] { "Period=this_month", "Period=custom&From=2026-12-01&To=2026-11-01", "Period=custom&From=abc&To=", "Period=bogus" })
            {
                (await client.Http.GetAsync($"/reports?{q}", ct: ctx.Ct)).ExpectOk();
            }
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("signed-out", "Dashboard: signed out is 401 for the API and the sign-in page for the pages", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/dashboard/summary", ct: ctx.Ct)).ExpectStatus(401);
            var page = await ctx.Anonymous.GetAsync("/reports", ct: ctx.Ct);
            Expect.True(page.Status == 302 && (page.Location ?? "").Contains("/Account/Login", StringComparison.OrdinalIgnoreCase),
                $"Signed-out /reports answered {page.Status} {page.Location}.");
        }, needs: Needs.None)
        .Build();
}

[Suite(37)]
public static class OpsOrdersSuite
{
    private static Task<ApiResponse> CreateAsync(TestContext ctx, TestClient client, object body) => client.Http.PostAsync("/api/orders", body, ct: ctx.Ct);

    public static TestSuite Build() => new SuiteBuilder("ops-orders", "Operations: orders",
            "Orders API (/api/orders): create, status, validation of amounts and currency, and references to other clients' data.")
        .Test("create-confirm", "Orders: create, list, and confirming moves the contact on and logs order_confirmed", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ord");
            var order = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId, quotedAmount = 120.5m, currencyCode = "USD", notes = "Automation" }))
                .ExpectStatus(201);
            Expect.EqualText("considering", order.Prop("status"), "Status of a new order");
            var id = order.Id();
            Expect.ContainsId((await client.Http.GetAsync("/api/orders?status=considering&take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "Orders with status considering");
            var stageBefore = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            Expect.EqualText("confirmed", (await client.Http.PatchAsync($"/api/orders/{id}", new { status = "confirmed", finalAmount = 100 }, ctx.Ct)).ExpectOk().Prop("status"), "Status after confirming");
            var stageAfter = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            Expect.True(stageAfter != stageBefore, $"The contact stayed in stage {stageBefore} after its order was confirmed.");
            var events = await ctx.Db.ScalarAsync<long>("select count(*) from activity.events where contact_id = @c and event_type = 'order_confirmed'", new { c = contact }, ctx.Ct);
            Expect.Equal(1L, events, "order_confirmed events");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing)
        .Test("status-and-refs", "Orders: unknown status 400, unknown order 404, other client's service or contact 400", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var other = await ctx.OtherClientAsync();
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ordref");
            var id = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId })).ExpectStatus(201).Id();
            (await client.Http.PatchAsync($"/api/orders/{id}", new { status = "shipped" }, ctx.Ct)).ExpectStatus(400);
            (await client.Http.PatchAsync($"/api/orders/{Guid.NewGuid()}", new { status = "quoted" }, ctx.Ct)).ExpectStatus(404);
            (await CreateAsync(ctx, client, new { contactId = contact, serviceId = other.BaseServiceId })).ExpectStatus(400);
            (await CreateAsync(ctx, client, new { contactId = await OpsHelpers.ContactAsync(ctx, other, "ordref-foreign"), serviceId = client.BaseServiceId })).ExpectStatus(400);
            (await CreateAsync(ctx, client, new { contactId = Guid.NewGuid(), serviceId = client.BaseServiceId })).ExpectStatus(400);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("contact-page", "Orders: the contact page lists a contact's orders, ?order=new opens the add popup, and its form adds one", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ordpage");
            var order = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId, quotedAmount = 99.5m, currencyCode = "USD" })).ExpectStatus(201).Id();
            var path = $"/dashboard/contacts/{contact}";
            var page = (await client.Http.GetAsync(path, ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains("id=\"orders\"") && page.Body.Contains($"data-order-id=\"{order}\""),
                "The contact page has no Orders panel listing the contact's order (orders have no UI).");
            var opened = (await client.Http.GetAsync(path + "?order=new", ct: ctx.Ct)).ExpectOk();
            Expect.True(System.Text.RegularExpressions.Regex.IsMatch(opened.Body, "id=\"order-overlay\"(?![^>]*hidden)"), "?order=new (the Inbox's Create order link) doesn't open the add popup.");
            var added = await client.Http.SubmitFormAsync(path, new Dictionary<string, string>
            {
                ["OrderServiceId"] = client.BaseServiceId.ToString(), ["OrderQuoted"] = "120", ["OrderCurrency"] = "usd",
            }, handler: "Order", ct: ctx.Ct);
            Expect.Equal(302, added.Status, "Adding an order from the contact page");
            var list = (await client.Http.GetAsync("/api/orders?take=200", ct: ctx.Ct)).ExpectOk().Array("items")
                .Where(o => o?["contactId"]?.ToString() == contact.ToString()).ToList();
            Expect.True(list.Count == 2 && list.Any(o => o?["currencyCode"]?.ToString() == "USD" && o?["quotedAmount"]?.GetValue<decimal>() == 120m),
                $"After the form the contact has {list.Count} order(s); expected the new 120 USD one too.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t3-inbox-10")
        .Test("isolation", "Orders: another client's order is 404 to change and missing from the list", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var other = await ctx.OtherClientAsync();
            var foreign = (await CreateAsync(ctx, other, new { contactId = await OpsHelpers.ContactAsync(ctx, other, "ordiso"), serviceId = other.BaseServiceId }))
                .ExpectStatus(201).Id();
            (await client.Http.PatchAsync($"/api/orders/{foreign}", new { status = "lost" }, ctx.Ct)).ExpectStatus(404);
            Expect.LacksId((await client.Http.GetAsync("/api/orders?take=200", ct: ctx.Ct)).ExpectOk().Array("items"), foreign, "Orders list");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("foreign-booking", "Orders: another client's booking can't be linked to an order", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var other = await OpsHelpers.BookingClientAsync(ctx, "ops-b");
            var foreignBooking = await OpsHelpers.BookOkAsync(ctx, other, await OpsHelpers.ContactAsync(ctx, other, "ordbk"), OpsHelpers.At(10, 10));
            var r = await CreateAsync(ctx, client, new { contactId = await OpsHelpers.ContactAsync(ctx, client, "ordbk"), serviceId = client.BaseServiceId, bookingId = foreignBooking });
            Expect.True(r.Status == 400, $"An order linked to another client's booking answered {r.Status} (a cross-tenant reference was stored); expected 400 \"Booking not found\".");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t1-accounts-01")
        .Test("unknown-booking", "Orders: an unknown booking id is 400, the same answer as a foreign one (not 500)", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var r = await CreateAsync(ctx, client, new { contactId = await OpsHelpers.ContactAsync(ctx, client, "ordunk"), serviceId = client.BaseServiceId, bookingId = Guid.NewGuid() });
            Expect.True(r.Status == 400, $"An order with a random booking id answered {r.Status} (500 lets anyone probe which booking ids exist); expected 400.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t1-accounts-01,t3-inbox-09")
        .Test("negative-amounts", "Orders: negative amounts are 400 on create and update", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ordneg");
            var create = await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId, quotedAmount = -10 });
            var id = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId })).ExpectStatus(201).Id();
            var patch = await client.Http.PatchAsync($"/api/orders/{id}", new { depositAmount = -5 }, ctx.Ct);
            Expect.True(create.Status == 400 && patch.Status == 400, $"quotedAmount -10 answered {create.Status}, depositAmount -5 answered {patch.Status}; expected 400 for both.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t3-inbox-09")
        .Test("currency", "Orders: invalid currency codes are 400 (not stored, not a 500)", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ordcur");
            var lower = await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId, quotedAmount = 10, currencyCode = "xyz" });
            var five = await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId, quotedAmount = 10, currencyCode = "DOLLR" });
            Expect.True(lower.Status == 400 && five.Status == 400, $"Currency \"xyz\" answered {lower.Status}, \"DOLLR\" answered {five.Status}; expected 400 (ISO 4217).");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t3-inbox-09")
        .Test("huge-amount", "Orders: an amount too big for the column is 400, not 500", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var r = await CreateAsync(ctx, client, new { contactId = await OpsHelpers.ContactAsync(ctx, client, "ordhuge"), serviceId = client.BaseServiceId, quotedAmount = 1e15 });
            Expect.True(r.Status == 400, $"quotedAmount 1e15 answered {r.Status}; expected 400.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t3-inbox-09")
        .Test("status-moves", "Orders: a completed order is final (422), a canceled one can be reopened", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ordmove");
            var done = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId })).ExpectStatus(201).Id();
            (await client.Http.PatchAsync($"/api/orders/{done}", new { status = "completed", finalAmount = 50 }, ctx.Ct)).ExpectOk();
            var cancel = await client.Http.PatchAsync($"/api/orders/{done}", new { status = "canceled" }, ctx.Ct);
            Expect.True(cancel.Status == 422, $"completed -> canceled answered {cancel.Status}; expected 422 (completed is final).");
            (await client.Http.PatchAsync($"/api/orders/{done}", new { status = "completed", notes = "same status again" }, ctx.Ct)).ExpectOk();

            var other = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId })).ExpectStatus(201).Id();
            (await client.Http.PatchAsync($"/api/orders/{other}", new { status = "canceled" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("considering", (await client.Http.PatchAsync($"/api/orders/{other}", new { status = "considering" }, ctx.Ct)).ExpectOk().Prop("status"), "Status after reopening");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t3-inbox-09")
        .Test("cancel-stage", "Orders: canceling a contact's only confirmed order takes them out of the customer stage", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "ordcancel");
            var id = (await CreateAsync(ctx, client, new { contactId = contact, serviceId = client.BaseServiceId })).ExpectStatus(201).Id();
            (await client.Http.PatchAsync($"/api/orders/{id}", new { status = "confirmed" }, ctx.Ct)).ExpectOk();
            var customer = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            (await client.Http.PatchAsync($"/api/orders/{id}", new { status = "canceled" }, ctx.Ct)).ExpectOk();
            var after = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            Expect.True(after != customer, $"The contact stayed in stage {customer} after their only confirmed order was canceled.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t3-inbox-09")
        .Build();
}
