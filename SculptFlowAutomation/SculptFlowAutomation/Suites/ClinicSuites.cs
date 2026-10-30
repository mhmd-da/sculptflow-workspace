using SculptFlowAutomation.Core;

namespace SculptFlowAutomation.Suites;

public static class StaffSuite
{
    public static TestSuite Build() => new SuiteBuilder("staff", "Staff", "The clinic's team list.")
        .Test("list", "Staff list includes the signed-up user", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var staff = (await clinic.Client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(staff.Any(s => string.Equals(s?["email"]?.ToString(), clinic.Email, StringComparison.OrdinalIgnoreCase)),
                $"{clinic.Email} is not in the staff list.");
        })
        .Build();
}

public static class ProceduresSuite
{
    public static TestSuite Build() => new SuiteBuilder("procedures", "Procedures", "Create, edit, deactivate and reactivate a procedure.")
        .Test("crud", "Create, read and update a procedure", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var created = (await api.PostAsync("/api/procedures", new
            {
                name = $"Rhinoplasty {ctx.Tag}", code = $"rhino-{ctx.Tag}", description = "Automation", consultationDuration = 45,
            }, ct: ctx.Ct)).ExpectStatus(201);
            var id = created.Id();

            var fetched = (await api.GetAsync($"/api/procedures/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText($"Rhinoplasty {ctx.Tag}", fetched.Prop("name"), "Name");

            var updated = (await api.PutAsync($"/api/procedures/{id}", new
            {
                name = $"Rhinoplasty v2 {ctx.Tag}", code = $"rhino-{ctx.Tag}", description = "Updated", consultationDuration = 60,
            }, ctx.Ct)).ExpectOk();
            Expect.EqualText($"Rhinoplasty v2 {ctx.Tag}", updated.Prop("name"), "Updated name");
            Expect.EqualText("60", updated.Prop("consultationDuration"), "Updated duration");

            var list = (await api.GetAsync("/api/procedures", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId(list, id, "Active procedures");
        })
        .Test("active-toggle", "Deactivated procedures drop out of the active list", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var id = (await api.PostAsync("/api/procedures", new { name = $"Toggle {ctx.Tag}", consultationDuration = 30 }, ct: ctx.Ct))
                .ExpectStatus(201).Id();

            (await api.PostAsync($"/api/procedures/{id}/active", new { isActive = false }, ct: ctx.Ct)).ExpectOk();
            Expect.LacksId((await api.GetAsync("/api/procedures", ct: ctx.Ct)).ExpectOk().Array(), id, "Active procedures");
            Expect.ContainsId((await api.GetAsync("/api/procedures?activeOnly=false", ct: ctx.Ct)).ExpectOk().Array(), id, "All procedures");

            (await api.PostAsync($"/api/procedures/{id}/active", new { isActive = true }, ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId((await api.GetAsync("/api/procedures", ct: ctx.Ct)).ExpectOk().Array(), id, "Active procedures after reactivating");
        })
        .Test("validation", "A procedure without a name is rejected", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.PostAsync("/api/procedures", new { name = "", consultationDuration = 30 }, ct: ctx.Ct)).ExpectStatus(400);
        })
        .Build();
}

public static class LeadsSuite
{
    public static TestSuite Build() => new SuiteBuilder("leads", "Leads", "Lead intake (the n8n / lead-form path) and staff lead management.")
        .Test("intake", "Intake creates a lead the clinic can see", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("intake");
            var api = await ctx.ApiAsync();
            var lead = (await api.GetAsync($"/api/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("new", lead.Prop("status"), "New lead status");
        })
        .Test("intake-idempotent", "Re-sending the same external lead id doesn't duplicate it", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var externalId = $"auto-{ctx.Tag}-dupe";
            var body = new { clinicId = clinic.ClinicId, fullName = $"Dupe {ctx.Tag}", phone = ctx.Fixtures.NewPhone(), source = "automation", externalLeadId = externalId };
            var first = (await ctx.Ingest.PostAsync("/api/leads", body, ct: ctx.Ct)).ExpectStatus(201);
            var second = (await ctx.Ingest.PostAsync("/api/leads", body, ct: ctx.Ct)).ExpectStatus(200);
            Expect.Equal(first.Id(), second.Id(), "Lead id on retry");
        }, Needs.Session)
        .Test("list-search", "Lead list and search find the lead", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            var api = await ctx.ApiAsync();
            Expect.ContainsId((await api.GetAsync("/api/leads?take=200", ct: ctx.Ct)).ExpectOk().Array("items"), leadId, "Lead list");
            Expect.ContainsId((await api.GetAsync($"/api/leads?search={Uri.EscapeDataString("shared " + ctx.Tag)}", ct: ctx.Ct)).ExpectOk().Array("items"), leadId, "Search results");
        })
        .Test("update", "Staff can edit a lead", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("edit");
            var api = await ctx.ApiAsync();
            var updated = (await api.PatchAsync($"/api/leads/{leadId}", new { city = "Beirut", notes = $"Edited by automation {ctx.Tag}", desiredTimeline = "1-3 months" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("Beirut", updated.Prop("city"), "City");
        })
        .Test("status", "Lead status moves through the pipeline", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("status");
            var api = await ctx.ApiAsync();
            foreach (var status in new[] { "contacted", "qualified" })
            {
                var lead = (await api.PostAsync($"/api/leads/{leadId}/status", new { status, qualificationStatus = "warm" }, ct: ctx.Ct)).ExpectOk();
                Expect.EqualText(status, lead.Prop("status"), "Lead status");
            }
            Expect.ContainsId((await api.GetAsync("/api/leads?status=qualified&take=200", ct: ctx.Ct)).ExpectOk().Array("items"), leadId, "Leads filtered by status");
        })
        .Test("status-invalid", "An unknown status is rejected", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            var api = await ctx.ApiAsync();
            (await api.PostAsync($"/api/leads/{leadId}/status", new { status = "not-a-status" }, ct: ctx.Ct)).ExpectStatus(400);
        })
        .Test("unknown", "A lead id that doesn't exist returns 404", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.GetAsync($"/api/leads/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        })
        .Build();
}

public static class AvailabilitySuite
{
    public static TestSuite Build() => new SuiteBuilder("availability", "Opening hours", "Saving opening hours and the slots they produce.")
        .Test("save", "Save weekly opening hours", async ctx =>
        {
            await ctx.Fixtures.EnsureAvailabilityAsync();
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/settings/clinic-info?tab=availability", ct: ctx.Ct)).ExpectOk().ExpectBodyContains("06:00");
        })
        .Test("slots", "Open slots are offered", async ctx =>
        {
            var slots = await ctx.Fixtures.SlotsAsync();
            var first = slots[0]!;
            Expect.True(first["start"] is not null && first["end"] is not null && first["label"] is not null, "A slot is missing start/end/label.");
        })
        .Test("slots-one-day", "Slots for a single date stay on that date", async ctx =>
        {
            await ctx.Fixtures.EnsureAvailabilityAsync();
            var api = await ctx.ApiAsync();
            var date = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd");
            var response = (await api.GetAsync($"/api/appointments/available?date={date}", ct: ctx.Ct)).ExpectOk();
            var slots = response.Array("slots");
            Expect.True(slots.Count > 0, $"No slots on {date}.");
            Expect.True(slots.All(s => s?["date"]?.ToString() == date), $"Some slots are not on {date}.");
        })
        .Build();
}

public static class AppointmentsSuite
{
    public static TestSuite Build() => new SuiteBuilder("appointments", "Appointments", "Staff booking, status changes and the calendar.")
        .Test("create", "Book an appointment for a lead", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("appt");
            var clinic = await ctx.ClinicAsync();
            var start = await ctx.Fixtures.SlotStartAsync(2);
            var created = (await clinic.Client.PostAsync("/api/appointments", new
            {
                leadId, procedureId = clinic.BaseProcedureId, appointmentType = "consultation",
                scheduledStart = start, locationType = "in_person", notes = $"Automation {ctx.Tag}",
            }, ct: ctx.Ct)).ExpectStatus(201);
            var id = created.Id();

            var fetched = (await clinic.Client.GetAsync($"/api/appointments/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText(leadId.ToString(), fetched.Prop("leadId"), "Appointment lead");

            var month = DateTimeOffset.Parse(start).UtcDateTime;
            var calendar = (await clinic.Client.GetAsync($"/api/appointments/calendar?year={month.Year}&month={month.Month}", ct: ctx.Ct)).ExpectOk();
            Expect.True(calendar.Body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase), "The appointment is not on the month calendar.");
            Expect.ContainsId((await clinic.Client.GetAsync("/api/appointments?take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "Appointment list");
        })
        .Test("status", "Confirm, then cancel an appointment", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("appt-status");
            var api = await ctx.ApiAsync();
            var start = await ctx.Fixtures.SlotStartAsync(4);
            var id = (await api.PostAsync("/api/appointments", new { leadId, appointmentType = "consultation", scheduledStart = start }, ct: ctx.Ct))
                .ExpectStatus(201).Id();

            Expect.EqualText("confirmed", (await api.PatchAsync($"/api/appointments/{id}/status", new { status = "confirmed" }, ctx.Ct)).ExpectOk().Prop("status"), "Status");
            Expect.EqualText("canceled", (await api.PatchAsync($"/api/appointments/{id}/status", new { status = "canceled" }, ctx.Ct)).ExpectOk().Prop("status"), "Status");
            (await api.PatchAsync($"/api/appointments/{id}/status", new { status = "bogus" }, ctx.Ct)).ExpectStatus(400);
        })
        .Test("calendar-validation", "Calendar rejects an invalid month", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/api/appointments/calendar?year=2026&month=13", ct: ctx.Ct)).ExpectStatus(400);
        })
        .Test("detail-page", "Appointment detail page renders", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("appt-page");
            var api = await ctx.ApiAsync();
            var start = await ctx.Fixtures.SlotStartAsync(6);
            var id = (await api.PostAsync("/api/appointments", new { leadId, appointmentType = "consultation", scheduledStart = start }, ct: ctx.Ct))
                .ExpectStatus(201).Id();
            (await api.GetAsync($"/dashboard/appointments/{id}", ct: ctx.Ct)).ExpectOk();
        })
        .Build();
}

public static class ProcedureBookingsSuite
{
    public static TestSuite Build() => new SuiteBuilder("bookings", "Procedure bookings", "Quotes, deposits and booked surgeries.")
        .Test("lifecycle", "Quote, take a deposit and book a procedure", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("booking");
            var clinic = await ctx.ClinicAsync();
            var created = (await clinic.Client.PostAsync("/api/procedure-bookings", new
            {
                leadId, procedureId = clinic.BaseProcedureId, quotedAmount = 4500m, currencyCode = "USD", notes = $"Automation {ctx.Tag}",
            }, ct: ctx.Ct)).ExpectStatus(201);
            var id = created.Id();

            var quoted = (await clinic.Client.PatchAsync($"/api/procedure-bookings/{id}", new { status = "quoted", quotedAmount = 4200m }, ctx.Ct)).ExpectOk();
            Expect.EqualText("quoted", quoted.Prop("status"), "Status");
            var deposit = (await clinic.Client.PatchAsync($"/api/procedure-bookings/{id}", new { status = "deposit_paid", depositAmount = 500m }, ctx.Ct)).ExpectOk();
            Expect.EqualText("deposit_paid", deposit.Prop("status"), "Status");
            var booked = (await clinic.Client.PatchAsync($"/api/procedure-bookings/{id}", new
            {
                status = "booked", procedureDate = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
            }, ctx.Ct)).ExpectOk();
            Expect.EqualText("booked", booked.Prop("status"), "Status");

            Expect.ContainsId((await clinic.Client.GetAsync("/api/procedure-bookings?status=booked&take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "Booked list");
        })
        .Test("invalid-status", "An unknown booking status is rejected", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            var clinic = await ctx.ClinicAsync();
            var id = (await clinic.Client.PostAsync("/api/procedure-bookings", new { leadId, procedureId = clinic.BaseProcedureId }, ct: ctx.Ct))
                .ExpectStatus(201).Id();
            (await clinic.Client.PatchAsync($"/api/procedure-bookings/{id}", new { status = "bogus" }, ctx.Ct)).ExpectStatus(400);
        })
        .Build();
}

public static class InboxSuite
{
    public static TestSuite Build() => new SuiteBuilder("inbox", "Inbox", "Conversations, messages and AI/human hand-over (website channel, so nothing is delivered).")
        .Test("conversation", "Open a conversation and log messages", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("inbox");
            var api = await ctx.ApiAsync();
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);

            (await api.PostAsync($"/api/conversations/{conversationId}/messages", new
            {
                direction = "inbound", senderType = "lead", channel = "website", messageType = "text",
                content = $"Hi, I'd like a consultation ({ctx.Tag})", externalMessageId = $"auto-{Guid.NewGuid():N}",
            }, ct: ctx.Ct)).ExpectStatus(200, 201);

            var messages = (await api.GetAsync($"/api/conversations/{conversationId}/messages", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(messages.Any(m => m?["content"]?.ToString()?.Contains(ctx.Tag) == true), "The logged message is not in the conversation.");

            var list = (await api.GetAsync("/api/conversations?take=200", ct: ctx.Ct)).ExpectOk();
            Expect.True(list.Body.Contains(conversationId.ToString(), StringComparison.OrdinalIgnoreCase), "The conversation is not in the inbox list.");
            (await api.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk();
            (await api.PostAsync($"/api/conversations/{conversationId}/read", ct: ctx.Ct)).ExpectStatus(200, 204);
        })
        .Test("modes", "Take over from the AI, hand back, then close", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("modes");
            var api = await ctx.ApiAsync();
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);

            Expect.EqualText("human", (await api.PostAsync($"/api/conversations/{conversationId}/take-over", ct: ctx.Ct)).ExpectOk().Prop("mode"), "Mode after take-over");
            Expect.EqualText("ai", (await api.PostAsync($"/api/conversations/{conversationId}/return-to-ai", ct: ctx.Ct)).ExpectOk().Prop("mode"), "Mode after hand-back");
            Expect.EqualText("closed", (await api.PostAsync($"/api/conversations/{conversationId}/close", ct: ctx.Ct)).ExpectOk().Prop("status"), "Status after close");
        })
        .Test("ingest", "n8n ingest stores an inbound customer message", async ctx =>
        {
            var leadId = await ctx.Fixtures.NewLeadAsync("ingest");
            var clinic = await ctx.ClinicAsync();
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);
            var result = (await ctx.Ingest.PostAsync("/api/messages/ingest", new
            {
                clinicId = clinic.ClinicId, conversationId, leadId, eventType = "customer_message", channel = "website",
                content = $"Ingested {ctx.Tag}", externalMessageId = $"auto-{Guid.NewGuid():N}", receivedAt = DateTimeOffset.UtcNow,
            }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("true", result.Prop("found"), "Ingest found the conversation");

            var messages = (await clinic.Client.GetAsync($"/api/conversations/{conversationId}/messages", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(messages.Any(m => m?["content"]?.ToString() == $"Ingested {ctx.Tag}"), "The ingested message is not in the conversation.");
        }, Needs.Session | Needs.IngestKey)
        .Test("ingest-unknown", "Ingest for an unknown conversation returns 404", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await ctx.Ingest.PostAsync("/api/messages/ingest", new
            {
                clinicId = clinic.ClinicId, conversationId = Guid.NewGuid(), leadId = Guid.NewGuid(), eventType = "customer_message",
                channel = "website", content = "nobody", externalMessageId = $"auto-{Guid.NewGuid():N}",
            }, ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.IngestKey)
        .Build();
}

public static class NotificationsSuite
{
    public static TestSuite Build() => new SuiteBuilder("notifications", "Notifications", "The bell: list, unread count and mark-all-read.")
        .Test("list", "List notifications and the unread count", async ctx =>
        {
            await ctx.Fixtures.LeadAsync(); // a new lead raises a NEW_LEAD notification on most setups
            var api = await ctx.ApiAsync();
            var list = (await api.GetAsync("/api/notifications?take=20", ct: ctx.Ct)).ExpectOk();
            list.Array("items");
            (await api.GetAsync("/api/notifications/unread-count", ct: ctx.Ct)).ExpectOk();
        })
        .Test("read-all", "Mark all as read clears the unread count", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.PostAsync("/api/notifications/read-all", ct: ctx.Ct)).ExpectStatus(200, 204);
            var list = (await api.GetAsync("/api/notifications?take=20", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("0", list.Prop("unreadCount"), "Unread count after mark-all-read");
        })
        .Build();
}

public static class DashboardSuite
{
    public static TestSuite Build() => new SuiteBuilder("dashboard", "Dashboard", "Summary numbers, attention list and the dashboard tables.")
        .Test("summary", "Summary and attention load", async ctx =>
        {
            await ctx.Fixtures.LeadAsync();
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/api/dashboard/summary", ct: ctx.Ct)).ExpectOk().RequireJson();
            (await api.GetAsync("/api/dashboard/attention", ct: ctx.Ct)).ExpectOk().RequireJson();
        })
        .Test("tables", "Leads, appointments and procedure tables load", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            var api = await ctx.ApiAsync();
            Expect.ContainsId((await api.GetAsync("/api/dashboard/leads?take=200", ct: ctx.Ct)).ExpectOk().Array("items"), leadId, "Dashboard leads");
            (await api.GetAsync("/api/dashboard/appointments", ct: ctx.Ct)).ExpectOk().Array("items");
            (await api.GetAsync("/api/dashboard/procedures", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Build();
}
