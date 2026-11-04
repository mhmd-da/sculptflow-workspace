using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Inbound Telegram updates on a fake bot connection (t3-inbox-02, -05, -06, -16, -18).</summary>
[Suite(45)]
public static class TelegramWebhookSuite
{
    private const Needs Tg = Needs.Session | Needs.Database;

    private static string WebhookPath(FakeChannel channel) => $"/api/integrations/telegram/webhook/{channel.IntegrationId}";

    /// <summary>Telegram message bodies other than text: what the parser keeps as type and Inbox text.</summary>
    private static readonly (string Id, string Name, string Extra, string Type, string Content)[] MessageKinds =
    {
        ("photo-caption", "a photo with a caption keeps the caption", """{"photo":[{"file_id":"AgACfake1","file_unique_id":"u1","width":90,"height":90}],"caption":"photo caption"}""", "image", "photo caption"),
        ("photo", "a photo without caption shows [photo]", """{"photo":[{"file_id":"AgACfake2","file_unique_id":"u2","width":90,"height":90}]}""", "image", "[photo]"),
        ("sticker", "a sticker shows [sticker]", """{"sticker":{"file_id":"CAACfake","file_unique_id":"u3","type":"regular","width":512,"height":512,"is_animated":false,"is_video":false,"emoji":"😀"}}""", "sticker", "[sticker]"),
        ("voice", "a voice note shows [voice message]", """{"voice":{"file_id":"AwACfake","file_unique_id":"u4","duration":3,"mime_type":"audio/ogg"}}""", "audio", "[voice message]"),
        ("location", "a location shows [location]", """{"location":{"latitude":33.89,"longitude":35.5}}""", "location", "[location]"),
        ("contact", "a shared contact shows [contact]", """{"contact":{"phone_number":"+15550100999","first_name":"X"}}""", "contacts", "[contact]"),
        ("document", "a document shows [document]", """{"document":{"file_id":"BQACfake","file_unique_id":"u5","file_name":"a.pdf","mime_type":"application/pdf"}}""", "document", "[document]"),
        ("poll", "an unsupported message (poll) is kept as [unsupported message]", """{"poll":{"id":"1","question":"q","options":[]}}""", "unsupported", "[unsupported message]"),
    };

    /// <summary>Updates Omni ignores: answered 200 (so Telegram doesn't retry) and nothing is stored.</summary>
    private static readonly (string Id, string Name, Func<TestContext, long, JsonObject> Update)[] IgnoredKinds =
    {
        ("callback-query", "a callback_query is ignored", (ctx, chat) => new JsonObject
        {
            ["update_id"] = ctx.Webhooks.NewChatId(),
            ["callback_query"] = new JsonObject { ["id"] = "1", ["from"] = new JsonObject { ["id"] = chat, ["is_bot"] = false, ["first_name"] = "x" }, ["data"] = "x" },
        }),
        ("blocked", "a my_chat_member (bot blocked) update is ignored", (ctx, chat) => new JsonObject
        {
            ["update_id"] = ctx.Webhooks.NewChatId(),
            ["my_chat_member"] = new JsonObject
            {
                ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" }, ["from"] = new JsonObject { ["id"] = chat }, ["date"] = 1,
                ["old_chat_member"] = new JsonObject { ["status"] = "member" }, ["new_chat_member"] = new JsonObject { ["status"] = "kicked" },
            },
        }),
        ("group", "a group chat message is ignored", (ctx, chat) =>
        {
            var update = ctx.Webhooks.TelegramMessage(chat, "group hello");
            update["message"]!["chat"]!["type"] = "group";
            return update;
        }),
        ("bot-sender", "a message from another bot is ignored", (ctx, chat) =>
        {
            var update = ctx.Webhooks.TelegramMessage(chat, "from a bot");
            update["message"]!["from"]!["is_bot"] = true;
            return update;
        }),
        ("no-message-id", "a message without message_id is ignored", (ctx, chat) =>
        {
            var update = ctx.Webhooks.TelegramMessage(chat, "no id");
            update["message"]!.AsObject().Remove("message_id");
            return update;
        }),
    };

