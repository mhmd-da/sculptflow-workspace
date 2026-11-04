using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(30)]
public static class OpsServicesSuite
{
    private static Task<ApiResponse> Create(TestContext ctx, TestClient client, object body) => client.Http.PostAsync("/api/services", body, ct: ctx.Ct);

    private static string Name(TestContext ctx, string label) => $"Auto {label} {ctx.Tag} {Guid.NewGuid().ToString("N")[..6]}";

    public static TestSuite Build() => new SuiteBuilder("ops-services", "Operations: services",
            "Services CRUD and validation (/api/services), isolation and safe rendering.")
        .Test("crud", "Services: create, read, update, deactivate and reactivate", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var name = Name(ctx, "svc");
            var created = (await Create(ctx, client, new { name, code = $"c-{ctx.Tag}", description = "First visit", bookingDuration = 45 })).ExpectStatus(201);
            var id = created.Id();
            Expect.EqualText("45", created.Prop("bookingDuration"), "Duration");
            Expect.EqualText(client.ClientId.ToString(), created.Prop("clientId"), "Owner client");
            (await client.Http.GetAsync($"/api/services/{id}", ct: ctx.Ct)).ExpectOk();
            var updated = (await client.Http.PutAsync($"/api/services/{id}", new { name = name + " v2", bookingDuration = 60 }, ctx.Ct)).ExpectOk();
            Expect.EqualText(name + " v2", updated.Prop("name"), "Name after update");
            var off = (await client.Http.PostAsync($"/api/services/{id}/active", new { isActive = false }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("false", off.Prop("isActive"), "isActive after deactivating");
            Expect.LacksId((await client.Http.GetAsync("/api/services", ct: ctx.Ct)).ExpectOk().Array(), id, "Active services list");
            Expect.ContainsId((await client.Http.GetAsync("/api/services?activeOnly=false", ct: ctx.Ct)).ExpectOk().Array(), id, "All services list");
            (await client.Http.PostAsync($"/api/services/{id}/active", new { isActive = true }, ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId((await client.Http.GetAsync("/api/services", ct: ctx.Ct)).ExpectOk().Array(), id, "Active services list after reactivating");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("name-validation", "Services: empty, blank, missing and over-200-character names are 400; 200 characters is fine", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            foreach (var (what, json) in new[] { ("empty", "{\"name\":\"\",\"bookingDuration\":30}"), ("spaces", "{\"name\":\"   \",\"bookingDuration\":30}"),
                         ("null", "{\"name\":null,\"bookingDuration\":30}"), ("201 chars", $"{{\"name\":\"{new string('x', 201)}\",\"bookingDuration\":30}}") })
            {
                var r = await client.Http.PostRawAsync("/api/services", json, "application/json", ct: ctx.Ct);
                Expect.True(r.Status == 400, $"Service name {what}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
            (await Create(ctx, client, new { name = (Name(ctx, "long") + new string('y', 200))[..200], bookingDuration = 30 })).ExpectStatus(201);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("duration-validation", "Services: duration 0, -5, 481 and text are 400; 480 is fine", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            foreach (var duration in new[] { "0", "-5", "481", "\"abc\"", "99999999999" })
            {
                var r = await client.Http.PostRawAsync("/api/services", $"{{\"name\":\"{Name(ctx, "dur")}\",\"bookingDuration\":{duration}}}", "application/json", ct: ctx.Ct);
                Expect.True(r.Status == 400, $"Duration {duration}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
            (await Create(ctx, client, new { name = Name(ctx, "d480"), bookingDuration = 480 })).ExpectStatus(201);
            (await Create(ctx, client, new { name = Name(ctx, "nodur") })).ExpectStatus(201);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("field-lengths", "Services: description over 500 and code over 100 characters are 400", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            (await Create(ctx, client, new { name = Name(ctx, "desc"), bookingDuration = 15, description = new string('d', 501) })).ExpectStatus(400);
            (await Create(ctx, client, new { name = Name(ctx, "code"), bookingDuration = 15, code = new string('c', 101) })).ExpectStatus(400);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("duplicates", "Services: a duplicate name (also by case or surrounding spaces) is refused, also on rename", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var name = Name(ctx, "Dup");
            (await Create(ctx, client, new { name, bookingDuration = 30 })).ExpectStatus(201);
            (await Create(ctx, client, new { name, bookingDuration = 30 })).ExpectStatus(400);
            (await Create(ctx, client, new { name = name.ToUpperInvariant(), bookingDuration = 30 })).ExpectStatus(400);
            (await Create(ctx, client, new { name = $"  {name}  ", bookingDuration = 30 })).ExpectStatus(400);
            var other = (await Create(ctx, client, new { name = Name(ctx, "other"), bookingDuration = 30 })).ExpectStatus(201).Id();
            (await client.Http.PutAsync($"/api/services/{other}", new { name, bookingDuration = 30 }, ctx.Ct)).ExpectStatus(400);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("text-safety", "Services: Arabic/emoji names work and HTML names are shown escaped on the Services page", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            (await Create(ctx, client, new { name = $"استشارة 💆 {ctx.Tag}", bookingDuration = 45, description = "وصف" })).ExpectStatus(201);
            var html = $"<b>x{ctx.Tag}</b><script>alert(1)</script>";
            (await Create(ctx, client, new { name = html, bookingDuration = 15 })).ExpectStatus(201);
            var page = (await client.Http.GetAsync("/Services", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains(html), "The Services page renders a service name as raw HTML.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("isolation", "Services: another client's service is 404 for get, update and (de)activate", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var other = await ctx.OtherClientAsync();
            var id = other.BaseServiceId;
            (await client.Http.GetAsync($"/api/services/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await client.Http.PutAsync($"/api/services/{id}", new { name = "Automation hijack", bookingDuration = 10 }, ctx.Ct)).ExpectStatus(404);
            (await client.Http.PostAsync($"/api/services/{id}/active", new { isActive = false }, ct: ctx.Ct)).ExpectStatus(404);
            var still = (await other.Http.GetAsync($"/api/services/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("true", still.Prop("isActive"), "The other client's service after the attempts");
            (await client.Http.PutAsync($"/api/services/{Guid.NewGuid()}", new { name = "x", bookingDuration = 10 }, ctx.Ct)).ExpectStatus(404);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("neutral-icon", "Services: a general (non-clinic) business doesn't get the medical icon", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx);
            var page = (await client.Http.GetAsync("/Services", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("medical_services"), "A general business sees the \"medical_services\" icon for Services (sidebar/page).");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-21")
        .Test("signed-out", "Services: signed out is 401", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/services", ct: ctx.Ct)).ExpectStatus(401);
            (await ctx.Anonymous.PostAsync("/api/services", new { name = "x", bookingDuration = 10 }, ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Build();
}

[Suite(31)]
public static class OpsAvailabilitySuite
{
    private const string Label = "ops-avail";

    public static TestSuite Build() => new SuiteBuilder("ops-availability", "Operations: availability and slots",
            "Opening hours, timezone/DST, notice, buffer, horizon, closed days and date exceptions, as GET /api/bookings/available sees them.")
        .Test("timezone", "Slots: in America/New_York every slot carries New York's UTC offset (DST-aware) and local hours 08-20", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "America/New_York", "08:00", "20:00", durationMinutes: 60);
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var slots = await OpsHelpers.SlotsAsync(ctx, client, "days=7");
            Expect.True(slots.Count > 0, "No slots in New York time.");
            foreach (var slot in slots)
            {
                var start = OpsHelpers.Start(slot);
                Expect.Equal(ny.GetUtcOffset(start), start.Offset, $"UTC offset of the slot at {start:o}");
                Expect.True(start.Hour is >= 8 and < 20, $"Slot {start:o} is outside 08:00-20:00 local.");
            }
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("notice", "Slots: with 48 hours' notice nothing earlier than 48 hours from now is offered", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC", noticeHours: 48);
            var slots = await OpsHelpers.SlotsAsync(ctx, client, "days=7");
            Expect.True(slots.Count > 0, "No slots with 48 hours' notice.");
            var earliest = slots.Min(OpsHelpers.Start);
            Expect.True(earliest >= DateTimeOffset.UtcNow.AddHours(48).AddMinutes(-1), $"A slot at {earliest:o} is inside the 48-hour notice.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("buffer", "Slots: 30-minute slots with a 15-minute buffer start 45 minutes apart", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC", durationMinutes: 30, bufferMinutes: 15);
            var date = DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy-MM-dd");
            var starts = (await OpsHelpers.SlotsAsync(ctx, client, $"date={date}")).Select(OpsHelpers.Start).OrderBy(s => s).ToList();
            Expect.True(starts.Count >= 3, $"Only {starts.Count} slots on {date}.");
            for (var i = 1; i < starts.Count; i++)
            {
                Expect.Equal(45.0, (starts[i] - starts[i - 1]).TotalMinutes, $"Minutes between slots {starts[i - 1]:HH:mm} and {starts[i]:HH:mm}");
            }
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("horizon", "Slots: with a 3-day horizon nothing later is offered, and a later date says so", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC", maxAdvanceDays: 3);
            var limit = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(4);
            var slots = await OpsHelpers.SlotsAsync(ctx, client, "days=10");
            Expect.True(slots.Count > 0, "No slots inside the horizon.");
            Expect.True(slots.All(s => OpsHelpers.Start(s).UtcDateTime < limit), "A slot beyond the 3-day horizon is offered.");
            var later = (await client.Http.GetAsync($"/api/bookings/available?date={DateTimeOffset.UtcNow.AddDays(10):yyyy-MM-dd}", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(0, later.Array("slots").Count, "Slots 10 days ahead with a 3-day horizon");
            Expect.True(!string.IsNullOrWhiteSpace(later.Prop("message").ToString()), "No message explains the horizon.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("closed-days", "Slots: closed weekdays get no slots", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            var open = new[] { false, true, true, true, true, true, false };
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC", openDays: open);
            var slots = await OpsHelpers.SlotsAsync(ctx, client, "days=7");
            Expect.True(slots.Count > 0, "No slots on weekdays.");
            var weekend = slots.Where(s => OpsHelpers.Start(s).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday).ToList();
            Expect.True(weekend.Count == 0, $"{weekend.Count} slot(s) offered on a closed Saturday/Sunday.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("not-configured", "Slots: with every day closed the client counts as not configured and gets a message", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC", openDays: new bool[7]);
            var r = (await client.Http.GetAsync($"/api/bookings/available?serviceId={client.BaseServiceId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("false", r.Prop("configured"), "configured");
            Expect.Equal(0, r.Array("slots").Count, "Slots when nothing is open");
            Expect.True(!string.IsNullOrWhiteSpace(r.Prop("message").ToString()), "No message says availability isn't set up.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("exception", "Slots: a closed-date exception removes that day's slots", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC");
            var date = DateTimeOffset.UtcNow.AddDays(5).ToString("yyyy-MM-dd");
            Expect.True((await OpsHelpers.SlotsAsync(ctx, client, $"date={date}")).Count > 0, $"No slots on {date} before the exception.");
            var saved = await client.Http.SubmitFormAsync("/settings/business-info", new Dictionary<string, string>
            {
                ["ExceptionDate"] = date, ["ExceptionClosed"] = "true", ["ExceptionReason"] = "Automation holiday",
            }, handler: "AddException", ct: ctx.Ct);
            Expect.True(saved.Status == 302, $"Saving a date exception answered {saved.Status}: {TestContext.PageError(saved.Body) ?? "no error shown"}.");
            Expect.Equal(0, (await OpsHelpers.SlotsAsync(ctx, client, $"date={date}")).Count, $"Slots on the closed date {date}");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("past-and-ranges", "Slots: a past date gives no slots with a message; odd day counts and unknown services don't crash", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC");
            var past = (await client.Http.GetAsync("/api/bookings/available?date=2020-01-01", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(0, past.Array("slots").Count, "Slots on a past date");
            Expect.True(!string.IsNullOrWhiteSpace(past.Prop("message").ToString()), "No message for a past date.");
            (await client.Http.GetAsync("/api/bookings/available?days=1000", ct: ctx.Ct)).ExpectOk();
            (await client.Http.GetAsync("/api/bookings/available?days=-3", ct: ctx.Ct)).ExpectOk();
            var unknown = await client.Http.GetAsync($"/api/bookings/available?serviceId={Guid.NewGuid()}", ct: ctx.Ct);
            Expect.True(unknown.Status is 400 or 404, $"An unknown service answered {unknown.Status}.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("taken-slot", "Slots: a booked slot is no longer offered", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            await ctx.Fixtures.SaveAvailabilityAsync(client, "UTC");
            var date = DateTimeOffset.UtcNow.AddDays(3).ToString("yyyy-MM-dd");
            var slot = OpsHelpers.Start((await OpsHelpers.SlotsAsync(ctx, client, $"date={date}"))[2]);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "taken");
            await OpsHelpers.BookOkAsync(ctx, client, contact, slot);
            var after = (await OpsHelpers.SlotsAsync(ctx, client, $"date={date}")).Select(OpsHelpers.Start).ToList();
            Expect.True(!after.Contains(slot), $"The booked slot {slot:o} is still offered.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("form-validation", "Opening hours: end before start, an unknown timezone and a 999-minute buffer are refused", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            foreach (var (what, tz, start, end, buffer) in new[]
                     { ("end before start", "UTC", "17:00", "09:00", 0), ("unknown timezone", "Mars/Olympus_Mons", "09:00", "17:00", 0), ("buffer 999", "UTC", "09:00", "17:00", 999) })
            {
                var r = await OpsHelpers.SubmitAvailabilityAsync(ctx, client, tz, start, end, buffer);
                Expect.True(r.Status == 200 && TestContext.PageError(r.Body) is not null,
                    $"Saving opening hours with {what} answered {r.Status} without an error message (should be refused).");
            }
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Build();
}

