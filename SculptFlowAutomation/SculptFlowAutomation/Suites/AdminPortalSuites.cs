using System.Text.Json.Nodes;
using SculptFlowAutomation.Core;
using SculptFlowAutomation.Http;

namespace SculptFlowAutomation.Suites;

/// <summary>Shared helpers for the platform-admin suites (everything the SculptFlowAdmin portal calls).</summary>
internal static class AdminKit
{
    public const string Root = "/api/platform-admin";

    /// <summary>The test clinic's only staff member (the user who signed up).</summary>
    public static async Task<string> StaffUserIdAsync(TestContext ctx)
    {
        var clinic = await ctx.ClinicAsync();
        var staff = (await clinic.Client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectOk().Array();
        return staff.First(s => string.Equals(s?["email"]?.ToString(), clinic.Email, StringComparison.OrdinalIgnoreCase))!["userId"]!.ToString();
    }

    /// <summary>Paged admin lists answer { items, total, page, pageSize }.</summary>
    public static JsonArray Items(ApiResponse response)
    {
        response.Prop("total");
        return response.Array("items");
    }

    /// <summary>Every admin write answers PlatformAdminChange { clinicId } for the portal's audit log.</summary>
    public static void ExpectChangeFor(ApiResponse response, Guid clinicId) =>
        Expect.EqualText(clinicId.ToString(), response.Prop("clinicId"), "Audited clinic");
}

public static class AdminClinicsSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-clinics", "Admin: clinics", "Clinic list, detail, counts, edit and activate/deactivate from the admin portal.")
        .Test("list", "Clinic list, search, filter and options", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var found = AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics?search={Uri.EscapeDataString(clinic.Name)}", ct: ctx.Ct)).ExpectOk());
            Expect.ContainsId(found, clinic.ClinicId, "Search by name");
            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics?active=true&search={ctx.Tag}", ct: ctx.Ct)).ExpectOk()), clinic.ClinicId, "Active filter");
            Expect.LacksId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics?active=false&search={ctx.Tag}", ct: ctx.Ct)).ExpectOk()), clinic.ClinicId, "Inactive filter");
            Expect.True(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics?search=no-such-clinic-{Guid.NewGuid():N}", ct: ctx.Ct)).ExpectOk()).Count == 0, "A nonsense search returned clinics.");
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics/options", ct: ctx.Ct)).ExpectOk().Array(), clinic.ClinicId, "Clinic options");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics?page=0", ct: ctx.Ct)).ExpectOk(); // page 0 is clamped, not an error
        }, Needs.Session | Needs.AdminKey)
        .Test("detail", "Clinic detail and counts match what the clinic did", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            await ctx.Fixtures.LeadAsync();
            var detail = (await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText(clinic.Name, detail.Prop("name"), "Name");
            var counts = (await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}/counts", ct: ctx.Ct)).ExpectOk();
            Expect.True(int.Parse(counts.Str("leads")) >= 1, "Lead count is 0 after creating a lead.");
            Expect.True(int.Parse(counts.Str("staff")) == 1, $"Staff count is {counts.Str("staff")}, expected 1.");
            Expect.True(int.Parse(counts.Str("procedures")) >= 1, "Procedure count is 0.");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("update", "Edit clinic details; invalid timezone and empty name are rejected", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var body = new
            {
                name = clinic.Name, phone = "+15550100999", email = $"front-desk-{ctx.Tag}@{TestContext.EmailDomain}", website = "https://example.com",
                countryCode = "LB", timezone = "UTC", address = "1 Test Street", operatingHours = "Mon-Fri 9-17", consultationInfo = "Free 30 min",
            };
            AdminKit.ExpectChangeFor((await ctx.Admin.PutAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}", body, ctx.Ct)).ExpectOk(), clinic.ClinicId);
            var detail = (await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("1 Test Street", detail.Prop("address"), "Address");
            Expect.EqualText("LB", detail.Prop("countryCode"), "Country");

            // The clinic sees the same values (AI clinic-info reads them too).
            if (ctx.Env.HasIngestKey)
            {
                var info = (await ctx.Ingest.GetAsync($"/api/ai/clinic-info?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk();
                Expect.EqualText("1 Test Street", info.Prop("address"), "Address the AI sees");
            }

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}", body with { timezone = "Mars/Olympus" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}", body with { name = "" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/clinics/{Guid.NewGuid()}", body, ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("deactivate", "A deactivated clinic is locked out, and reactivating restores access", async ctx =>
        {
            var clinic = await ctx.RegisterClinicAsync("inactive");
            (await clinic.Client.GetAsync("/api/leads", ct: ctx.Ct)).ExpectOk();

            AdminKit.ExpectChangeFor((await ctx.Admin.PutAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}/active", new { active = false }, ctx.Ct)).ExpectOk(), clinic.ClinicId);
            Expect.EqualText("false", (await ctx.Admin.GetAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}", ct: ctx.Ct)).ExpectOk().Prop("isActive"), "isActive");
            var whileInactive = await clinic.Client.GetAsync("/api/leads", ct: ctx.Ct);
            var (_, signIn) = await ctx.SignInAsync(clinic.Email, clinic.Password);

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/clinics/{clinic.ClinicId}/active", new { active = true }, ctx.Ct)).ExpectOk();
            (await clinic.Client.GetAsync("/api/leads", ct: ctx.Ct)).ExpectOk();

            Expect.True(!whileInactive.IsSuccess,
                $"While the clinic was deactivated its signed-in staff could still use the API (GET /api/leads -> {whileInactive.Status}). clinics.is_active isn't enforced.");
            Expect.True(signIn.Status != 302,
                "Staff of a deactivated clinic could still sign in. clinics.is_active isn't enforced at sign-in.");
        }, Needs.Session | Needs.AdminKey)
        .Build();
}

public static class AdminStaffSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-staff", "Admin: staff", "Staff accounts: list, detail, lock, disable membership, reset password, confirm email.")
        .Test("list", "Staff list and detail", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var userId = await AdminKit.StaffUserIdAsync(ctx);
            var items = AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/staff?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk());
            Expect.True(items.Any(s => s?["userId"]?.ToString() == userId), "The clinic's user is not in the staff list.");
            Expect.True(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/staff?search={Uri.EscapeDataString(clinic.Email)}", ct: ctx.Ct)).ExpectOk()).Count == 1, "Searching by email didn't find exactly one user.");
            var detail = (await ctx.Admin.GetAsync($"{AdminKit.Root}/staff/{userId}", ct: ctx.Ct)).ExpectOk();
            Expect.True(detail.Body.Contains(clinic.Email, StringComparison.OrdinalIgnoreCase), "Staff detail doesn't show the email.");
            Expect.True(!detail.Body.Contains("passwordHash", StringComparison.OrdinalIgnoreCase) && !detail.Body.Contains("securityStamp", StringComparison.OrdinalIgnoreCase),
                "Staff detail exposes credential fields.");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/staff/no-such-user-{ctx.Tag}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("lock", "A locked account can't sign in until unlocked", async ctx =>
        {
            var clinic = await ctx.RegisterClinicAsync("locked");
            var userId = (await clinic.Client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectOk().Array()[0]!["userId"]!.ToString();

            AdminKit.ExpectChangeFor((await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/locked", new { value = true }, ctx.Ct)).ExpectOk(), clinic.ClinicId);
            var (_, locked) = await ctx.SignInAsync(clinic.Email, clinic.Password);
            Expect.True(locked.Status != 302, "A locked account signed in.");

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/locked", new { value = false }, ctx.Ct)).ExpectOk();
            var (_, unlocked) = await ctx.SignInAsync(clinic.Email, clinic.Password);
            unlocked.ExpectStatus(302);
        }, Needs.Session | Needs.AdminKey)
        .Test("membership", "Disabling a membership cuts API access; enabling restores it", async ctx =>
        {
            var clinic = await ctx.RegisterClinicAsync("member");
            var userId = (await clinic.Client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectOk().Array()[0]!["userId"]!.ToString();

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/membership-active", new { active = false }, ctx.Ct)).ExpectOk();
            (await clinic.Client.GetAsync("/api/leads", ct: ctx.Ct)).ExpectRefused();
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/membership-active", new { active = true }, ctx.Ct)).ExpectOk();
            (await clinic.Client.GetAsync("/api/leads", ct: ctx.Ct)).ExpectOk();
        }, Needs.Session | Needs.AdminKey)
        .Test("password", "Admin password reset: new password works, old one doesn't, weak one is refused", async ctx =>
        {
            var clinic = await ctx.RegisterClinicAsync("password");
            var userId = (await clinic.Client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectOk().Array()[0]!["userId"]!.ToString();
            var newPassword = "Reset-" + Guid.NewGuid().ToString("N")[..12];

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/password", new { password = "123" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/password", new { password = newPassword }, ctx.Ct)).ExpectOk();
            var (_, withOld) = await ctx.SignInAsync(clinic.Email, clinic.Password);
            Expect.True(withOld.Status != 302, "The old password still works after a reset.");
            var (_, withNew) = await ctx.SignInAsync(clinic.Email, newPassword);
            withNew.ExpectStatus(302);
        }, Needs.Session | Needs.AdminKey)
        .Test("email-confirmed", "Mark the email confirmed and back", async ctx =>
        {
            var userId = await AdminKit.StaffUserIdAsync(ctx);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/email-confirmed", new { value = true }, ctx.Ct)).ExpectOk();
            Expect.True((await ctx.Admin.GetAsync($"{AdminKit.Root}/staff/{userId}", ct: ctx.Ct)).ExpectOk().Body.Contains("\"emailConfirmed\":true"),
                "emailConfirmed is not true after confirming.");
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/{userId}/email-confirmed", new { value = false }, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/staff/no-such-user-{ctx.Tag}/email-confirmed", new { value = true }, ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Build();
}

public static class AdminLeadsSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-leads", "Admin: leads and inbox", "Leads, conversations, messages and appointments across clinics.")
        .Test("leads", "Lead list, filters, detail, events and edit", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("admin-lead");
            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk()), leadId, "Lead list");
            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads?clinicId={clinic.ClinicId}&status=new&search={Uri.EscapeDataString("admin-lead")}", ct: ctx.Ct)).ExpectOk()), leadId, "Filtered lead list");
            Expect.LacksId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads?clinicId={clinic.ClinicId}&status=lost", ct: ctx.Ct)).ExpectOk()), leadId, "Lost leads");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
            var events = (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/{leadId}/events", ct: ctx.Ct)).ExpectOk();
            Expect.True(events.Body.Contains("lead_created"), "The lead's history has no lead_created event.");

            AdminKit.ExpectChangeFor((await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/{leadId}", new
            {
                status = "qualified", qualificationStatus = "hot", marketingOptIn = false, notes = $"Admin edit {ctx.Tag}",
            }, ctx.Ct)).ExpectOk(), clinic.ClinicId);
            var lead = (await clinic.Client.GetAsync($"/api/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("qualified", lead.Prop("status"), "Status the clinic sees");

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/{leadId}", new { status = "bogus", qualificationStatus = "hot", marketingOptIn = true }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("conversations", "Conversation list, detail, messages, mode and status", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("admin-convo");
            var conversationId = await ctx.Fixtures.NewConversationAsync(leadId);
            (await clinic.Client.PostAsync($"/api/conversations/{conversationId}/messages", new
            {
                direction = "inbound", senderType = "lead", channel = "website", messageType = "text", content = $"Admin view {ctx.Tag}",
            }, ct: ctx.Ct)).ExpectStatus(200, 201);

            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/conversations?clinicId={clinic.ClinicId}&leadId={leadId}", ct: ctx.Ct)).ExpectOk()), conversationId, "Conversations");
            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/conversations?clinicId={clinic.ClinicId}&channel=website&mode=ai&status=active", ct: ctx.Ct)).ExpectOk()), conversationId, "Filtered conversations");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk();
            var messages = (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/conversations/{conversationId}/messages?take=10", ct: ctx.Ct)).ExpectOk();
            Expect.True(messages.Body.Contains($"Admin view {ctx.Tag}"), "The message isn't in the admin's conversation view.");
            Expect.True(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/messages?clinicId={clinic.ClinicId}&search={Uri.EscapeDataString("Admin view " + ctx.Tag)}", ct: ctx.Ct)).ExpectOk()).Count >= 1,
                "Message search found nothing.");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/messages?clinicId={clinic.ClinicId}&failedOnly=true", ct: ctx.Ct)).ExpectOk();

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/conversations/{conversationId}/mode", new { mode = "human" }, ctx.Ct)).ExpectOk();
            Expect.True((await clinic.Client.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Body.Contains("\"mode\":\"human\""),
                "The clinic doesn't see the conversation in human mode after the admin switched it.");
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/conversations/{conversationId}/mode", new { mode = "robot" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/conversations/{conversationId}/status", new { status = "closed" }, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/conversations/{conversationId}/status", new { status = "bogus" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/conversations/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("appointments", "Appointment list and status change", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("admin-appt");
            var start = await ctx.Fixtures.SlotStartAsync(14);
            var id = (await clinic.Client.PostAsync("/api/appointments", new { leadId, appointmentType = "consultation", scheduledStart = start }, ct: ctx.Ct)).ExpectStatus(201).Id();

            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/appointments?clinicId={clinic.ClinicId}&leadId={leadId}", ct: ctx.Ct)).ExpectOk()), id, "Appointments");
            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/leads/appointments?clinicId={clinic.ClinicId}&upcomingOnly=true&status=booked", ct: ctx.Ct)).ExpectOk()), id, "Upcoming booked");

            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/appointments/{id}/status", new { status = "confirmed" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("confirmed", (await clinic.Client.GetAsync($"/api/appointments/{id}", ct: ctx.Ct)).ExpectOk().Prop("status"), "Status the clinic sees");
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/appointments/{id}/status", new { status = "bogus" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/leads/appointments/{Guid.NewGuid()}/status", new { status = "confirmed" }, ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Build();
}

public static class AdminContentSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-content", "Admin: content", "Procedures, knowledge, websites, templates and campaigns across clinics.")
        .Test("procedures", "Procedure list and activate/deactivate", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var id = (await clinic.Client.PostAsync("/api/procedures", new { name = $"Admin toggle {ctx.Tag}", consultationDuration = 30 }, ct: ctx.Ct)).ExpectStatus(201).Id();
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminKit.Root}/content/procedures?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk().Array(), id, "Procedures");

            AdminKit.ExpectChangeFor((await ctx.Admin.PutAsync($"{AdminKit.Root}/content/procedures/{id}/active", new { active = false }, ctx.Ct)).ExpectOk(), clinic.ClinicId);
            Expect.LacksId((await clinic.Client.GetAsync("/api/procedures", ct: ctx.Ct)).ExpectOk().Array(), id, "Clinic's active procedures");
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/content/procedures/{id}/active", new { active = true }, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/content/procedures/{Guid.NewGuid()}/active", new { active = true }, ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("knowledge-settings", "Clinic search settings: read, update, validate", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var path = $"{AdminKit.Root}/content/knowledge/settings/{clinic.ClinicId}";
            (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync(path, new { topK = 6, minimumSimilarity = 0.25 }, ctx.Ct)).ExpectOk();
            Expect.EqualText("6", (await clinic.Client.GetAsync("/api/knowledge/settings", ct: ctx.Ct)).ExpectOk().Prop("topK"), "topK the clinic sees");
            (await ctx.Admin.PutAsync(path, new { topK = 0, minimumSimilarity = 0.25 }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PutAsync(path, new { topK = 4, minimumSimilarity = 1.5 }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/content/knowledge/settings/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("lists", "Knowledge, websites, templates and campaigns lists load", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/content/knowledge?clinicId={clinic.ClinicId}&active=true&search=x", ct: ctx.Ct)).ExpectOk());
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/content/knowledge/websites?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/content/templates?clinicId={clinic.ClinicId}&status=approved", ct: ctx.Ct)).ExpectOk().Array();
            AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/content/campaigns?clinicId={clinic.ClinicId}&status=running", ct: ctx.Ct)).ExpectOk());
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/content/knowledge/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/content/knowledge/{Guid.NewGuid()}/active", new { active = false }, ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/content/campaigns/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            // Recipients of an unknown campaign is just an empty page.
            Expect.True(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/content/campaigns/{Guid.NewGuid()}/recipients", ct: ctx.Ct)).ExpectOk()).Count == 0,
                "An unknown campaign has recipients.");
            (await ctx.Admin.PostAsync($"{AdminKit.Root}/content/campaigns/{Guid.NewGuid()}/cancel", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("knowledge-document", "Knowledge entry detail and deactivate from the admin", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var docId = (await clinic.Client.PostAsync("/api/knowledge", new
            {
                title = $"Admin doc {ctx.Tag}", category = "faq", content = "Parking is free for patients.", isActive = true,
            }, ct: ctx.Ct)).ExpectStatus(201).Id();
            Expect.ContainsId(AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/content/knowledge?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk()), docId, "Knowledge list");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/content/knowledge/{docId}", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/content/knowledge/{docId}/active", new { active = false }, ctx.Ct)).ExpectOk();
            Expect.EqualText("false", (await clinic.Client.GetAsync($"/api/knowledge/{docId}", ct: ctx.Ct)).ExpectOk().Prop("isActive"), "isActive the clinic sees");
        }, Needs.Session | Needs.AdminKey, Risk.External)
        .Build();
}

public static class AdminChannelsSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-channels", "Admin: channels", "Channel, calendar and TikTok connections across clinics (none connected on the test clinic).")
        .Test("lists", "Channel, calendar and TikTok lists load", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var channels = (await ctx.Admin.GetAsync($"{AdminKit.Root}/channels?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(channels.All(c => c?["status"]?.ToString() != "connected"), "The test clinic shows a connected channel, but it never connected one.");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/channels?problemsOnly=true&channel=whatsapp", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/channels/calendars?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/channels/tiktok?clinicId={clinic.ClinicId}", ct: ctx.Ct)).ExpectOk().Array();
        }, Needs.Session | Needs.AdminKey)
        .Test("unknown", "Unknown channel ids return 404 for every action", async ctx =>
        {
            var id = Guid.NewGuid();
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/channels/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/channels/{id}/health-events", ct: ctx.Ct)).ExpectStatus(404, 200);
            (await ctx.Admin.PostAsync($"{AdminKit.Root}/channels/{id}/disconnect", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PostAsync($"{AdminKit.Root}/channels/calendars/{id}/disconnect", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/channels/calendars/{id}/sync", new { value = false }, ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PostAsync($"{AdminKit.Root}/channels/tiktok/{id}/disconnect", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.AdminKey)
        .Build();
}

public static class AdminOverviewSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-overview", "Admin: overview", "The admin portal's home: totals, message chart, problems, recent clinics and the event log.")
        .Test("totals", "Totals and recent clinics include the test clinic", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var totals = (await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/totals", ct: ctx.Ct)).ExpectOk();
            Expect.True(int.Parse(totals.Str("clinics")) >= 1, "Total clinics is 0 with a test clinic registered.");
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/recent-clinics?take=50", ct: ctx.Ct)).ExpectOk().Array(), clinic.ClinicId, "Recent clinics");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/problems", ct: ctx.Ct)).ExpectOk().Array();
        }, Needs.Session | Needs.AdminKey)
        .Test("daily-messages", "Message chart covers the asked number of days", async ctx =>
        {
            var days = (await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/daily-messages?days=7", ct: ctx.Ct)).ExpectOk().Array();
            Expect.Equal(7, days.Count, "Days in the chart");
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/daily-messages?days=0", ct: ctx.Ct)).ExpectOk(); // clamped
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/daily-messages?days=100000", ct: ctx.Ct)).ExpectOk();
        }, Needs.AdminKey)
        .Test("events", "Event log, filtered by clinic and type", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            await ctx.Fixtures.LeadAsync();
            var types = (await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/event-types", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(types.Any(t => t?.ToString() == "lead_created"), "lead_created isn't a known event type.");
            var events = AdminKit.Items((await ctx.Admin.GetAsync($"{AdminKit.Root}/overview/events?clinicId={clinic.ClinicId}&eventType=lead_created", ct: ctx.Ct)).ExpectOk());
            Expect.True(events.Count >= 1, "No lead_created events for the test clinic.");
            Expect.True(events.All(e => e?["eventType"]?.ToString() == "lead_created"), "The event-type filter returned other types.");
        }, Needs.Session | Needs.AdminKey)
        .Build();
}

public static class AdminSettingsSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-settings", "Admin: settings", "Global configuration overrides. Read and validation only: changing a value would change it for every clinic.")
        .Test("list", "Settings list shows every declared setting without secret values", async ctx =>
        {
            var settings = (await ctx.Admin.GetAsync($"{AdminKit.Root}/settings", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(settings.Count > 0, "No settings returned.");
            var leaked = settings.Where(s => s?["isSecret"]?.ToString() == "true"
                && s?["storedValue"] is JsonValue v && v.ToString().Length > 0 && !v.ToString().Contains('*') && !v.ToString().Contains('•')).ToList();
            Expect.True(leaked.Count == 0, $"Secret settings show their value: {string.Join(", ", leaked.Select(s => $"{s?["section"]}/{s?["key"]}"))}.");
        }, Needs.AdminKey)
        .Test("validation", "Out-of-range and unknown settings are rejected", async ctx =>
        {
            var settings = (await ctx.Admin.GetAsync($"{AdminKit.Root}/settings", ct: ctx.Ct)).ExpectOk().Array();
            var bounded = settings.FirstOrDefault(s => s?["type"]?.ToString() == "Int" && s?["max"] is not null && s["max"]!.GetValueKind() == System.Text.Json.JsonValueKind.Number);
            if (bounded is not null)
            {
                var tooBig = (long)bounded["max"]!.GetValue<double>() + 1;
                (await ctx.Admin.PutAsync($"{AdminKit.Root}/settings/{bounded["section"]}/{bounded["key"]}", new { value = tooBig.ToString(), note = "automation range check" }, ctx.Ct))
                    .ExpectStatus(400);
                (await ctx.Admin.PutAsync($"{AdminKit.Root}/settings/{bounded["section"]}/{bounded["key"]}", new { value = "not-a-number", note = "automation type check" }, ctx.Ct))
                    .ExpectStatus(400);
            }
            (await ctx.Admin.PutAsync($"{AdminKit.Root}/settings/NoSuchSection/NoSuchKey{ctx.Tag}", new { value = "1" }, ctx.Ct)).ExpectStatus(404, 400);
            (await ctx.Admin.DeleteAsync($"{AdminKit.Root}/settings/NoSuchSection/NoSuchKey{ctx.Tag}", ctx.Ct)).ExpectStatus(404, 400);

            // Nothing changed: the bounded setting still has its old value.
            if (bounded is not null)
            {
                var after = (await ctx.Admin.GetAsync($"{AdminKit.Root}/settings", ct: ctx.Ct)).ExpectOk().Array()
                    .First(s => s?["section"]?.ToString() == bounded["section"]!.ToString() && s?["key"]?.ToString() == bounded["key"]!.ToString());
                Expect.Equal(bounded["effectiveValue"]?.ToString(), after!["effectiveValue"]?.ToString(), "Value after rejected writes");
            }
        }, Needs.AdminKey)
        .Build();
}

public static class AdminCacheSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-cache", "Admin: cache", "Cache keys: list, read and remove (only the test clinic's own keys are removed).")
        .Test("keys", "List, read and remove the test clinic's cached entitlements", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await clinic.Client.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk(); // usually warms billing:entitlements:{clinic}
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/cache/keys", ct: ctx.Ct)).ExpectOk().Array();
            var key = $"billing:entitlements:{clinic.ClinicId:N}";
            var mine = (await ctx.Admin.GetAsync($"{AdminKit.Root}/cache/keys?prefix={Uri.EscapeDataString(key)}", ct: ctx.Ct)).ExpectOk().Array();
            ctx.Log($"Cached keys for the test clinic: {mine.Count}");
            if (mine.Count > 0)
            {
                (await ctx.Admin.GetAsync($"{AdminKit.Root}/cache/key?key={Uri.EscapeDataString(key)}", ct: ctx.Ct)).ExpectOk();
            }
            (await ctx.Admin.DeleteAsync($"{AdminKit.Root}/cache/key?key={Uri.EscapeDataString(key)}", ctx.Ct)).ExpectStatus(204, 404);
            Expect.True((await ctx.Admin.GetAsync($"{AdminKit.Root}/cache/keys?prefix={Uri.EscapeDataString(key)}", ct: ctx.Ct)).ExpectOk().Array().Count == 0, "The removed key is still listed.");
            (await ctx.Admin.DeleteAsync($"{AdminKit.Root}/cache/keys?prefix={Uri.EscapeDataString(key)}", ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/cache/key?key=automation:no-such-key:{ctx.Tag}", ct: ctx.Ct)).ExpectStatus(404, 200);
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/cache/key", ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.DeleteAsync($"{AdminKit.Root}/cache/keys?prefix=", ctx.Ct)).ExpectStatus(400);
        }, Needs.Session | Needs.AdminKey)
        .Test("clear-all", "Clearing the whole cache works and the app keeps serving", async ctx =>
        {
            (await ctx.Admin.DeleteAsync($"{AdminKit.Root}/cache", ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{AdminKit.Root}/settings", ct: ctx.Ct)).ExpectOk(); // settings reload after a clear
            (await (await ctx.ApiAsync()).GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk();
        }, Needs.Session | Needs.AdminKey, Risk.Shared)
        .Build();
}