    public static TestSuite Build()
    {
        var suite = new SuiteBuilder("inbox-telegram", "Telegram: webhook (fake connection)",
                "Inbound Telegram updates on a fake bot connection: secret checks, every update type, duplicates, bursts and ordering.")
            .Test("secret", "Telegram: a missing, empty or wrong secret token is refused (403) and nothing is stored", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                var body = ctx.Webhooks.TelegramMessage(chat, "secret check").ToJsonString();
                (await ctx.Anonymous.PostRawAsync(WebhookPath(channel), body, "application/json", ct: ctx.Ct)).ExpectStatus(403);
                (await ctx.Webhooks.TelegramAsync(channel, JsonNode.Parse(body)!, secretOverride: "")).ExpectStatus(403);
                (await ctx.Webhooks.TelegramAsync(channel, JsonNode.Parse(body)!, secretOverride: "wrong-secret")).ExpectStatus(403);
                Expect.True(await InboxHelpers.TelegramThreadOrNullAsync(ctx, client.ClientId, chat) is null, "A refused delivery created a conversation.");
            }, needs: Tg)
            .Test("unknown-connection", "Telegram: an unknown connection id gives 404", async ctx =>
            {
                (await ctx.Anonymous.PostRawAsync($"/api/integrations/telegram/webhook/{Guid.NewGuid()}", "{}", "application/json",
                    new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = "anything" }, ctx.Ct)).ExpectStatus(404);
                (await ctx.Anonymous.PostRawAsync("/api/integrations/telegram/webhook/abc", "{}", "application/json", ct: ctx.Ct)).ExpectStatus(404);
            }, needs: Needs.None)
            .Test("get", "Telegram: GET on the webhook gives 405", async ctx =>
            {
                (await ctx.Anonymous.GetAsync($"/api/integrations/telegram/webhook/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(405);
            }, needs: Needs.None)
            .Test("text-plain", "Telegram: a text/plain body is refused (415)", async ctx =>
            {
                var (_, channel) = await InboxHelpers.TelegramAsync(ctx);
                (await ctx.Anonymous.PostRawAsync(WebhookPath(channel), ctx.Webhooks.TelegramMessage(ctx.Webhooks.NewChatId(), "x").ToJsonString(), "text/plain",
                    new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = channel.WebhookSecret! }, ctx.Ct)).ExpectStatus(415);
            }, needs: Tg)
            .Test("invalid-json-no-secret", "Telegram: invalid JSON without the secret gets 403 before any body validation", async ctx =>
            {
                var (_, channel) = await InboxHelpers.TelegramAsync(ctx);
                var response = await ctx.Anonymous.PostRawAsync(WebhookPath(channel), "{not json", "application/json", ct: ctx.Ct);
                Expect.True(response.Status == 403, $"Invalid JSON without the secret answered {response.Status}; the secret should be checked first (403).");
            }, needs: Tg, finding: "t3-inbox-18")
            .Test("disconnected", "Telegram: after the bot is disconnected its webhook answers 404", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx, "tg-disc");
                await InboxHelpers.TelegramTextAsync(ctx, channel, "before disconnect");
                (await client.Http.PostAsync("/api/channel-integrations/telegram/disconnect", ct: ctx.Ct)).ExpectStatus(204);
                (await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(ctx.Webhooks.NewChatId(), "after disconnect"))).ExpectStatus(404);
            }, needs: Tg)
            .Test("text", "Telegram: a first text creates the contact, a conversation with the team and the inbound message", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hello from a new chat");
                var thread = await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat);
                Expect.Equal($"Automation {chat.ToString()[^4..]}", InboxHelpers.S(thread, "full_name"), "Contact name from first/last name");
                Expect.Equal("telegram", InboxHelpers.S(thread, "source"), "Contact source");
                Expect.Equal("human", InboxHelpers.S(thread, "mode"), "Conversation mode on a plan without AI");
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(thread, "id"))).Single();
                Expect.Equal("inbound", InboxHelpers.S(message, "direction"), "Direction");
                Expect.Equal("text", InboxHelpers.S(message, "message_type"), "Type");
                Expect.Equal("hello from a new chat", InboxHelpers.S(message, "content"), "Content");
            }, needs: Tg)
            .Test("start-command", "Telegram: /start is stored as a command message", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "/start");
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                Expect.Equal("command", InboxHelpers.S(message, "message_type"), "Type of /start");
                Expect.Equal("/start", InboxHelpers.S(message, "content"), "Content");
            }, needs: Tg)
            .Test("text-unicode", "Telegram: Arabic, emoji and HTML are stored exactly as sent", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                const string text = "مرحبا كيف الحال 😀🎉 <b>bold</b> <script>x</script>";
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, text);
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                Expect.Equal(text, InboxHelpers.S(message, "content"), "Stored text");
            }, needs: Tg)
            .Test("text-5000", "Telegram: a 5,000-character message is stored whole", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var text = "long " + new string('x', 5000);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, text);
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                Expect.Equal(text.Length, InboxHelpers.S(message, "content")?.Length ?? 0, "Stored length");
            }, needs: Tg)
            .Test("sql-like-text", "Telegram: a customer message that looks like SQL reaches the app and is stored", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                const string text = "'; drop table x;--";
                var response = await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(chat, text));
                Expect.True(response.Status == 200, $"The delivery was answered {response.Status}{(response.Status == 403 ? " by the edge firewall (Cloudflare) before reaching the app, so Telegram retries a message that can never arrive" : "")}.");
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                Expect.Equal(text, InboxHelpers.S(message, "content"), "Stored text");
            }, needs: Tg, finding: "t3-inbox-06")
            .Test("edited-message", "Telegram: an edited_message is acknowledged and ignored (the original stays)", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "original text");
                var update = new JsonObject
                {
                    ["update_id"] = ctx.Webhooks.NewChatId(),
                    ["edited_message"] = new JsonObject
                    {
                        ["message_id"] = 1, ["from"] = new JsonObject { ["id"] = chat, ["is_bot"] = false, ["first_name"] = "x" },
                        ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" }, ["date"] = 1, ["edit_date"] = 2, ["text"] = "edited text",
                    },
                };
                (await ctx.Webhooks.TelegramAsync(channel, update)).ExpectOk();
                var messages = await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"));
                Expect.Equal(1, messages.Count, "Messages after an edit");
                Expect.Equal("original text", InboxHelpers.S(messages[0], "content"), "Stored text");
            }, needs: Tg)
            .Test("replay", "Telegram: the same update delivered twice is stored once", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                var update = ctx.Webhooks.TelegramMessage(chat, "dup test");
                (await ctx.Webhooks.TelegramAsync(channel, update)).ExpectOk();
                (await ctx.Webhooks.TelegramAsync(channel, update)).ExpectOk();
                var again = (JsonObject)update.DeepClone();
                again["update_id"] = update["update_id"]!.GetValue<long>() + 999;
                (await ctx.Webhooks.TelegramAsync(channel, again)).ExpectOk();
                Expect.Equal(1, (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Count,
                    "Messages after a replay and a new update_id for the same message");
            }, needs: Tg)
            .Test("concurrent-duplicates", "Telegram: five simultaneous copies of one update all get 200 and one row", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "first");
                var update = ctx.Webhooks.TelegramMessage(chat, "race dup");
                var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ctx.Webhooks.TelegramAsync(channel, update)));
                Expect.True(responses.All(r => r.Status == 200), $"Statuses: {string.Join(",", responses.Select(r => r.Status))}.");
                Expect.Equal(2, (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Count,
                    "Messages (the first text plus one copy)");
            }, needs: Tg)
            .Test("burst", "Telegram: a burst of 15 simultaneous messages from a new chat is accepted without 500s", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                var updates = Enumerable.Range(1, 15).Select(i => ctx.Webhooks.TelegramMessage(chat, $"burst {i}")).ToList();
                var responses = await Task.WhenAll(updates.Select(u => ctx.Webhooks.TelegramAsync(channel, u)));
                var failed = responses.Count(r => r.Status != 200);
                Expect.True(failed == 0, $"{failed} of 15 simultaneous deliveries failed ({string.Join(",", responses.Select(r => r.Status))}): the database pool runs out under a modest burst.");
                Expect.Equal(15, (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Count, "Messages stored");
            }, needs: Tg, risk: Risk.Shared, finding: "t3-inbox-02")
            .Test("order", "Telegram: a late-arriving earlier message is shown before the newer one", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var second = ctx.Webhooks.TelegramMessage(chat, "second");
                second["message"]!["message_id"] = 2;
                second["message"]!["date"] = now;
                var first = ctx.Webhooks.TelegramMessage(chat, "first");
                first["message"]!["message_id"] = 1;
                first["message"]!["date"] = now - 60;
                (await ctx.Webhooks.TelegramAsync(channel, second)).ExpectOk();
                (await ctx.Webhooks.TelegramAsync(channel, first)).ExpectOk();
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                var shown = (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Array("messages").Select(m => m?["content"]?.ToString()).ToList();
                Expect.True(shown.SequenceEqual(new[] { "first", "second" }), $"The thread shows [{string.Join(", ", shown)}]: messages are ordered by arrival at the server, not by when the customer sent them.");
            }, needs: Tg, finding: "t3-inbox-16")
            .Test("forged-id", "Telegram: a member-forged message can't block the customer's real message", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hi");
                var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
                await client.Http.PostAsync($"/api/conversations/{conversationId}/messages", new
                {
                    direction = "inbound", senderType = "contact", channel = "telegram", content = "FORGED", externalMessageId = $"{chat}:777",
                }, ct: ctx.Ct);
                var real = ctx.Webhooks.TelegramMessage(chat, "the real message");
                real["message"]!["message_id"] = 777;
                (await ctx.Webhooks.TelegramAsync(channel, real)).ExpectOk();
                var contents = (await InboxHelpers.MessagesAsync(ctx, conversationId)).Select(m => InboxHelpers.S(m, "content")).ToList();
                Expect.True(contents.Contains("the real message"), $"The customer's real message was dropped as a duplicate of a member-forged one. Thread: [{string.Join(", ", contents)}].");
                Expect.True(!contents.Contains("FORGED"), "A member-forged customer message is in the thread.");
            }, needs: Tg, finding: "t3-inbox-05");

        foreach (var kind in MessageKinds)
        {
            suite.Test("type-" + kind.Id, $"Telegram: {kind.Name}", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = ctx.Webhooks.NewChatId();
                (await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(chat, null, (JsonObject)JsonNode.Parse(kind.Extra)!))).ExpectOk();
                var message = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id"))).Single();
                Expect.Equal(kind.Type, InboxHelpers.S(message, "message_type"), "Message type");
                Expect.Equal(kind.Content, InboxHelpers.S(message, "content"), "Inbox text");
                Expect.True(InboxHelpers.S(message, "has_metadata") == "True", "The raw media object wasn't kept as metadata.");
            }, needs: Tg);
        }

        foreach (var kind in IgnoredKinds)
        {
            suite.Test("ignored-" + kind.Id, $"Telegram: {kind.Name} (200, nothing stored)", async ctx =>
            {
                var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
                var chat = kind.Id == "group" ? -ctx.Webhooks.NewChatId() : ctx.Webhooks.NewChatId();
                (await ctx.Webhooks.TelegramAsync(channel, kind.Update(ctx, chat))).ExpectOk();
                Expect.True(await InboxHelpers.TelegramThreadOrNullAsync(ctx, client.ClientId, chat) is null, "An ignored update created a conversation.");
            }, needs: Tg);
        }

        return suite.Build();
    }
}

