using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Connecting, sending, health after failures, disconnecting and campaigns (t8-01, t8-10, t8-11, t8-18).</summary>
[Suite(55)]
public static class WhatsAppSendingSuite
{
    private const Needs Wa = Needs.Session | Needs.IngestKey | Needs.Database;

    /// <summary>A WhatsApp conversation on <paramref name="label"/>'s fake number with the window open (the customer wrote).</summary>
    private static async Task<(TestClient Client, FakeChannel Channel, (Guid ContactId, Guid ConversationId) Thread)> OpenThreadAsync(TestContext ctx, string label, bool withPhone = true)
    {
        var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, label);
        var thread = await WhatsAppHelpers.IngestThreadAsync(ctx, client, "send", withPhone);
        (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "hello"), ("externalMessageId", ctx.Webhooks.NewWamid()))).ExpectOk();
        return (client, channel, thread);
    }

    public static TestSuite Build() => new SuiteBuilder("wa-sending", "WhatsApp: connecting, sending and disconnecting",
            "Member sends on a fake WhatsApp number (every send fails at Meta), the unverified connect PUT (t8-01), what disconnecting " +
            "does (t8-18), health after failed sends (t8-10) and a campaign with an unusable template (t8-11).")
        .Test("connect-unverified", "WhatsApp connect: saving a made-up token and phone number id doesn't mark WhatsApp connected", async ctx =>
        {
            var client = await ctx.NamedClientAsync("wa-put");
            var phone = $"auto-{ctx.Tag}-put-{Random.Shared.Next(100000, 999999)}";
            var response = await client.Http.PutAsync("/api/channel-integrations", new
            {
                channel = "whatsapp", displayName = "Fake", phoneNumberId = phone, whatsAppBusinessId = $"auto-waba-{ctx.Tag}-put", accessToken = "made-up-token",
            }, ctx.Ct);
            var status = await ctx.Db.ScalarAsync<string>("select status from channels.channel_integrations where client_id = @clientId and channel = 'whatsapp'", new { clientId = client.ClientId }, ctx.Ct);
            Expect.True(status != "connected",
                $"PUT /api/channel-integrations answered {response.Status} and WhatsApp is now \"{status}\" with a token nobody checked; the caller also owns phone number id {phone} from now on.");
            Expect.True(response.Status is 400 or 422 or 502, $"PUT with a made-up token answered {response.Status}; expected a refusal.");
        }, needs: Needs.Session | Needs.Database, risk: Risk.External, finding: "t8-01",
            description: "Sends a made-up token to Meta's Graph API, which refuses it (nothing is connected).")
        .Test("send-fails-cleanly", "WhatsApp send: a reply Meta refuses answers 502, saves no message and releases the usage", async ctx =>
        {
            var (client, _, thread) = await OpenThreadAsync(ctx, "wa-send");
            (await client.Http.PostAsync($"/api/conversations/{thread.ConversationId}/messages/send", new { content = "member reply" }, ct: ctx.Ct)).ExpectStatus(502);
            Expect.Equal(0, (await InboxHelpers.MessagesAsync(ctx, thread.ConversationId)).Count(m => InboxHelpers.S(m, "direction") == "outbound"), "Outbound messages saved");
            var usage = await ctx.Db.QueryAsync("select charge_status from billing.usage_records where client_id = @clientId and conversation_id = @conversationId",
                new { clientId = client.ClientId, conversationId = thread.ConversationId }, ctx.Ct);
            Expect.True(usage.All(u => InboxHelpers.S(u, "charge_status") is not ("settled" or "reserved")), $"Usage left: {string.Join(", ", usage.Select(u => InboxHelpers.S(u, "charge_status")))}.");
        }, needs: Wa)
        .Test("send-window-closed", "WhatsApp send: free text outside the 24-hour window is refused (409)", async ctx =>
        {
            var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-send");
            var thread = await WhatsAppHelpers.IngestThreadAsync(ctx, client, "closed");
            (await client.Http.PostAsync($"/api/conversations/{thread.ConversationId}/messages/send", new { content = "hello?" }, ct: ctx.Ct)).ExpectStatus(409);
        }, needs: Wa)
        .Test("send-window-expired", "WhatsApp send: once the window has expired, free text is refused (409)", async ctx =>
        {
            var (client, _, thread) = await OpenThreadAsync(ctx, "wa-send");
            await ctx.Db.ExecuteForClientAsync(client.ClientId, """
                update crm.conversations set service_window_expires_at = now() - interval '1 minute', last_customer_message_at = now() - interval '25 hours'
                where client_id = @clientId and id = @id
                """, new { id = thread.ConversationId }, ctx.Ct);
            (await client.Http.PostAsync($"/api/conversations/{thread.ConversationId}/messages/send", new { content = "too late" }, ct: ctx.Ct)).ExpectStatus(409);
        }, needs: Wa)
        .Test("send-no-phone", "WhatsApp send: a contact without a phone number is refused (422)", async ctx =>
        {
            var (client, _, thread) = await OpenThreadAsync(ctx, "wa-send", withPhone: false);
            (await client.Http.PostAsync($"/api/conversations/{thread.ConversationId}/messages/send", new { content = "hi" }, ct: ctx.Ct)).ExpectStatus(422);
        }, needs: Wa)
        .Test("health-after-failures", "WhatsApp health: repeated token errors on sends make the number unhealthy", async ctx =>
        {
            var (client, _, thread) = await OpenThreadAsync(ctx, "wa-sendfail");
            for (var i = 0; i < 3; i++)
            {
                (await client.Http.PostAsync($"/api/conversations/{thread.ConversationId}/messages/send", new { content = $"attempt {i}" }, ct: ctx.Ct)).ExpectStatus(502);
            }
            var level = (await WhatsAppHelpers.HealthAsync(ctx, client))["healthLevel"]?.ToString();
            Expect.True(level == "problem", $"After three sends Meta refused with an invalid token the health level is \"{level}\"; the owner is never told to reconnect.");
        }, needs: Wa, finding: "t8-10")
        .Test("disconnect", "WhatsApp disconnect: the connection is disconnected and its token cleared; WhatsApp pages redirect", async ctx =>
        {
            var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-disc");
            (await client.Http.GetAsync("/WhatsApp/Templates", ct: ctx.Ct)).ExpectOk();
            (await client.Http.PostAsync("/api/channel-integrations/whatsapp/disconnect", ct: ctx.Ct)).ExpectStatus(204);
            var row = (await ctx.Db.QueryAsync("select status, access_token is null as no_token from channels.channel_integrations where client_id = @clientId and channel = 'whatsapp'",
                new { clientId = client.ClientId }, ctx.Ct)).Single();
            Expect.Equal("disconnected", InboxHelpers.S(row, "status"), "Status");
            Expect.Equal("True", InboxHelpers.S(row, "no_token"), "Token cleared");
            var page = await client.Http.GetAsync("/WhatsApp/Templates", ct: ctx.Ct);
            Expect.True(page.Status == 302 && page.Location?.Contains("not-connected", StringComparison.OrdinalIgnoreCase) == true,
                $"/WhatsApp/Templates after disconnect answered {page.Status} ({page.Location}).");
        }, needs: Needs.Session | Needs.Database)
        .Test("send-after-disconnect", "WhatsApp disconnect: sending afterwards is refused as a client error (409/422), not a provider failure (502)", async ctx =>
        {
            var (client, _, thread) = await OpenThreadAsync(ctx, "wa-disc2");
            (await client.Http.PostAsync("/api/channel-integrations/whatsapp/disconnect", ct: ctx.Ct)).ExpectStatus(204);
            var response = await client.Http.PostAsync($"/api/conversations/{thread.ConversationId}/messages/send", new { content = "after disconnect" }, ct: ctx.Ct);
            Expect.True(response.Status is 409 or 422, $"Sending with WhatsApp disconnected answered {response.Status} (a 502 says Meta failed, and is logged as an error).{response.Snippet(200)}");
        }, needs: Wa, finding: "t8-18")
        .Test("health-after-disconnect", "WhatsApp disconnect: health shows disconnected right away", async ctx =>
        {
            var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-disc3");
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/health/events", new { clientId = client.ClientId, phoneNumberId = channel.PhoneNumberId, eventType = "phone_number_quality_update", qualityRating = "GREEN" }, ct: ctx.Ct)).ExpectOk();
            (await client.Http.PostAsync("/api/channel-integrations/whatsapp/disconnect", ct: ctx.Ct)).ExpectStatus(204);
            var health = await WhatsAppHelpers.HealthAsync(ctx, client);
            Expect.True(health["healthLevel"]?.ToString() == "disconnected",
                $"Right after disconnecting, health says connected={health["connected"]} with level \"{health["healthLevel"]}\" (it's only recomputed on the next Meta event).");
        }, needs: Wa, finding: "t8-18")
        .Test("campaign-rejected-template", "Campaigns: a campaign can't be created with a template Meta hasn't approved", async ctx =>
        {
            var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-camp");
            await ctx.SetPlanAsync(client, "growth");
            var template = (await client.Http.PostAsync("/api/whatsapp/templates", new { name = $"auto_{ctx.Tag}_camp".ToLowerInvariant(), category = "marketing", language = "en_US", body = "Hi {{1}}, 20% off!" }, ct: ctx.Ct)).ExpectStatus(201);
            var contactId = await InboxHelpers.NewContactAsync(ctx, client, "camp", phone: "+" + ctx.Webhooks.NewWaId());
            var response = await client.Http.PostAsync("/api/campaigns", new { name = $"Auto campaign {ctx.Tag}", whatsAppTemplateId = template.Id(), contactIds = new[] { contactId } }, ct: ctx.Ct);
            Expect.True(!response.IsSuccess && response.Status < 500,
                $"A campaign was created ({response.Status}) with a template in status \"{template.Str("status")}\": sending it fails every recipient and ends \"completed\".");
        }, needs: Wa | Needs.AdminKey, risk: Risk.Billing, finding: "t8-11")
        .Test("campaign-token-pause", "Campaigns: an invalid WhatsApp token pauses the campaign once with the reason instead of failing everyone", async ctx =>
        {
            // The fake WhatsApp number's token is refused by Meta (401, code 190): a problem for every recipient, not one.
            var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-camp-tok");
            await ctx.SetPlanAsync(client, "growth");
            var template = await OpsHelpers.TemplateAsync(ctx, client);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template,
                await OpsHelpers.ContactAsync(ctx, client, "tok1"), await OpsHelpers.ContactAsync(ctx, client, "tok2"));
            var since = await AiHelpers.DbNowAsync(ctx);

            var send = await client.Http.PostAsync($"/api/campaigns/{id}/send", ct: ctx.Ct);
            Expect.True(send.Status < 500, $"Sending answered {send.Status}.{send.Snippet(200)}");
            var details = await OpsHelpers.CampaignAsync(ctx, client, id);
            var recipients = details["recipients"]!.AsArray();
            if (recipients.Any(r => (r?["failureReason"]?.ToString() ?? "").Contains("priced", StringComparison.OrdinalIgnoreCase)))
            {
                throw new TestSkipped("Template sends aren't priced for this client, so nothing reached Meta.");
            }
            var campaign = details["campaign"]!;
            var status = campaign["status"]?.ToString();
            var reason = campaign["pausedReason"]?.ToString() ?? "";
            var failed = details["stats"]!["failed"]!.GetValue<int>();
            Expect.True(status == "paused" && reason.Length > 0 && !reason.Contains("{\"error\"") && failed == 0,
                $"After Meta refused the token the campaign is \"{status}\" with {failed} failed recipients and the reason \"{reason}\"; it should stop once, with a readable reason, and keep everyone queued.");
            var notices = await ctx.Db.ScalarAsync<long>(
                "select count(*) from activity.notifications where client_id = @clientId and type = 'OUTBOUND_MESSAGE_FAILED' and created_at >= @since",
                new { clientId = client.ClientId, since }, ctx.Ct);
            Expect.Equal(1L, notices, "Notifications for the stopped campaign (one, not one per recipient)");
            await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct);
        }, needs: Wa | Needs.AdminKey, risk: Risk.Billing | Risk.External, finding: "t8-11")
        .Test("campaign-retry-failed", "Campaigns: Retry failed is refused for a draft and for an unknown campaign", async ctx =>
        {
            var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-camp-retry");
            await ctx.SetPlanAsync(client, "growth");
            var template = await OpsHelpers.TemplateAsync(ctx, client);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template, await OpsHelpers.ContactAsync(ctx, client, "retry"));
            (await client.Http.PostAsync($"/api/campaigns/{id}/retry-failed", ct: ctx.Ct)).ExpectStatus(422);
            (await client.Http.PostAsync($"/api/campaigns/{Guid.NewGuid()}/retry-failed", ct: ctx.Ct)).ExpectStatus(404);
            await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct);
        }, needs: Wa | Needs.AdminKey, risk: Risk.Billing, finding: "t8-11")
        .Build();
}
