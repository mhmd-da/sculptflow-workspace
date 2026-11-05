using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Template and account/health events, signed and via n8n (t10-02, t10-06, t8-12).</summary>
[Suite(53)]
public static class WhatsAppEventsSuite
{
    private const Needs Signed = Needs.Session | Needs.Database | Needs.MetaSecret;
    private const Needs Events = Needs.Session | Needs.Database | Needs.IngestKey;

    private static async Task<(TestClient Client, FakeChannel Channel)> HealthClientAsync(TestContext ctx)
    {
        var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-health");
        await WhatsAppHelpers.ResetHealthAsync(ctx, client);
        return (client, channel);
    }

    private static Task<ApiResponse> WebhookEventAsync(TestContext ctx, FakeChannel channel, string field, JsonObject value) =>
        WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Change(field, value)));

    private static Task<ApiResponse> HealthEventAsync(TestContext ctx, TestClient client, FakeChannel channel, string eventType, string? status = null, string? quality = null) =>
        ctx.Ingest.PostAsync("/api/integrations/whatsapp/health/events", new
        {
            clientId = client.ClientId, phoneNumberId = channel.PhoneNumberId, eventType, status, qualityRating = quality, occurredAt = DateTimeOffset.UtcNow,
        }, ct: ctx.Ct);

    /// <summary>A local template on the "wa-tplev" client (its submission to Meta fails with the fake token).</summary>
    private static async Task<(TestClient Client, FakeChannel Channel, string Name)> TemplateAsync(TestContext ctx, string label, string category = "utility")
    {
        var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-tplev");
        var name = $"auto_{ctx.Tag}_{label}_{Random.Shared.Next(1000, 9999)}".ToLowerInvariant();
        (await client.Http.PostAsync("/api/whatsapp/templates", new { name, category, language = "en_US", body = "Hello {{1}}, your order is ready." }, ct: ctx.Ct)).ExpectStatus(201);
        return (client, channel, name);
    }

    private static async Task<JsonNode> TemplateByNameAsync(TestContext ctx, TestClient client, string name) =>
        (await client.Http.GetAsync("/api/whatsapp/templates", ct: ctx.Ct)).ExpectOk().Array().FirstOrDefault(t => t?["name"]?.ToString() == name)
        ?? throw new TestFailure($"Template {name} isn't listed.");

    private static JsonObject TemplateStatus(string name, string status, string reason = "NONE") => new()
    {
        ["event"] = status, ["message_template_id"] = 990000000000 + Random.Shared.Next(100000, 999999), ["message_template_name"] = name,
        ["message_template_language"] = "en_US", ["reason"] = reason,
    };

    public static TestSuite Build() => new SuiteBuilder("wa-events", "WhatsApp: template and account events",
            "Template status/category/quality events and account/phone health events, through the signed webhook (Meta's shapes) and the " +
            "n8n event endpoints; WhatsApp health as the client sees it.")
        .Test("template-approved", "WhatsApp events: an APPROVED template event approves the local template", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "approve");
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "APPROVED"));
            Expect.EqualText("approved", (await TemplateByNameAsync(ctx, client, name))["status"], "Status");
        }, needs: Signed)
        .Test("template-approved-reason", "WhatsApp events: an approved template carries no rejection reason (not \"NONE\")", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "none");
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "APPROVED"));
            var reason = (await TemplateByNameAsync(ctx, client, name))["rejectionReason"]?.ToString();
            Expect.True(string.IsNullOrEmpty(reason), $"The approved template shows the rejection reason \"{(reason?.Length > 80 ? reason[..80] + "…" : reason)}\".");
        }, needs: Signed, finding: "t10-06,t8-12")
        .Test("template-rejected", "WhatsApp events: a REJECTED event marks the template rejected with Meta's reason", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "reject");
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "REJECTED", "INVALID_FORMAT"));
            var template = await TemplateByNameAsync(ctx, client, name);
            Expect.EqualText("rejected", template["status"], "Status");
            Expect.True(template["rejectionReason"]?.ToString().Contains("INVALID_FORMAT") == true, $"Rejection reason: {template["rejectionReason"]}.");
        }, needs: Signed)
        .Test("template-rejection-info", "WhatsApp events: a rejection keeps Meta's explanation and recommendation (rejection_info)", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "rejinfo");
            var evt = TemplateStatus(name, "REJECTED", "INVALID_FORMAT");
            evt["rejection_info"] = new JsonObject { ["reason"] = "Variables can't be at the start or end.", ["recommendation"] = "Add text around {{1}}." };
            await WebhookEventAsync(ctx, channel, "message_template_status_update", evt);
            var reason = (await TemplateByNameAsync(ctx, client, name))["rejectionReason"]?.ToString() ?? "";
            Expect.True(reason.Contains("Variables can't be at the start") && reason.Contains("Add text around"), $"Meta's explanation was dropped: \"{reason}\".");
        }, needs: Signed, finding: "t10-06")
        .Test("template-notified", "WhatsApp events: a rejected template notifies the owner once, linking to Templates", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "notify");
            var before = (await InboxHelpers.NotificationsAsync(ctx, client)).Count(n => n?["type"]?.ToString() == "TEMPLATE_STATUS_CHANGED");
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "REJECTED", "INVALID_FORMAT"));
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "REJECTED", "INVALID_FORMAT"));
            var notes = (await InboxHelpers.NotificationsAsync(ctx, client)).Where(n => n?["type"]?.ToString() == "TEMPLATE_STATUS_CHANGED").ToList();
            Expect.True(notes.Count == before + 1, $"Expected one TEMPLATE_STATUS_CHANGED notification for the rejection (repeated events included); found {notes.Count - before}.");
            var note = notes[0]!;
            Expect.True(note.ToJsonString().Contains(name, StringComparison.Ordinal) && note["link"]?.ToString() == "/WhatsApp/Templates",
                $"The notification doesn't name the template or link to Templates: {note.ToJsonString()}");
        }, needs: Signed, finding: "t8-15")
        .Test("template-paused", "WhatsApp events: a PAUSED event pauses the template", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "pause");
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "APPROVED"));
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "PAUSED"));
            Expect.EqualText("paused", (await TemplateByNameAsync(ctx, client, name))["status"], "Status");
        }, needs: Signed)
        .Test("template-reinstated", "WhatsApp events: a REINSTATED template is approved (usable) again", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "reinstate");
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "PAUSED"));
            await WebhookEventAsync(ctx, channel, "message_template_status_update", TemplateStatus(name, "REINSTATED"));
            var status = (await TemplateByNameAsync(ctx, client, name))["status"]?.ToString();
            Expect.True(status == "approved", $"A reinstated template has status \"{status}\" and can't be sent.");
        }, needs: Signed, finding: "t10-06")
        .Test("template-category", "WhatsApp events: Meta's template_category_update changes the template's current category", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "category");
            await WebhookEventAsync(ctx, channel, "template_category_update", new JsonObject
            {
                ["message_template_id"] = 990000000000 + Random.Shared.Next(100000, 999999), ["message_template_name"] = name, ["message_template_language"] = "en_US",
                ["previous_category"] = "UTILITY", ["new_category"] = "MARKETING",
            });
            var template = await TemplateByNameAsync(ctx, client, name);
            Expect.True(string.Equals(template["currentCategory"]?.ToString(), "marketing", StringComparison.OrdinalIgnoreCase),
                $"After Meta re-categorised it to MARKETING the template's current category is \"{template["currentCategory"]}\" (billing and the owner rely on it).");
        }, needs: Signed, finding: "t10-06")
        .Test("template-quality-unknown", "WhatsApp events: a quality update for an unknown template creates no empty template", async ctx =>
        {
            var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-tplev");
            var name = $"auto_{ctx.Tag}_ghost_{Random.Shared.Next(1000, 9999)}".ToLowerInvariant();
            await WebhookEventAsync(ctx, channel, "message_template_quality_update", new JsonObject
            {
                ["previous_quality_score"] = "GREEN", ["new_quality_score"] = "RED", ["message_template_id"] = 990000000000 + Random.Shared.Next(100000, 999999),
                ["message_template_name"] = name, ["message_template_language"] = "en_US",
            });
            var listed = (await client.Http.GetAsync("/api/whatsapp/templates", ct: ctx.Ct)).ExpectOk().Array().Any(t => t?["name"]?.ToString() == name);
            Expect.True(!listed, "A quality update for a template the client never had created an empty draft template.");
        }, needs: Signed, finding: "t10-06")
        .Test("template-approved-ingest", "WhatsApp events: after an APPROVED event (no reason) the earlier submission error is cleared", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "clear");
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/templates/events", new { clientId = client.ClientId, eventType = "message_template_status_update", wabaId = channel.WabaId, name, language = "en_US", status = "APPROVED" }, ct: ctx.Ct)).ExpectOk();
            var template = await TemplateByNameAsync(ctx, client, name);
            Expect.EqualText("approved", template["status"], "Status");
            var reason = template["rejectionReason"]?.ToString();
            Expect.True(string.IsNullOrEmpty(reason), $"The approved template still shows the old submission error: \"{(reason?.Length > 80 ? reason[..80] + "…" : reason)}\".");
        }, needs: Events, finding: "t8-12")
        .Test("template-event-waba-mismatch", "WhatsApp events: a template event with another WABA id is refused", async ctx =>
        {
            var (client, channel, name) = await TemplateAsync(ctx, "waba");
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/templates/events", new { clientId = client.ClientId, eventType = "message_template_status_update", wabaId = channel.WabaId, name, language = "en_US", status = "APPROVED" }, ct: ctx.Ct)).ExpectOk();
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/templates/events", new { clientId = client.ClientId, eventType = "message_template_status_update", wabaId = "someone-elses-waba", name, language = "en_US", status = "REJECTED" }, ct: ctx.Ct))
                .ExpectClientError("A template event with another client's WABA id");
            Expect.True((await TemplateByNameAsync(ctx, client, name))["status"]?.ToString() == "approved", "The refused event still changed the template's status.");
        }, needs: Events)
        .Test("health-initial", "WhatsApp health: a connected fake number reports connected", async ctx =>
        {
            var (client, _) = await HealthClientAsync(ctx);
            var health = await WhatsAppHelpers.HealthAsync(ctx, client);
            Expect.True(health["connected"]?.GetValue<bool>() == true, $"Health: {health.ToJsonString()}.");
        }, needs: Needs.Session | Needs.Database)
        .Test("health-flagged", "WhatsApp health: a FLAGGED phone (webhook) is a warning", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            await WebhookEventAsync(ctx, channel, "phone_number_quality_update", new JsonObject { ["display_phone_number"] = "15550000000", ["event"] = "FLAGGED", ["current_limit"] = "TIER_1K" });
            Expect.EqualText("warning", (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"], "Health level");
        }, needs: Signed)
        .Test("health-review-rejected", "WhatsApp health: a rejected account review is a problem with one notification", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            var before = await WhatsAppHelpers.UnhealthyNotificationsAsync(ctx, client);
            await WebhookEventAsync(ctx, channel, "account_review_update", new JsonObject { ["decision"] = "REJECTED" });
            await WebhookEventAsync(ctx, channel, "account_review_update", new JsonObject { ["decision"] = "REJECTED" });
            Expect.EqualText("problem", (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"], "Health level");
            Expect.Equal(before + 1, await WhatsAppHelpers.UnhealthyNotificationsAsync(ctx, client), "INTEGRATION_UNHEALTHY notifications after two rejections");
        }, needs: Signed)
        .Test("health-ban", "WhatsApp health: a banned account (account_update DISABLED_UPDATE) is a problem, not \"All good\"", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            await WebhookEventAsync(ctx, channel, "account_update", new JsonObject { ["event"] = "DISABLED_UPDATE", ["ban_info"] = new JsonObject { ["waba_ban_state"] = "DISABLE", ["waba_ban_date"] = "October 30, 2026" } });
            var level = (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"]?.ToString();
            Expect.True(level == "problem", $"After Meta banned the account the health level is \"{level}\".");
        }, needs: Signed, finding: "t10-02")
        .Test("health-restriction", "WhatsApp health: a messaging restriction (account_update ACCOUNT_RESTRICTION) isn't healthy", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            await WebhookEventAsync(ctx, channel, "account_update", new JsonObject
            {
                ["event"] = "ACCOUNT_RESTRICTION",
                ["restriction_info"] = new JsonArray(new JsonObject { ["restriction_type"] = "RESTRICTED_BIZ_INITIATED_MESSAGING", ["expiration"] = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }),
            });
            var level = (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"]?.ToString();
            Expect.True(level is "problem" or "warning", $"After Meta restricted business-initiated messaging the health level is \"{level}\".");
        }, needs: Signed, finding: "t10-02")
        .Test("health-reinstated", "WhatsApp health: a reinstated account (DISABLED_UPDATE REINSTATE) is healthy again with no old problem", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            await WebhookEventAsync(ctx, channel, "account_update", new JsonObject { ["event"] = "DISABLED_UPDATE", ["ban_info"] = new JsonObject { ["waba_ban_state"] = "DISABLE" } });
            await WebhookEventAsync(ctx, channel, "account_update", new JsonObject { ["event"] = "VERIFIED_ACCOUNT" });
            var stillBanned = (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"]?.ToString();
            await WebhookEventAsync(ctx, channel, "account_update", new JsonObject { ["event"] = "DISABLED_UPDATE", ["ban_info"] = new JsonObject { ["waba_ban_state"] = "REINSTATE" } });
            var health = await WhatsAppHelpers.HealthAsync(ctx, client);
            Expect.True(stillBanned == "problem" && health["healthLevel"]?.ToString() == "healthy" && health["lastProblemMessage"] is null,
                $"Ban then an unrelated account event then reinstate: \"{stillBanned}\" then {health.ToJsonString()}.");
        }, needs: Signed, finding: "t10-02")
        .Test("health-name-rejected", "WhatsApp health: a rejected display name is at least a warning", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            await WebhookEventAsync(ctx, channel, "phone_number_name_update", new JsonObject
            {
                ["display_phone_number"] = "15550000000", ["decision"] = "REJECTED", ["requested_verified_name"] = "Auto Name", ["rejection_reason"] = "NAME_FORMAT_UNACCEPTABLE",
            });
            var level = (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"]?.ToString();
            Expect.True(level is "problem" or "warning", $"After Meta rejected the display name the health level is \"{level}\".");
        }, needs: Signed, finding: "t10-02")
        .Test("health-red", "WhatsApp health (n8n events): RED quality is a problem and notifies once", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            var before = await WhatsAppHelpers.UnhealthyNotificationsAsync(ctx, client);
            (await HealthEventAsync(ctx, client, channel, "phone_number_quality_update", quality: "YELLOW")).ExpectOk();
            Expect.EqualText("warning", (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"], "Level after YELLOW");
            (await HealthEventAsync(ctx, client, channel, "phone_number_quality_update", quality: "RED")).ExpectOk();
            (await HealthEventAsync(ctx, client, channel, "phone_number_quality_update", quality: "RED")).ExpectOk();
            Expect.EqualText("problem", (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"], "Level after RED");
            Expect.Equal(before + 1, await WhatsAppHelpers.UnhealthyNotificationsAsync(ctx, client), "INTEGRATION_UNHEALTHY notifications after RED twice");
        }, needs: Events)
        .Test("health-banned-phone", "WhatsApp health (n8n events): a BANNED phone number is a problem", async ctx =>
        {
            var (client, channel) = await HealthClientAsync(ctx);
            (await HealthEventAsync(ctx, client, channel, "phone_number_quality_update", status: "BANNED", quality: "GREEN")).ExpectOk();
            var level = (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"]?.ToString();
            Expect.True(level == "problem", $"A BANNED phone number has health level \"{level}\" (sending is blocked).");
        }, needs: Events, finding: "t10-02")
        .Test("health-wrong-number", "WhatsApp health (n8n events): an event for another phone number id is refused (422)", async ctx =>
        {
            var (client, _) = await HealthClientAsync(ctx);
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/health/events", new { clientId = client.ClientId, phoneNumberId = "1234567890", eventType = "phone_number_quality_update", qualityRating = "RED" }, ct: ctx.Ct))
                .ExpectStatus(422);
        }, needs: Events)
        .Build();
}

