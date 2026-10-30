using SculptFlowAutomation.Core;

namespace SculptFlowAutomation.Suites;

/// <summary>The endpoints the n8n AI agent calls (ingest key), exercised the way the agent uses them.</summary>
public static class AiAgentSuite
{
    public static TestSuite Build() => new SuiteBuilder("ai", "AI agent tools", "What the n8n AI agent calls: clinic info, procedures, lead context, slots, booking, rescheduling, cancelling and hand-off.")
        .Test("clinic-info", "Clinic info for the agent", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var info = (await ctx.Ingest.GetAsync($"/api/ai/clinic-info?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText(clinic.Name, info.Prop("name"), "Clinic name");
            (await ctx.Ingest.GetAsync($"/api/ai/clinic-info?clinicId={Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.IngestKey)
        .Test("procedures", "Agent only sees active procedures", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var inactive = (await clinic.Client.PostAsync("/api/procedures", new { name = $"Hidden {ctx.Tag}", consultationDuration = 30 }, ct: ctx.Ct)).ExpectStatus(201).Id();
            (await clinic.Client.PostAsync($"/api/procedures/{inactive}/active", new { isActive = false }, ct: ctx.Ct)).ExpectOk();

            var list = (await ctx.Ingest.GetAsync($"/api/ai/procedures?clinicId={clinic.ClinicId}&activeOnly=false", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId(list, clinic.BaseProcedureId, "Agent procedures");
            Expect.LacksId(list, inactive, "Agent procedures (inactive one)");
        }, Needs.Session | Needs.IngestKey)
        .Test("lead-context", "Read and update a lead's context", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("ai-ctx");
            (await ctx.Ingest.GetAsync($"/api/ai/leads/{leadId}?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk();
            var updated = (await ctx.Ingest.PatchAsync($"/api/ai/leads/{leadId}?clinicId={clinic.ClinicId}", new { city = "Dubai", desiredTimeline = "next month" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("Dubai", updated.Prop("city"), "City");
            // A lead from another clinic id must not resolve.
            (await ctx.Ingest.GetAsync($"/api/ai/leads/{leadId}?clinicId={Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.IngestKey)
        .Test("booking-flow", "Find a slot, book, reschedule and cancel", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("ai-book");
            await ctx.Fixtures.EnsureAvailabilityAsync();
            var q = $"clinicId={clinic.ClinicId}&leadId={leadId}";

            var available = (await ctx.Ingest.GetAsync($"/api/ai/appointments/available?{q}&procedureId={clinic.BaseProcedureId}&days=14", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("true", available.Prop("canCreateNewBooking"), "canCreateNewBooking before booking");
            var slots = available.Array("slots");
            Expect.True(slots.Count >= 3, "Fewer than 3 open slots offered to the agent.");

            var book = (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new
            {
                leadId, procedureId = clinic.BaseProcedureId, appointmentType = "consultation", scheduledStart = slots[0]!["start"]!.ToString(),
            }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("BOOKED", book.Prop("code"), "Booking result");

            var again = (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new
            {
                leadId, scheduledStart = slots[1]!["start"]!.ToString(),
            }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("EXISTING_UPCOMING_APPOINTMENT", again.Prop("code"), "Second booking for the same patient");

            var upcoming = (await ctx.Ingest.GetAsync($"/api/ai/appointments/upcoming?{q}", ct: ctx.Ct)).ExpectOk();
            Expect.True(upcoming.Array("appointments").Count == 1, "The patient should have exactly one upcoming appointment.");

            var moved = (await ctx.Ingest.PostAsync($"/api/ai/appointments/reschedule?{q}", new
            {
                scheduledStart = slots[2]!["start"]!.ToString(), reason = "Automation reschedule",
            }, ct: ctx.Ct)).ExpectOk();
            Expect.Equal(DateTimeOffset.Parse(slots[2]!["start"]!.ToString()), DateTimeOffset.Parse(moved.Str("scheduledStart")), "Rescheduled start");

            var cancel = (await ctx.Ingest.PostAsync($"/api/ai/appointments/cancel?{q}", new { reason = "Automation cancel" }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("CANCELED", cancel.Prop("code"), "Cancel result");
            Expect.True((await ctx.Ingest.GetAsync($"/api/ai/appointments/upcoming?{q}", ct: ctx.Ct)).ExpectOk().Array("appointments").Count == 0,
                "The cancelled appointment is still upcoming.");
        }, Needs.Session | Needs.IngestKey)
        .Test("slot-taken", "Booking a slot that's already taken is refused", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var first = await ctx.Fixtures.NewLeadAsync("ai-slot-a");
            var second = await ctx.Fixtures.NewLeadAsync("ai-slot-b");
            var start = await ctx.Fixtures.SlotStartAsync(8);

            Expect.EqualText("BOOKED", (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new { leadId = first, scheduledStart = start }, ct: ctx.Ct))
                .ExpectOk().Prop("code"), "First booking");
            Expect.EqualText("SLOT_UNAVAILABLE", (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new { leadId = second, scheduledStart = start }, ct: ctx.Ct))
                .ExpectOk().Prop("code"), "Second booking of the same slot");
        }, Needs.Session | Needs.IngestKey)
        .Test("schedule", "schedule_consultation books when the patient has nothing upcoming", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("ai-schedule");
            var start = await ctx.Fixtures.SlotStartAsync(10);
            var result = (await ctx.Ingest.PostAsync($"/api/ai/appointments/schedule?clinicId={clinic.ClinicId}&leadId={leadId}", new { scheduledStart = start }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("BOOKED", result.Prop("code"), "Schedule result");
        }, Needs.Session | Needs.IngestKey)
        .Test("handoff", "Agent hands a conversation to a human", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("ai-handoff");
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);
            var conversation = (await ctx.Ingest.PostAsync($"/api/ai/conversations/{conversationId}/handoff?clinicId={clinic.ClinicId}", new { reason = "Medical question" }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("human", conversation.Prop("mode"), "Mode after hand-off");
        }, Needs.Session | Needs.IngestKey)
        .Test("ai-send-blocked-in-human-mode", "AI reply is refused once a human has taken over", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("ai-mode");
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);
            (await clinic.Client.PostAsync($"/api/conversations/{conversationId}/take-over", ct: ctx.Ct)).ExpectOk();
            var response = (await ctx.Ingest.PostAsync($"/api/conversations/{conversationId}/messages/send?clinicId={clinic.ClinicId}",
                new { sender = "ai", content = "This must not be sent" }, ct: ctx.Ct)).ExpectStatus(409);
            Expect.EqualText("conversation_in_human_mode", response.Prop("code"), "Refusal code");
        }, Needs.Session | Needs.IngestKey)
        .Test("knowledge-search", "Knowledge search finds a fresh entry", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var docId = (await clinic.Client.PostAsync("/api/knowledge", new
            {
                title = $"Payment plans {ctx.Tag}", category = "pricing",
                content = "We offer interest-free payment plans over 6 or 12 months for all surgical procedures.", isActive = true,
            }, ct: ctx.Ct)).ExpectStatus(201).Id();
            var result = (await ctx.Ingest.PostAsync("/api/ai/knowledge/search", new { clinicId = clinic.ClinicId, query = "Can I pay in monthly installments?", limit = 3 }, ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId(result.Array("results"), docId, "Search results", "documentId");
            (await clinic.Client.DeleteAsync($"/api/knowledge/{docId}", ctx.Ct)).ExpectStatus(204, 200);
        }, Needs.Session | Needs.IngestKey, Risk.External)
        .Test("test-codes", "AI test endpoints list their codes", async ctx =>
        {
            (await ctx.Ingest.GetAsync("/api/ai/test/codes", ct: ctx.Ct)).ExpectOk().RequireJson();
        }, Needs.IngestKey)
        .Build();
}

/// <summary>The API the SculptFlowAdmin portal calls (platform-admin key).</summary>
public static class PlatformAdminSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin", "Admin portal API", "Platform-admin endpoints the admin portal relies on.")
        .Test("catalog", "Plans, entitlements and rate cards load", async ctx =>
        {
            (await ctx.Admin.GetAsync("/api/platform-admin/billing/plans", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync("/api/platform-admin/billing/entitlements", ct: ctx.Ct)).ExpectOk().RequireJson();
            var cards = (await ctx.Admin.GetAsync("/api/platform-admin/billing/rate-cards", ct: ctx.Ct)).ExpectOk().Array();
            if (cards.FirstOrDefault()?["code"]?.ToString() is { } code)
            {
                (await ctx.Admin.GetAsync($"/api/platform-admin/billing/rate-cards/{code}/rates", ct: ctx.Ct)).ExpectOk().Array();
            }
            (await ctx.Admin.GetAsync("/api/platform-admin/billing/provider-billing", ct: ctx.Ct)).ExpectOk().RequireJson();
        }, Needs.AdminKey)
        .Test("accounts", "The test clinic shows up in billing accounts", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var accounts = (await ctx.Admin.GetAsync("/api/platform-admin/billing/accounts", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId(accounts, clinic.ClinicId, "Billing accounts", "clinicId");
        }, Needs.Session | Needs.AdminKey)
        .Test("clinic", "Clinic billing detail, ledger, usage and reconciliation load", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var root = $"/api/platform-admin/billing/clinics/{clinic.ClinicId}";
            (await ctx.Admin.GetAsync(root, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{root}/ledger", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{root}/usage", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{root}/channel-accounts", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("true", (await ctx.Admin.GetAsync($"{root}/reconciliation", ct: ctx.Ct)).ExpectOk().Prop("isBalanced"), "Reconciliation");
        }, Needs.Session | Needs.AdminKey)
        .Test("report", "Revenue report loads", async ctx =>
        {
            (await ctx.Admin.GetAsync("/api/platform-admin/billing/report", ct: ctx.Ct)).ExpectOk().RequireJson();
        }, Needs.AdminKey)
        .Test("top-up", "Wallet top-up is applied once and the books still balance", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var root = $"/api/platform-admin/billing/clinics/{clinic.ClinicId}";
            var headers = new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-topup" };
            var body = new { amount = 25m, reference = $"automation-{ctx.Tag}", reason = "Automation test top-up" };
            (await ctx.Admin.PostAsync($"{root}/wallet/top-ups", body, headers, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PostAsync($"{root}/wallet/top-ups", body, headers, ctx.Ct)).ExpectOk(); // retry with the same key

            var ledger = (await ctx.Admin.GetAsync($"{root}/ledger?pageSize=100", ct: ctx.Ct)).ExpectOk();
            var topUps = ledger.Body.Split("wallet_top_up").Length - 1;
            Expect.Equal(1, topUps, "Wallet top-up entries in the ledger after a retried request");
            Expect.EqualText("true", (await ctx.Admin.GetAsync($"{root}/reconciliation", ct: ctx.Ct)).ExpectOk().Prop("isBalanced"), "Reconciliation");

            (await ctx.Admin.PostAsync($"{root}/wallet/top-ups", body, ct: ctx.Ct)).ExpectStatus(400); // no Idempotency-Key
        }, Needs.Session | Needs.AdminKey, Risk.Billing)
        .Test("automation-clinics", "Leftover automation clinics are listed", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var list = (await ctx.Admin.GetAsync("/api/platform-admin/automation/clinics", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId(list, clinic.ClinicId, "Automation clinics");
        }, Needs.Session | Needs.AdminKey)
        .Test("automation-guard", "Cleanup refuses to delete a clinic that isn't an automation clinic", async ctx =>
        {
            // A clinic whose name lacks the [Automation] prefix: rename the "other" test clinic, then try to delete it.
            var other = await ctx.RegisterClinicAsync("guard");
            (await other.Client.SubmitFormAsync("/settings/clinic-info", new Dictionary<string, string> { ["Name"] = $"Real clinic {ctx.Tag}" }, ct: ctx.Ct))
                .ExpectStatus(302);
            (await ctx.Admin.DeleteAsync($"/api/platform-admin/automation/clinics/{other.ClinicId}", ctx.Ct)).ExpectStatus(409);
            // Put the prefix back so the run's own cleanup can delete it.
            (await other.Client.SubmitFormAsync("/settings/clinic-info", new Dictionary<string, string> { ["Name"] = other.Name }, ct: ctx.Ct))
                .ExpectStatus(302);
        }, Needs.Session | Needs.AdminKey)
        .Build();
}

/// <summary>End-to-end: one patient from first contact to a booked procedure, checking each screen's data on the way.</summary>
public static class JourneySuite
{
    public static TestSuite Build() => new SuiteBuilder("journey", "Patient journey (end to end)", "A lead arrives, messages in, the AI books a consultation, staff run the visit and book surgery.")
        .Test("full", "Lead to booked surgery", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var api = clinic.Client;

            ctx.Log("Step 1: lead arrives from a lead form");
            var leadId = await ctx.Fixtures.NewLeadAsync("journey");

            ctx.Log("Step 2: patient writes in, n8n ingests the message");
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);
            (await ctx.Ingest.PostAsync("/api/messages/ingest", new
            {
                clinicId = clinic.ClinicId, conversationId, leadId, eventType = "customer_message", channel = "website",
                content = "Hello, can I book a consultation next week?", externalMessageId = $"auto-{Guid.NewGuid():N}", receivedAt = DateTimeOffset.UtcNow,
            }, ct: ctx.Ct)).ExpectOk();

            ctx.Log("Step 3: AI agent qualifies the lead and books a slot");
            (await ctx.Ingest.PatchAsync($"/api/ai/leads/{leadId}?clinicId={clinic.ClinicId}", new { desiredTimeline = "next week" }, ctx.Ct)).ExpectOk();
            var slot = await ctx.Fixtures.SlotStartAsync(12);
            var booked = (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new
            {
                leadId, procedureId = clinic.BaseProcedureId, scheduledStart = slot,
            }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("BOOKED", booked.Prop("code"), "AI booking");
            var appointmentId = Guid.Parse(booked.Prop("appointment")["id"]!.ToString());

            ctx.Log("Step 4: staff see it in the inbox, on the calendar and on the lead");
            var messages = (await api.GetAsync($"/api/conversations/{conversationId}/messages", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(messages.Count >= 1, "The patient's message is missing from the inbox.");
            var month = DateTimeOffset.Parse(slot).UtcDateTime;
            var calendar = (await api.GetAsync($"/api/appointments/calendar?year={month.Year}&month={month.Month}", ct: ctx.Ct)).ExpectOk();
            Expect.True(calendar.Body.Contains(appointmentId.ToString(), StringComparison.OrdinalIgnoreCase), "The AI-booked appointment is not on the calendar.");
            var lead = (await api.GetAsync($"/api/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
            ctx.Log($"Lead status after booking: {lead.Json?["status"]}");

            ctx.Log("Step 5: patient attends; staff record the visit");
            (await api.PatchAsync($"/api/appointments/{appointmentId}/status", new { status = "attended" }, ctx.Ct)).ExpectOk();
            (await api.PostAsync($"/api/leads/{leadId}/status", new { status = "consultation_attended", qualificationStatus = "hot" }, ct: ctx.Ct)).ExpectOk();

            ctx.Log("Step 6: quote, deposit and surgery booking");
            var bookingId = (await api.PostAsync("/api/procedure-bookings", new
            {
                leadId, procedureId = clinic.BaseProcedureId, appointmentId, quotedAmount = 6000m, currencyCode = "USD",
            }, ct: ctx.Ct)).ExpectStatus(201).Id();
            (await api.PatchAsync($"/api/procedure-bookings/{bookingId}", new { status = "deposit_paid", depositAmount = 1000m }, ctx.Ct)).ExpectOk();
            (await api.PatchAsync($"/api/procedure-bookings/{bookingId}", new { status = "booked", procedureDate = DateTimeOffset.UtcNow.AddDays(45).ToString("O") }, ctx.Ct)).ExpectOk();
            (await api.PostAsync($"/api/leads/{leadId}/status", new { status = "surgery_booked" }, ct: ctx.Ct)).ExpectOk();

            ctx.Log("Step 7: dashboard reflects the journey");
            var finalLead = (await api.GetAsync($"/api/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("surgery_booked", finalLead.Prop("status"), "Final lead status");
            (await api.GetAsync("/api/dashboard/summary", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId((await api.GetAsync("/api/procedure-bookings?status=booked&take=200", ct: ctx.Ct)).ExpectOk().Array("items"), bookingId, "Booked procedures");
        }, Needs.Session | Needs.IngestKey)
        .Build();
}
