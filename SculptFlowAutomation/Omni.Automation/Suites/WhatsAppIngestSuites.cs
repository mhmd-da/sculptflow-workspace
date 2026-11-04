using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>The n8n ingest path on WhatsApp conversations (t8-03, -05, -06, -07, -08).</summary>
[Suite(50)]
public static class WhatsAppIngestSuite
{
    private const Needs Wa = Needs.Session | Needs.IngestKey | Needs.Database;

    private static async Task<(TestClient Client, (Guid ContactId, Guid ConversationId) Thread)> ThreadAsync(TestContext ctx, string label, bool withPhone = true)
    {
        var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-ingest");
        return (client, await WhatsAppHelpers.IngestThreadAsync(ctx, client, label, withPhone));
    }

    private static string NewExternalId(TestContext ctx) => ctx.Webhooks.NewWamid();

    public static TestSuite Build()
    {
        var suite = new SuiteBuilder("wa-ingest", "WhatsApp: n8n ingest path",
                "POST /api/messages/ingest on WhatsApp conversations: the 24-hour window, message types, stop words, statuses, and the " +
                "trust and validation gaps QA found (t8).")
            .Test("window-opens", "WhatsApp ingest: a customer message opens the 24-hour window", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "window");
                Expect.True(!(await WhatsAppHelpers.ConversationAsync(ctx, client, thread.ConversationId))["isServiceWindowOpen"]!.GetValue<bool>(), "A brand-new conversation already has an open window.");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "hello"), ("externalMessageId", NewExternalId(ctx)))).ExpectOk();
                var conversation = await WhatsAppHelpers.ConversationAsync(ctx, client, thread.ConversationId);
                Expect.True(conversation["isServiceWindowOpen"]!.GetValue<bool>(), "The window isn't open after the customer wrote.");
                var hours = (DateTimeOffset.Parse(conversation["serviceWindowExpiresAt"]!.ToString()) - DateTimeOffset.UtcNow).TotalHours;
                Expect.True(hours is > 23 and < 25, $"The window closes in {hours:0.0} h, expected about 24 h.");
            }, needs: Wa)
            .Test("window-outbound", "WhatsApp ingest: AI messages and Business-app echoes never open the window", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "outbound");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "ai_message", ("content", "AI reply n8n sent"), ("externalMessageId", NewExternalId(ctx)), ("deliveryStatus", "sent"))).ExpectOk();
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "business_app_echo", ("content", "reply from the phone app"), ("externalMessageId", NewExternalId(ctx)))).ExpectOk();
                Expect.True(!(await WhatsAppHelpers.ConversationAsync(ctx, client, thread.ConversationId))["isServiceWindowOpen"]!.GetValue<bool>(), "An outbound message opened the 24-hour window.");
            }, needs: Wa)
            .Test("window-late", "WhatsApp ingest: a customer message from 30 hours ago doesn't reopen the window", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "late");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "sent 30 hours ago"), ("externalMessageId", NewExternalId(ctx)),
                    ("receivedAt", DateTimeOffset.UtcNow.AddHours(-30)))).ExpectOk();
                var conversation = await WhatsAppHelpers.ConversationAsync(ctx, client, thread.ConversationId);
                Expect.True(!conversation["isServiceWindowOpen"]!.GetValue<bool>(),
                    $"A 30-hour-old message opened the window until {conversation["serviceWindowExpiresAt"]}: it's computed from the server clock, so members can send free text Meta will refuse.");
            }, needs: Wa, finding: "t8-05")
            .Test("dedup", "WhatsApp ingest: the same wamid twice is stored once", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "dedup");
                var wamid = NewExternalId(ctx);
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "once"), ("externalMessageId", wamid))).ExpectOk();
                Expect.True((await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "once"), ("externalMessageId", wamid))).ExpectOk().Prop("deduplicated").GetValue<bool>(),
                    "The repeat wasn't reported as a duplicate.");
                Expect.Equal(1, (await InboxHelpers.MessagesAsync(ctx, thread.ConversationId)).Count, "Messages stored");
            }, needs: Wa)
            .Test("stop-text", "WhatsApp ingest: a typed STOP opts the contact out and START opts back in", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "stoptext");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "STOP"), ("externalMessageId", NewExternalId(ctx)))).ExpectOk();
                Expect.True(InboxHelpers.IsOptedOut(await InboxHelpers.ContactAsync(ctx, client, thread.ContactId)), "STOP didn't opt the contact out.");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "start"), ("externalMessageId", NewExternalId(ctx)))).ExpectOk();
                Expect.True(!InboxHelpers.IsOptedOut(await InboxHelpers.ContactAsync(ctx, client, thread.ContactId)), "start didn't opt the contact back in.");
            }, needs: Wa)
            .Test("stop-button", "WhatsApp ingest: tapping a template's STOP quick-reply button opts the contact out", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "stopbutton");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "STOP"), ("messageType", "button"), ("externalMessageId", NewExternalId(ctx)))).ExpectOk();
                Expect.True(InboxHelpers.IsOptedOut(await InboxHelpers.ContactAsync(ctx, client, thread.ContactId)),
                    "A STOP button reply (message type \"button\") didn't opt the contact out; only typed text and commands are checked for stop words.");
            }, needs: Wa, finding: "t8-08")
            .Test("stop-interactive", "WhatsApp ingest: an interactive STOP reply opts the contact out", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "stopinteractive");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "Stop"), ("messageType", "interactive"), ("externalMessageId", NewExternalId(ctx)))).ExpectOk();
                Expect.True(InboxHelpers.IsOptedOut(await InboxHelpers.ContactAsync(ctx, client, thread.ContactId)),
                    "An interactive \"Stop\" reply didn't opt the contact out.");
            }, needs: Wa, finding: "t8-08")
            .Test("contact-other-client", "WhatsApp ingest: another client's contactId is refused and that contact is untouched", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "xcontact");
                var other = await ctx.OtherClientAsync();
                var foreign = await InboxHelpers.NewContactAsync(ctx, other, "victim");
                var response = await WhatsAppHelpers.IngestAsync(ctx, client, (foreign, thread.ConversationId), "customer_message", ("content", "STOP"), ("externalMessageId", NewExternalId(ctx)));
                var victim = await InboxHelpers.ContactAsync(ctx, other, foreign);
                Expect.True(!response.IsSuccess && response.Status < 500 && !InboxHelpers.IsOptedOut(victim),
                    $"Ingest answered {response.Status} for a contactId that belongs to another client{(InboxHelpers.IsOptedOut(victim) ? ", and that client's contact was opted out" : "")}. The contact must be the conversation's own.");
            }, needs: Wa, finding: "t8-03")
            .Test("contact-mismatch", "WhatsApp ingest: a contactId that isn't the conversation's contact is refused", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "mismatch");
                var stranger = await InboxHelpers.NewContactAsync(ctx, client, "stranger");
                var response = await WhatsAppHelpers.IngestAsync(ctx, client, (stranger, thread.ConversationId), "customer_message", ("content", "hi"), ("externalMessageId", NewExternalId(ctx)));
                Expect.True(!response.IsSuccess && response.Status < 500, $"Ingest answered {response.Status}: a message was filed in one contact's conversation under another contact.");
            }, needs: Wa, finding: "t8-03")
            .Test("contact-unknown", "WhatsApp ingest: a contactId that doesn't exist is refused with 400/404, not 500", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "ghost");
                (await WhatsAppHelpers.IngestAsync(ctx, client, (Guid.NewGuid(), thread.ConversationId), "customer_message", ("content", "hi"), ("externalMessageId", NewExternalId(ctx))))
                    .ExpectClientError("Ingest with a contactId that doesn't exist");
            }, needs: Wa, finding: "t8-03")
            .Test("bad-input", "WhatsApp ingest: over-long or malformed fields get a 400, not a 500", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "badinput");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "x"), ("messageType", new string('x', 40)), ("externalMessageId", NewExternalId(ctx))))
                    .ExpectClientError("A 40-character messageType");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "x"), ("messageType", "image"), ("metadataJson", "{not json"), ("externalMessageId", NewExternalId(ctx))))
                    .ExpectClientError("metadataJson that isn't JSON");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "x"), ("externalMessageId", "wamid." + new string('z', 300))))
                    .ExpectClientError("A 306-character externalMessageId");
            }, needs: Wa, finding: "t8-06")
            .Test("channel-mismatch", "WhatsApp ingest: a message with another channel than the conversation's is refused", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "chanmismatch");
                var response = await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("channel", "telegram"), ("content", "wrong channel"), ("externalMessageId", NewExternalId(ctx)));
                Expect.True(!response.IsSuccess, $"A \"telegram\" message was saved inside a WhatsApp conversation ({response.Status}).");
            }, needs: Wa, finding: "t8-07")
            .Test("status-invalid", "WhatsApp ingest: a missing or unknown delivery status is refused and doesn't overwrite the real one", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "badstatus");
                var wamid = NewExternalId(ctx);
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "ai_message", ("content", "AI reply"), ("externalMessageId", wamid), ("deliveryStatus", "sent"))).ExpectOk();
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "status_update", ("externalMessageId", wamid), ("deliveryStatus", "read"))).ExpectOk();
                var missing = await WhatsAppHelpers.IngestAsync(ctx, client, thread, "status_update", ("externalMessageId", wamid));
                var banana = await WhatsAppHelpers.IngestAsync(ctx, client, thread, "status_update", ("externalMessageId", wamid), ("deliveryStatus", "banana"));
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.True(!missing.IsSuccess && !banana.IsSuccess && InboxHelpers.S(row!, "delivery_status") == "read",
                    $"No status answered {missing.Status}, \"banana\" answered {banana.Status}, and the stored status is now \"{InboxHelpers.S(row!, "delivery_status") ?? "null"}\" (should stay \"read\").");
            }, needs: Wa, finding: "t8-07")
            .Test("status-on-inbound", "WhatsApp ingest: a delivery status for an inbound message is refused", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "inboundstatus");
                var wamid = NewExternalId(ctx);
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "from the customer"), ("externalMessageId", wamid))).ExpectOk();
                var response = await WhatsAppHelpers.IngestAsync(ctx, client, thread, "status_update", ("externalMessageId", wamid), ("deliveryStatus", "failed"), ("failureReason", "weird"));
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.True(InboxHelpers.S(row!, "delivery_status") is null,
                    $"A customer's inbound message was marked \"{InboxHelpers.S(row!, "delivery_status")}\" by a status update (answered {response.Status}); statuses apply to outbound messages only.");
            }, needs: Wa, finding: "t8-07")
            .Test("new-contact-flag", "WhatsApp ingest: a caller can't fake a NEW_CONTACT notification for an existing contact", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "newflag");
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", "hi"), ("externalMessageId", NewExternalId(ctx)), ("contactWasNewlyCreated", true))).ExpectOk();
                var notifications = await InboxHelpers.NotificationsAsync(ctx, client);
                Expect.True(!notifications.Any(n => n?["type"]?.ToString() == "NEW_CONTACT" && n["conversationId"]?.ToString() == thread.ConversationId.ToString()),
                    "contactWasNewlyCreated from the request body created a NEW_CONTACT notification for a contact that already existed.");
            }, needs: Wa, finding: "t8-07")
            .Test("status-progression", "WhatsApp ingest: statuses never move backwards (a late 'delivered' after 'read')", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "progress");
                var wamid = NewExternalId(ctx);
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "ai_message", ("content", "AI reply"), ("externalMessageId", wamid), ("deliveryStatus", "sent"))).ExpectOk();
                foreach (var status in new[] { "delivered", "read", "delivered", "read" })
                {
                    (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "status_update", ("externalMessageId", wamid), ("deliveryStatus", status))).ExpectOk();
                }
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.Equal("read", InboxHelpers.S(row!, "delivery_status"), "Status after a late 'delivered'");
                Expect.True(InboxHelpers.S(row!, "delivered") == "True" && InboxHelpers.S(row!, "read") == "True", "delivered_at and read_at should both be set.");
            }, needs: Wa)
            .Test("status-failed", "WhatsApp ingest: a failed status records the code and notifies once, even when repeated", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "failed");
                var wamid = NewExternalId(ctx);
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "ai_message", ("content", "AI reply"), ("externalMessageId", wamid), ("deliveryStatus", "sent"))).ExpectOk();
                for (var i = 0; i < 2; i++)
                {
                    (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "status_update", ("externalMessageId", wamid), ("deliveryStatus", "failed"),
                        ("failureCode", "131047"), ("failureReason", "Re-engagement message"))).ExpectOk();
                }
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.Equal("failed", InboxHelpers.S(row!, "delivery_status"), "Status");
                Expect.Equal("131047", InboxHelpers.S(row!, "failure_code"), "Failure code");
                var failures = (await InboxHelpers.NotificationsAsync(ctx, client)).Count(n => n?["type"]?.ToString() == "OUTBOUND_MESSAGE_FAILED" && n["conversationId"]?.ToString() == thread.ConversationId.ToString());
                Expect.Equal(1, failures, "OUTBOUND_MESSAGE_FAILED notifications for one failed message reported twice");
            }, needs: Wa)
            .Test("events-no-key", "WhatsApp events: template and health events without the ingest key get 401 before body validation", async ctx =>
            {
                var template = await ctx.Anonymous.PostAsync("/api/integrations/whatsapp/templates/events", new { }, ct: ctx.Ct);
                var health = await ctx.Anonymous.PostAsync("/api/integrations/whatsapp/health/events", new { }, ct: ctx.Ct);
                Expect.True(template.Status == 401 && health.Status == 401,
                    $"Empty bodies without the key answered {template.Status} (templates) and {health.Status} (health); both should be 401 before any validation.");
            }, needs: Needs.None, finding: "t3-inbox-18")
            .Test("template-event-unknown-client", "WhatsApp events: a template event for an unknown client is refused, not a 500", async ctx =>
            {
                (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/templates/events", new { clientId = Guid.NewGuid(), eventType = "message_template_status_update", name = "auto_ghost", language = "en_US", status = "APPROVED" }, ct: ctx.Ct))
                    .ExpectClientError("A template event for an unknown client");
            }, needs: Needs.IngestKey, finding: "t8-06")
            .Test("health-event-bad-json", "WhatsApp events: a health event with invalid rawMetadataJson is refused, not a 500", async ctx =>
            {
                var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-ingest");
                (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/health/events", new { clientId = client.ClientId, phoneNumberId = channel.PhoneNumberId, eventType = "phone_number_quality_update", qualityRating = "GREEN", rawMetadataJson = "{bad" }, ct: ctx.Ct))
                    .ExpectClientError("A health event with rawMetadataJson \"{bad\"");
            }, needs: Wa, finding: "t8-06");

        foreach (var type in new[] { "image", "document", "audio", "video", "location", "contact", "reaction", "sticker", "interactive", "button", "unsupported" })
        {
            suite.Test("type-" + type, $"WhatsApp ingest: a \"{type}\" message is stored with its type and metadata", async ctx =>
            {
                var (client, thread) = await ThreadAsync(ctx, "type" + type);
                var wamid = NewExternalId(ctx);
                (await WhatsAppHelpers.IngestAsync(ctx, client, thread, "customer_message", ("content", type == "interactive" ? "Yes" : null), ("messageType", type),
                    ("metadataJson", "{\"media_id\":\"auto-media\",\"caption\":\"cap\"}"), ("externalMessageId", wamid))).ExpectOk();
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.Equal(type, InboxHelpers.S(row!, "message_type"), "Stored type");
                Expect.Equal("inbound", InboxHelpers.S(row!, "direction"), "Direction");
            }, needs: Wa);
        }

        return suite.Build();
    }
}

