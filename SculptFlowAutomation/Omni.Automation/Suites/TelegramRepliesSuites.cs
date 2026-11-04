using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Member replies on a fake Telegram bot (every send fails at Telegram because the bot doesn't exist) and the
/// message-media endpoint. Nothing can reach a real chat.</summary>
[Suite(48)]
public static class TelegramRepliesSuite
{
    private const Needs Tg = Needs.Session | Needs.Database;
    private const string Photo = """{"photo":[{"file_id":"AgACfake2","file_unique_id":"u2","width":90,"height":90}]}""";
    private const string Voice = """{"voice":{"file_id":"AwACfake","file_unique_id":"u4","duration":3,"mime_type":"audio/ogg"}}""";

    public static TestSuite Build() => new SuiteBuilder("inbox-telegram-replies", "Telegram: member replies and media (fake connection)",
            "A member's reply to a chat the fake bot can't reach fails cleanly (502, usage released, a notification), over-long " +
            "replies are refused first, and the media endpoint refuses what it can't play.")
            .Test("send-fails-cleanly", "Telegram reply: a send the bot can't deliver answers 502 and leaves no message behind", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "please reply");
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                var response = (await client.Http.PostAsync($"/api/conversations/{conversationId}/messages/send", new { content = "member reply" }, ct: ctx.Ct)).ExpectStatus(502);
                Expect.True(!string.IsNullOrWhiteSpace(response.Json?["error"]?.ToString()), $"The 502 carries no error text.{response.Snippet(200)}");
                var outbound = (await InboxHelpers.MessagesAsync(ctx, conversationId)).Count(m => InboxHelpers.S(m, "direction") == "outbound");
                Expect.Equal(0, outbound, "Outbound messages saved for a failed send");
            }, needs: Tg)
            .Test("send-releases-usage", "Telegram reply: a failed send releases its usage record (nothing charged)", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "please reply");
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                (await client.Http.PostAsync($"/api/conversations/{conversationId}/messages/send", new { content = "member reply" }, ct: ctx.Ct)).ExpectStatus(502);
                var usage = await ctx.Db.QueryAsync("select charge_status, release_reason from billing.usage_records where client_id = @clientId and conversation_id = @conversationId",
                    new { clientId = client.ClientId, conversationId }, ctx.Ct);
                Expect.True(usage.Count > 0, "The send left no usage record.");
                Expect.True(usage.All(u => InboxHelpers.S(u, "charge_status") is not ("settled" or "reserved")),
                    $"Usage after a failed send: {string.Join(", ", usage.Select(u => InboxHelpers.S(u, "charge_status")))} (nothing may stay reserved or be charged).");
                Expect.True(usage.Any(u => InboxHelpers.S(u, "release_reason") == "send_failed"), "The usage wasn't released as send_failed.");
            }, needs: Tg)
            .Test("send-notifies", "Telegram reply: a failed send creates an OUTBOUND_MESSAGE_FAILED notification", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "please reply");
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                (await client.Http.PostAsync($"/api/conversations/{conversationId}/messages/send", new { content = "member reply" }, ct: ctx.Ct)).ExpectStatus(502);
                var notifications = await InboxHelpers.NotificationsAsync(ctx, client);
                Expect.True(notifications.Any(n => n?["type"]?.ToString() == "OUTBOUND_MESSAGE_FAILED" && n["conversationId"]?.ToString() == conversationId.ToString()),
                    "No OUTBOUND_MESSAGE_FAILED notification for the conversation.");
            }, needs: Tg)
            .Test("send-over-4096", "Telegram reply: a message over 4,096 characters is refused before sending", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "please reply");
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                var response = await client.Http.PostAsync($"/api/conversations/{conversationId}/messages/send", new { content = new string('z', 4100) }, ct: ctx.Ct);
                Expect.True(!response.IsSuccess && response.Status != 500, $"A 4,100-character reply answered {response.Status}.");
                response.ExpectBodyContains("4096");
                Expect.Equal(0, (await InboxHelpers.MessagesAsync(ctx, conversationId)).Count(m => InboxHelpers.S(m, "direction") == "outbound"), "Outbound messages saved");
            }, needs: Tg)
            .Test("send-template", "Telegram reply: a WhatsApp template can't be sent in a Telegram conversation", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hi");
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                (await client.Http.PostAsync($"/api/conversations/{conversationId}/messages/send-template", new { whatsAppTemplateId = Guid.NewGuid(), bodyParameters = Array.Empty<string>() }, ct: ctx.Ct))
                    .ExpectClientError("Sending a WhatsApp template in a Telegram conversation");
            }, needs: Tg)
            .Test("media-not-audio", "Media: asking for the audio of a photo message is refused (400)", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                (await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(chat, null, (JsonObject)JsonNode.Parse(Photo)!))).ExpectOk();
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                (await client.Http.GetAsync($"/api/messages/{message["id"]}/media", ct: ctx.Ct)).ExpectStatus(400);
            }, needs: Tg)
            .Test("media-voice-gone", "Media: a voice note Telegram can't serve is refused cleanly (no 500)", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                (await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(chat, null, (JsonObject)JsonNode.Parse(Voice)!))).ExpectOk();
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                var response = await client.Http.GetAsync($"/api/messages/{message["id"]}/media", ct: ctx.Ct);
                Expect.True(response.Status is 404 or 502, $"Playing a voice note whose file Telegram can't serve answered {response.Status} (expected 404 or 502).");
            }, needs: Tg)
            .Test("media-isolation", "Media: another client's message (or an unknown id) gives 404", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                (await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(chat, null, (JsonObject)JsonNode.Parse(Voice)!))).ExpectOk();
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                var other = (await ctx.OtherClientAsync()).Http;
                (await other.GetAsync($"/api/messages/{message["id"]}/media", ct: ctx.Ct)).ExpectStatus(404);
                (await client.Http.GetAsync($"/api/messages/{Guid.NewGuid()}/media", ct: ctx.Ct)).ExpectStatus(404);
            }, needs: Tg)
        .Build();
}
