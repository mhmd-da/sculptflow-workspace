using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Signed Meta deliveries carrying every inbound message type, Business-app echoes (both shapes) and delivery
/// statuses, on the same fake number as the webhook suite.</summary>
[Suite(56)]
public static class WhatsAppWebhookMessagesSuite
{
    private const Needs Signed = Needs.Session | Needs.Database | Needs.MetaSecret;

    private static Task<(TestClient Client, FakeChannel Channel)> HookAsync(TestContext ctx) => WhatsAppHelpers.WhatsAppAsync(ctx, "wa-hook");


    /// <summary>Message types in Meta's shapes and what the Inbox keeps as text.</summary>
    private static readonly (string Id, string Type, string Body, string? Content)[] Types =
    {
        ("image", "image", """{"caption":"Is this the right product?","mime_type":"image/jpeg","sha256":"abc","id":"1003001001"}""", "Is this the right product?"),
        ("audio", "audio", """{"mime_type":"audio/ogg; codecs=opus","sha256":"abc","id":"1003001002","voice":true}""", "[audio]"),
        ("video", "video", """{"mime_type":"video/mp4","sha256":"abc","id":"1003001003"}""", "[video]"),
        ("document", "document", """{"caption":"my prescription","filename":"auto.pdf","mime_type":"application/pdf","sha256":"abc","id":"1003001004"}""", "my prescription"),
        ("sticker", "sticker", """{"mime_type":"image/webp","sha256":"abc","id":"1003001005","animated":false}""", "[sticker]"),
        ("location", "location", """{"latitude":33.98,"longitude":35.64,"name":"Jounieh port","address":"Jounieh"}""", "Jounieh port"),
        ("location-bare", "location", """{"latitude":33.98,"longitude":35.64}""", "[location]"),
        ("contacts", "contacts", """[{"name":{"formatted_name":"Shared Sami","first_name":"Sami"},"phones":[{"phone":"+15550109998","wa_id":"15550109998","type":"MOBILE"}]}]""", "[contact]"),
        ("reaction", "reaction", """{"message_id":"wamid.AUTOsome","emoji":"👍"}""", "👍"),
        ("button", "button", """{"payload":"more_info","text":"Tell me more"}""", "Tell me more"),
        ("button-reply", "interactive", """{"type":"button_reply","button_reply":{"id":"yes","title":"Yes, confirm"}}""", "Yes, confirm"),
        ("list-reply", "interactive", """{"type":"list_reply","list_reply":{"id":"mon10","title":"Monday 10:00 AM"}}""", "Monday 10:00 AM"),
    };

    public static TestSuite Build()
    {
        var suite = new SuiteBuilder("wa-webhook-messages", "WhatsApp: message types, echoes and statuses",
                "Every inbound Meta message type as the Inbox stores it, Business-app echoes in the parser's and in Meta's documented " +
                "shape (t10-04), and delivery statuses that never move backwards.")
            .Test("echo", "WhatsApp webhook: a Business-app echo is stored as the member's outbound message", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "hello")).ExpectOk();
                var wamid = ctx.Webhooks.NewWamid();
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.ParserEcho(ctx, channel, waId, "reply from the phone app", wamid)));
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid) ?? throw new TestFailure("The echo wasn't stored.");
                Expect.Equal("outbound", InboxHelpers.S(row, "direction"), "Direction");
                Expect.Equal("member", InboxHelpers.S(row, "sender_type"), "Sender");
                Expect.Equal(InboxHelpers.S(await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId), "conversation_id"), InboxHelpers.S(row, "conversation_id"), "Conversation of the echo");
            }, needs: Signed)
            .Test("echo-meta-shape", "WhatsApp webhook: an echo in Meta's documented shape (message_echoes[]) is stored", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "hello")).ExpectOk();
                var wamid = ctx.Webhooks.NewWamid();
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Change("smb_message_echoes", new JsonObject
                {
                    ["messaging_product"] = "whatsapp",
                    ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = channel.PhoneNumberId },
                    ["message_echoes"] = new JsonArray(new JsonObject
                    {
                        ["from"] = "15550000000", ["to"] = waId, ["id"] = wamid, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                        ["type"] = "text", ["text"] = new JsonObject { ["body"] = "member reply (Meta shape)" },
                    }),
                })));
                Expect.True(await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid) is not null,
                    "An echo in Meta's documented shape was dropped: a member replying from the WhatsApp Business app is invisible and the AI keeps answering.");
            }, needs: Signed, finding: "t10-04")
            .Test("echo-no-contacts", "WhatsApp webhook: an echo without contacts[] never creates a contact for the business's own number", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var echo = WhatsAppHelpers.ParserEcho(ctx, channel, waId, "echo without contacts", ctx.Webhooks.NewWamid());
                echo["value"]!.AsObject().Remove("contacts");
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, echo));
                Expect.True(await WhatsAppHelpers.ByWaIdAsync(ctx, client.ClientId, "15550000000") is null,
                    "The echo's sender (the business's own number) was saved as a customer contact.");
            }, needs: Signed, finding: "t10-04")
            .Test("statuses", "WhatsApp webhook: statuses on an outbound message never go backwards; 'failed' records Meta's code", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "hello")).ExpectOk();
                var wamid = ctx.Webhooks.NewWamid();
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.ParserEcho(ctx, channel, waId, "outbound", wamid)));
                var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var (status, offset) in new[] { ("sent", 0), ("read", 20), ("delivered", 10), ("read", 20) })
                {
                    await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Status(channel, wamid, status, t0 + offset, waId)));
                }
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.Equal("read", InboxHelpers.S(row!, "delivery_status"), "Status after a late 'delivered'");
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Status(channel, wamid, "failed", t0 + 30, waId,
                    new JsonArray(new JsonObject { ["code"] = 131047, ["title"] = "Re-engagement message", ["message"] = "Re-engagement message" }))));
                row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
                Expect.Equal("failed", InboxHelpers.S(row!, "delivery_status"), "Status after 'failed'");
            }, needs: Signed);

        foreach (var type in Types)
        {
            suite.Test("type-" + type.Id, $"WhatsApp webhook: a {type.Id} message is stored as \"{type.Type}\" with readable text", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var wamid = ctx.Webhooks.NewWamid();
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waId, "Media Maya",
                    WhatsAppHelpers.Message(ctx, waId, type.Type, JsonNode.Parse(type.Body), wamid))));
                var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid) ?? throw new TestFailure("The message wasn't stored.");
                Expect.Equal(type.Type, InboxHelpers.S(row, "message_type"), "Stored type");
                Expect.Equal(type.Content, InboxHelpers.S(row, "content"), "Inbox text");
            }, needs: Signed);
        }

        return suite.Build();
    }
}
