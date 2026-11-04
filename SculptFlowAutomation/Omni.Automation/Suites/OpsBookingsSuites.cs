using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(32)]
public static class OpsBookingsSuite
{
    public static TestSuite Build() => new SuiteBuilder("ops-bookings", "Operations: bookings",
            "Dashboard bookings (POST /api/bookings): create, validation, scheduling rules, status changes, calendar and list.")
        .Test("create", "Bookings: create, read, list and see it on the month calendar; the contact moves to a booked stage", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "book");
            var stageBefore = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            var start = OpsHelpers.At(2, 10);
            var created = (await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddMinutes(30))).ExpectStatus(201);
            var id = created.Id();
            Expect.EqualText("booked", created.Prop("status"), "Status");
            Expect.Equal(start, DateTimeOffset.Parse(created.Str("scheduledStart"), CultureInfo.InvariantCulture), "Start");
            (await client.Http.GetAsync($"/api/bookings/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId((await client.Http.GetAsync("/api/bookings?take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "Bookings list");
            var month = (await client.Http.GetAsync($"/api/bookings/calendar?year={start.Year}&month={start.Month}", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId(month.Array("items"), id, "Month calendar");
            var stageAfter = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            Expect.True(stageAfter != stageBefore, $"The contact stayed in stage {stageBefore} after booking.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("refs", "Bookings: an unknown contact, another client's contact or service, and an inactive service are 400", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var other = await ctx.OtherClientAsync();
            var foreignContact = await OpsHelpers.ContactAsync(ctx, other, "foreign");
            var contact = await OpsHelpers.ContactAsync(ctx, client, "refs");
            var start = OpsHelpers.At(3, 7);
            (await OpsHelpers.BookAsync(ctx, client, Guid.NewGuid(), start, start.AddMinutes(30))).ExpectStatus(400);
            (await OpsHelpers.BookAsync(ctx, client, foreignContact, start, start.AddMinutes(30))).ExpectStatus(400);
            (await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddMinutes(30), serviceId: other.BaseServiceId)).ExpectStatus(400);
            var service = (await client.Http.PostAsync("/api/services", new { name = $"Auto inactive {ctx.Tag} {Guid.NewGuid():N}"[..36], bookingDuration = 30 }, ct: ctx.Ct))
                .ExpectStatus(201).Id();
            var existing = (await OpsHelpers.BookAsync(ctx, client, contact, OpsHelpers.At(3, 8), OpsHelpers.At(3, 8, 30), serviceId: service)).ExpectStatus(201).Id();
            (await client.Http.PostAsync($"/api/services/{service}/active", new { isActive = false }, ct: ctx.Ct)).ExpectOk();
            (await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddMinutes(30), serviceId: service)).ExpectStatus(400);
            Expect.EqualText("booked", (await client.Http.GetAsync($"/api/bookings/{existing}", ct: ctx.Ct)).ExpectOk().Prop("status"),
                "An existing booking of a service that was then deactivated");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("no-service", "Bookings: a booking without a service is allowed", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "nosvc");
            var start = OpsHelpers.At(3, 9);
            var r = (await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddMinutes(20), noService: true)).ExpectStatus(201);
            Expect.True(r.RequireJson()["serviceId"] is null, "A booking made without a service got one.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("default-end", "Bookings: without an end time the service's duration sets the end", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "noend");
            var start = OpsHelpers.At(3, 11);
            var r = (await OpsHelpers.BookAsync(ctx, client, contact, start, null)).ExpectStatus(201);
            var end = r.RequireJson()["scheduledEnd"];
            Expect.True(end is not null && DateTimeOffset.Parse(end.ToString(), CultureInfo.InvariantCulture) == start.AddMinutes(30),
                $"A booking without an end for a 30-minute service was stored with end {end?.ToString() ?? "NULL"}; expected {start.AddMinutes(30):o}.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-19")
        .Test("double-booking", "Bookings: the same slot can't be booked twice (409)", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var a = await OpsHelpers.ContactAsync(ctx, client, "dbl-a");
            var b = await OpsHelpers.ContactAsync(ctx, client, "dbl-b");
            var start = OpsHelpers.At(4, 10);
            await OpsHelpers.BookOkAsync(ctx, client, a, start);
            var second = await OpsHelpers.BookAsync(ctx, client, b, start, start.AddMinutes(30));
            Expect.True(second.Status == 409, $"A second booking for the same slot answered {second.Status}; the dashboard path skips the slot check (expected 409).");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-01")
        .Test("overlap", "Bookings: a partially overlapping booking is refused (409)", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var a = await OpsHelpers.ContactAsync(ctx, client, "ovl-a");
            var b = await OpsHelpers.ContactAsync(ctx, client, "ovl-b");
            var start = OpsHelpers.At(4, 12);
            await OpsHelpers.BookOkAsync(ctx, client, a, start);
            var overlap = await OpsHelpers.BookAsync(ctx, client, b, start.AddMinutes(15), start.AddMinutes(45));
            Expect.True(overlap.Status == 409, $"A booking overlapping 15 minutes of another answered {overlap.Status} (expected 409).");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-01")
        .Test("concurrent", "Bookings: 5 concurrent bookings of one slot — exactly one succeeds", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contacts = new List<Guid>();
            for (var i = 0; i < 5; i++) contacts.Add(await OpsHelpers.ContactAsync(ctx, client, $"conc{i}"));
            var start = OpsHelpers.At(4, 14);
            var results = await Task.WhenAll(contacts.Select(c => OpsHelpers.BookAsync(ctx, client, c, start, start.AddMinutes(30))));
            var created = results.Count(r => r.Status == 201);
            Expect.True(created == 1, $"{created} of 5 concurrent bookings of one slot were created ({string.Join(",", results.Select(r => r.Status))}); expected exactly 1.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing | Risk.Shared, finding: "t5-ops-01", description: "A small burst of 5 requests.")
        .Test("past", "Bookings: a booking in the past is refused", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "past");
            var start = OpsHelpers.At(-30, 10);
            var r = await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddMinutes(30));
            Expect.True(r.Status is 400 or 409 or 422, $"A booking 30 days in the past answered {r.Status} (expected a refusal).");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-01")
        .Test("end-before-start", "Bookings: an end before (or equal to) the start is 400", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "endb");
            var start = OpsHelpers.At(5, 10);
            var before = await OpsHelpers.BookAsync(ctx, client, contact, start, start.AddHours(-1));
            var zero = await OpsHelpers.BookAsync(ctx, client, contact, start.AddHours(2), start.AddHours(2));
            Expect.True(before.Status == 400 && zero.Status == 400,
                $"End before start answered {before.Status}, zero length answered {zero.Status}; expected 400 for both.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-01")
        .Test("absurd-date", "Bookings: a booking in the year 9999 is refused", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "y9999");
            var start = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
            var r = await OpsHelpers.BookAsync(ctx, client, contact, start, null);
            Expect.True(r.Status is 400 or 422, $"A booking on 9999-12-31 (far beyond the 60-day horizon) answered {r.Status}.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-01")
        .Test("long-fields", "Bookings: over-long booking type and location are 400 JSON, not a 500 page", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "long");
            var type = await OpsHelpers.BookAsync(ctx, client, contact, OpsHelpers.At(5, 12), null, bookingType: new string('x', 60));
            var location = await OpsHelpers.BookAsync(ctx, client, contact, OpsHelpers.At(5, 13), null, locationType: new string('y', 40), locationName: new string('z', 250));
            Expect.True(type.Status == 400 && location.Status == 400,
                $"A 60-character booking type answered {type.Status}, a 40/250-character location answered {location.Status}; expected 400 with a JSON error.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-02")
        .Test("notes-escaped", "Bookings: HTML in notes is stored as text and shown escaped on the booking page", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "notes");
            const string notes = "<img src=x onerror=alert(1)> ملاحظة 😀";
            var id = (await OpsHelpers.BookAsync(ctx, client, contact, OpsHelpers.At(5, 15), null, notes: notes)).ExpectStatus(201).Id();
            Expect.EqualText(notes, (await client.Http.GetAsync($"/api/bookings/{id}", ct: ctx.Ct)).ExpectOk().Prop("notes"), "Stored notes");
            var page = (await client.Http.GetAsync($"/dashboard/bookings/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("<img src=x onerror"), "The booking page renders the notes as raw HTML.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("status", "Bookings: confirm and cancel work; an unknown status is 400; an unknown booking is 404", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "status");
            var id = await OpsHelpers.BookOkAsync(ctx, client, contact, OpsHelpers.At(6, 10));
            Expect.EqualText("confirmed", (await OpsHelpers.SetStatusAsync(ctx, client, id, "confirmed")).ExpectOk().Prop("status"), "Status after confirm");
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "foo")).ExpectStatus(400);
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "")).ExpectStatus(400);
            Expect.EqualText("canceled", (await OpsHelpers.SetStatusAsync(ctx, client, id, "canceled")).ExpectOk().Prop("status"), "Status after cancel");
            (await OpsHelpers.SetStatusAsync(ctx, client, Guid.NewGuid(), "confirmed")).ExpectStatus(404);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("outside-hours", "Bookings: a time outside opening hours asks first (422 booking_rules) and is booked with overrideRules; an overlap never is", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "afterhours");
            var start = OpsHelpers.At(8, 23);   // the automation client opens 06:00-22:00 UTC
            var body = new { contactId = contact, serviceId = client.BaseServiceId, bookingType = "standard", scheduledStart = start, scheduledEnd = start.AddMinutes(30), overrideRules = false };
            var asked = await client.Http.PostAsync("/api/bookings", body, ct: ctx.Ct);
            Expect.Equal(422, asked.Status, "A booking outside opening hours without the override");
            Expect.EqualText("booking_rules", asked.Prop("code"), "Its code");
            var booked = (await client.Http.PostAsync("/api/bookings", body with { overrideRules = true }, ct: ctx.Ct)).ExpectStatus(201);
            var again = await client.Http.PostAsync("/api/bookings", body with { overrideRules = true }, ct: ctx.Ct);
            Expect.Equal(409, again.Status, "The same time again, even with the override");
            (await OpsHelpers.SetStatusAsync(ctx, client, booked.Id(), "canceled")).ExpectOk();
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-01")
        .Test("edit", "Bookings: a member moves a booking and changes its place and notes (PATCH /api/bookings/{id}); the move is logged and notified", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "edit");
            var id = await OpsHelpers.BookOkAsync(ctx, client, contact, OpsHelpers.At(7, 10));
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "confirmed")).ExpectOk();
            var start = OpsHelpers.At(7, 15);
            var edited = (await client.Http.PatchAsync($"/api/bookings/{id}",
                new { scheduledStart = start, scheduledEnd = start.AddMinutes(45), serviceId = client.BaseServiceId, locationType = "phone", notes = "Moved by phone" }, ctx.Ct)).ExpectOk();
            Expect.Equal(start, DateTimeOffset.Parse(edited.Str("scheduledStart"), CultureInfo.InvariantCulture), "Start after the move");
            Expect.Equal(start.AddMinutes(45), DateTimeOffset.Parse(edited.Str("scheduledEnd"), CultureInfo.InvariantCulture), "End after the move");
            Expect.EqualText("phone", edited.Prop("locationType"), "Place");
            Expect.EqualText("Moved by phone", edited.Prop("notes"), "Notes");
            Expect.EqualText("confirmed", edited.Prop("status"), "Status (a member's move keeps it)");
            var month = (await client.Http.GetAsync($"/api/bookings/calendar?year={start.Year}&month={start.Month}", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId(month.Array("items"), id, "Month calendar after the move");
            var moved = await ctx.Db.ScalarAsync<long>("select count(*) from activity.events where booking_id = @b and event_type = 'booking_rescheduled'", new { b = id }, ctx.Ct);
            Expect.True(moved == 1, $"Moving booking {id} logged {moved} booking_rescheduled events (expected 1).");
            var notified = await ctx.Db.ScalarAsync<long>("select count(*) from activity.notifications where booking_id = @b and type = 'BOOKING_RESCHEDULED'", new { b = id }, ctx.Ct);
            Expect.True(notified >= 1, $"Moving booking {id} created no BOOKING_RESCHEDULED notification.");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-04")
        .Test("edit-refused", "Bookings: a canceled booking, an end before the start and an unknown place can't be saved (400); another client's booking is 404", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "editno");
            var id = await OpsHelpers.BookOkAsync(ctx, client, contact, OpsHelpers.At(7, 11));
            var start = OpsHelpers.At(7, 12);
            (await client.Http.PatchAsync($"/api/bookings/{id}", new { scheduledStart = start, scheduledEnd = start.AddMinutes(-15) }, ctx.Ct)).ExpectClientError("Saving an end before the start");
            (await client.Http.PatchAsync($"/api/bookings/{id}", new { scheduledStart = start, locationType = "garden" }, ctx.Ct)).ExpectClientError("Saving an unknown place");
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "canceled")).ExpectOk();
            (await client.Http.PatchAsync($"/api/bookings/{id}", new { scheduledStart = start }, ctx.Ct)).ExpectClientError("Moving a canceled booking");
            var other = await OpsHelpers.BookingClientAsync(ctx, "ops-b");
            (await other.Http.PatchAsync($"/api/bookings/{id}", new { scheduledStart = start }, ctx.Ct)).ExpectStatus(404);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-04")
        .Test("walk-in", "Bookings: a walk-in added by hand (POST /api/contacts/manual) can be booked right away", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = (await client.Http.PostAsync("/api/contacts/manual", new { fullName = $"Walk-in {ctx.Tag} {Guid.NewGuid():N}"[..40] }, ct: ctx.Ct)).ExpectStatus(201).Id();
            await OpsHelpers.BookOkAsync(ctx, client, contact, OpsHelpers.At(7, 13));
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-04")
        .Test("isolation", "Bookings: another client's booking is 404 to read or change", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var other = await OpsHelpers.BookingClientAsync(ctx, "ops-b");
            var foreign = await OpsHelpers.BookOkAsync(ctx, other, await OpsHelpers.ContactAsync(ctx, other, "iso"), OpsHelpers.At(6, 11));
            (await client.Http.GetAsync($"/api/bookings/{foreign}", ct: ctx.Ct)).ExpectStatus(404);
            (await OpsHelpers.SetStatusAsync(ctx, client, foreign, "canceled")).ExpectStatus(404);
            Expect.EqualText("booked", (await other.Http.GetAsync($"/api/bookings/{foreign}", ct: ctx.Ct)).ExpectOk().Prop("status"), "The other client's booking");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("future-attended", "Bookings: a future booking can't be marked attended", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var id = await OpsHelpers.BookOkAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "fut"), OpsHelpers.At(6, 12));
            var r = await OpsHelpers.SetStatusAsync(ctx, client, id, "attended");
            Expect.True(r.Status is 400 or 422, $"Marking a booking 6 days ahead as attended answered {r.Status}; outcomes should only be recorded once the time has passed.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-03")
        .Test("canceled-final", "Bookings: a canceled booking can't jump to attended", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var id = await OpsHelpers.BookOkAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "cfin"), OpsHelpers.At(6, 13));
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "canceled")).ExpectOk();
            var r = await OpsHelpers.SetStatusAsync(ctx, client, id, "attended");
            Expect.True(r.Status is 400 or 422, $"Canceled -> attended answered {r.Status}; a canceled booking should be final (or need an explicit restore).");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-03")
        .Test("rescheduled-direct", "Bookings: \"rescheduled\" can't be set without a new time", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var id = await OpsHelpers.BookOkAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "resch"), OpsHelpers.At(6, 14));
            var r = await OpsHelpers.SetStatusAsync(ctx, client, id, "rescheduled");
            Expect.True(r.Status is 400 or 422, $"Setting status \"rescheduled\" directly answered {r.Status}; only the reschedule flow should set it.");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing, finding: "t5-ops-03")
        .Test("cancel-event", "Bookings: canceling through the status logs a booking_canceled event", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var id = await OpsHelpers.BookOkAsync(ctx, client, await OpsHelpers.ContactAsync(ctx, client, "cev"), OpsHelpers.At(6, 15));
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "canceled")).ExpectOk();
            var events = await ctx.Db.ScalarAsync<long>("select count(*) from activity.events where booking_id = @b and event_type = 'booking_canceled'", new { b = id }, ctx.Ct);
            Expect.True(events == 1, $"Canceling booking {id} from the dashboard logged {events} booking_canceled events (expected 1).");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-03")
        .Test("past-attended", "Bookings: a past booking marked attended logs booking_attended and moves the contact", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "att");
            var id = await OpsHelpers.BookOkAsync(ctx, client, contact, OpsHelpers.At(7, 10));
            await ctx.Db.ExecuteForClientAsync(client.ClientId,
                "update scheduling.bookings set scheduled_start = now() - interval '2 hours', scheduled_end = now() - interval '90 minutes' where id = @id and client_id = @clientId",
                new { id }, ctx.Ct);
            var stageBefore = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            (await OpsHelpers.SetStatusAsync(ctx, client, id, "attended")).ExpectOk();
            var events = await ctx.Db.ScalarAsync<long>("select count(*) from activity.events where booking_id = @b and event_type = 'booking_attended'", new { b = id }, ctx.Ct);
            Expect.Equal(1L, events, "booking_attended events");
            var stageAfter = (await client.Http.GetAsync($"/api/contacts/{contact}", ct: ctx.Ct)).ExpectOk().Str("stageKey");
            Expect.True(stageAfter != stageBefore, $"The contact stayed in stage {stageBefore} after attending.");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing)
        .Test("calendar-bounds", "Calendar: month 13 and year 0 are 400; a valid month answers with the client's timezone", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            (await client.Http.GetAsync("/api/bookings/calendar?year=2026&month=13", ct: ctx.Ct)).ExpectStatus(400);
            (await client.Http.GetAsync("/api/bookings/calendar?year=0&month=1", ct: ctx.Ct)).ExpectStatus(400);
            (await client.Http.GetAsync("/api/bookings/calendar?year=3001&month=1", ct: ctx.Ct)).ExpectStatus(400);
            var now = DateTimeOffset.UtcNow;
            var month = (await client.Http.GetAsync($"/api/bookings/calendar?year={now.Year}&month={now.Month}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("UTC", month.Prop("clientTimezone"), "Client timezone");
            (await client.Http.GetAsync($"/api/bookings/calendar?year={now.Year}&month={now.Month}&view=mine", ct: ctx.Ct)).ExpectOk();
            (await client.Http.GetAsync("/api/bookings/calendar?year=1900&month=1", ct: ctx.Ct)).ExpectOk();
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("list-filters", "Bookings list: paging is clamped and the status filter only returns that status", async ctx =>
        {
            var client = await OpsHelpers.BookingClientAsync(ctx);
            var contact = await OpsHelpers.ContactAsync(ctx, client, "list");
            var canceled = await OpsHelpers.BookOkAsync(ctx, client, contact, OpsHelpers.At(7, 12));
            (await OpsHelpers.SetStatusAsync(ctx, client, canceled, "canceled")).ExpectOk();
            var paged = (await client.Http.GetAsync("/api/bookings?take=500&skip=-5", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("200", paged.Prop("take"), "take after clamping 500");
            Expect.EqualText("0", paged.Prop("skip"), "skip after clamping -5");
            var onlyCanceled = (await client.Http.GetAsync("/api/bookings?status=canceled&take=200", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.ContainsId(onlyCanceled, canceled, "Canceled bookings");
            Expect.True(onlyCanceled.All(b => b?["status"]?.ToString() == "canceled"), "The status filter returned other statuses.");
            var from = Uri.EscapeDataString(OpsHelpers.At(7, 0).ToString("o"));
            var to = Uri.EscapeDataString(OpsHelpers.At(8, 0).ToString("o"));
            var day = (await client.Http.GetAsync($"/api/bookings?from={from}&to={to}&take=200", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.ContainsId(day, canceled, "Bookings of that day");
            Expect.Equal(0, (await client.Http.GetAsync("/api/bookings?status=foo", ct: ctx.Ct)).ExpectOk().Prop("totalCount").GetValue<int>(), "Bookings with status foo");
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("signed-out", "Bookings: signed out is 401", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/bookings", ct: ctx.Ct)).ExpectStatus(401);
            (await ctx.Anonymous.PostAsync("/api/bookings", new { contactId = Guid.NewGuid() }, ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Build();
}

