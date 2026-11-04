using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Message ingest with the ingest key (t3-inbox-18).</summary>
[Suite(43)]
public static class InboxIngestSuite
{
    private const Needs Api = Needs.Session | Needs.IngestKey;

    public static TestSuite Build() => new SuiteBuilder("inbox-ingest", "Inbox: message ingest (ingest key)",
            "POST /api/messages/ingest, the n8n path that records customer, AI and status events on a conversation.")
        .Test("no-key", "Ingest: without the ingest key the call is refused (401)", async ctx =>
        {
            (await ctx.Anonymous.PostAsync("/api/messages/ingest", new { clientId = Guid.NewGuid(), conversationId = Guid.NewGuid(), contactId = Guid.NewGuid(), eventType = "customer_message", channel = "whatsapp", content = "x" }, ct: ctx.Ct)).ExpectStatus(401);
            var wrong = ctx.NewHttp(new Dictionary<string, string> { ["X-Ingest-Key"] = "not-the-key" });
            (await wrong.PostAsync("/api/messages/ingest", new { clientId = Guid.NewGuid(), conversationId = Guid.NewGuid(), contactId = Guid.NewGuid(), eventType = "customer_message", channel = "whatsapp", content = "x" }, ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Test("no-key-bad-body", "Ingest: an unauthenticated caller gets 401 before any body validation", async ctx =>
        {
            var response = await ctx.Anonymous.PostAsync("/api/messages/ingest", new { }, ct: ctx.Ct);
            Expect.True(response.Status == 401, $"An empty body without the key answered {response.Status} (it should be 401 before validation; a 400 tells an anonymous caller which fields exist).{response.Snippet(200)}");
        }, needs: Needs.None, finding: "t3-inbox-18")
        .Test("bad-event", "Ingest: an unknown eventType is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "badevent");
            (await InboxHelpers.IngestAsync(ctx, new { clientId = client.ClientId, conversationId, contactId, eventType = "banana", channel = "website", content = "x" })).ExpectStatus(400);
        }, needs: Api)
        .Test("customer-message", "Ingest: a customer message is saved as inbound and isn't sent to the AI on a plan without it", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "customer");
            var result = (await InboxHelpers.IngestAsync(ctx, new { clientId = client.ClientId, conversationId, contactId, eventType = "customer_message", channel = "website", content = "Hello from ingest", externalMessageId = $"auto-{Guid.NewGuid():N}" })).ExpectOk();
            Expect.True(result.Prop("found").GetValue<bool>(), "found should be true.");
            Expect.True(!result.Prop("aiEligible").GetValue<bool>(), "aiEligible should be false (no AI on the plan, conversation with the team).");
            var message = (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Array("messages").Single()!;
            Expect.EqualText("inbound", message["direction"], "Direction");
            Expect.EqualText("contact", message["senderType"], "Sender");
            Expect.EqualText("Hello from ingest", message["content"], "Content");
        }, needs: Api)
        .Test("ai-message", "Ingest: an AI message n8n already sent is saved as outbound AI", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "aimsg");
            (await InboxHelpers.IngestAsync(ctx, new { clientId = client.ClientId, conversationId, contactId, eventType = "ai_message", channel = "website", content = "AI says hi", externalMessageId = $"auto-{Guid.NewGuid():N}" })).ExpectOk();
            var message = (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Array("messages").Single()!;
            Expect.EqualText("outbound", message["direction"], "Direction");
            Expect.EqualText("ai", message["senderType"], "Sender");
            Expect.True(message["isAiGenerated"]?.GetValue<bool>() == true, "isAiGenerated should be true.");
        }, needs: Api)
        .Test("unknown-conversation", "Ingest: an unknown conversation gives 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await InboxHelpers.NewContactAsync(ctx, client, "noconv");
            (await InboxHelpers.IngestAsync(ctx, new { clientId = client.ClientId, conversationId = Guid.NewGuid(), contactId, eventType = "customer_message", channel = "website", content = "x" })).ExpectStatus(404);
        }, needs: Api)
        .Test("wrong-client", "Ingest: a conversation of another client gives 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var other = await ctx.OtherClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "wrongclient");
            (await InboxHelpers.IngestAsync(ctx, new { clientId = other.ClientId, conversationId, contactId, eventType = "customer_message", channel = "website", content = "x" })).ExpectStatus(404);
            Expect.Equal(0, (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Array("messages").Count, "Messages in the conversation");
        }, needs: Api)
        .Test("dedup", "Ingest: the same external message id twice is saved once", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "dedup");
            var body = new { clientId = client.ClientId, conversationId, contactId, eventType = "customer_message", channel = "website", content = "once", externalMessageId = $"auto-{Guid.NewGuid():N}" };
            Expect.True(!(await InboxHelpers.IngestAsync(ctx, body)).ExpectOk().Prop("deduplicated").GetValue<bool>(), "The first delivery was reported as a duplicate.");
            Expect.True((await InboxHelpers.IngestAsync(ctx, body)).ExpectOk().Prop("deduplicated").GetValue<bool>(), "The repeat wasn't reported as a duplicate.");
            Expect.Equal(1, (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Array("messages").Count, "Messages after a repeated delivery");
        }, needs: Api)
        .Test("status-unknown-message", "Ingest: a status update for a message nobody sent gives 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "nostatus");
            (await InboxHelpers.IngestAsync(ctx, new { clientId = client.ClientId, conversationId, contactId, eventType = "status_update", channel = "website", externalMessageId = $"auto-missing-{Guid.NewGuid():N}", deliveryStatus = "delivered" })).ExpectStatus(404);
        }, needs: Api)
        .Test("reopens", "Ingest: a customer message reopens a closed conversation", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (contactId, conversationId) = await InboxHelpers.NewThreadAsync(ctx, client, "reopen");
            (await client.Http.PostAsync($"/api/conversations/{conversationId}/close", ct: ctx.Ct)).ExpectOk();
            (await InboxHelpers.IngestAsync(ctx, new { clientId = client.ClientId, conversationId, contactId, eventType = "customer_message", channel = "website", content = "back again" })).ExpectOk();
            Expect.EqualText("active", (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Prop("conversation")["status"], "Status after the customer wrote again");
        }, needs: Api)
        .Build();
}

/// <summary>The notification bell, fed by new contacts on a fake Telegram bot (t3-inbox-19).</summary>
[Suite(44)]
public static class InboxNotificationsSuite
{
    private const Needs Tg = Needs.Session | Needs.Database;

    /// <summary>A NEW_CONTACT notification created by a first Telegram message on the "notif" client.</summary>
    private static async Task<(TestClient Client, JsonNode Notification)> NewContactNotificationAsync(TestContext ctx)
    {
        var (client, channel) = await InboxHelpers.TelegramAsync(ctx, "notif");
        var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hello, I'm new");
        var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
        var notification = (await InboxHelpers.NotificationsAsync(ctx, client))
            .FirstOrDefault(n => n?["type"]?.ToString() == "NEW_CONTACT" && n["conversationId"]?.ToString() == conversationId.ToString())
            ?? throw new TestFailure("A new Telegram contact created no NEW_CONTACT notification.");
        return (client, notification);
    }

    public static TestSuite Build() => new SuiteBuilder("inbox-notifications", "Inbox: notifications",
            "The bell: new-contact notifications, unread count, mark read / read all, and isolation.")
        .Test("signed-out", "Notifications: signed-out callers get 401", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/notifications", ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Test("new-contact", "Notifications: a new contact creates an unread NEW_CONTACT notification linking to its conversation", async ctx =>
        {
            var (_, notification) = await NewContactNotificationAsync(ctx);
            Expect.True(notification["isRead"]?.GetValue<bool>() == false, "The new notification is already marked read.");
            Expect.EqualText($"/inbox?conversationId={notification["conversationId"]}", notification["link"], "Link");
        }, needs: Tg)
        .Test("mark-read", "Notifications: marking one read updates it and the unread count", async ctx =>
        {
            var (client, notification) = await NewContactNotificationAsync(ctx);
            var before = await InboxHelpers.UnreadCountAsync(ctx, client);
            (await client.Http.PostAsync($"/api/notifications/{notification["id"]}/read", ct: ctx.Ct)).ExpectStatus(204);
            var after = (await InboxHelpers.NotificationsAsync(ctx, client)).First(n => n?["id"]?.ToString() == notification["id"]!.ToString())!;
            Expect.True(after["isRead"]?.GetValue<bool>() == true, "The notification isn't marked read.");
            Expect.Equal(before - 1, await InboxHelpers.UnreadCountAsync(ctx, client), "Unread count after marking one read");
        }, needs: Tg)
        .Test("read-all", "Notifications: read-all clears the unread count", async ctx =>
        {
            var (client, _) = await NewContactNotificationAsync(ctx);
            Expect.True(await InboxHelpers.UnreadCountAsync(ctx, client) > 0, "There is no unread notification to clear.");
            (await client.Http.PostAsync("/api/notifications/read-all", ct: ctx.Ct)).ExpectStatus(204);
            Expect.Equal(0, await InboxHelpers.UnreadCountAsync(ctx, client), "Unread count after read-all");
        }, needs: Tg)
        .Test("isolation-no-effect", "Notifications: another client marking my notification read changes nothing", async ctx =>
        {
            var (client, notification) = await NewContactNotificationAsync(ctx);
            var other = (await ctx.OtherClientAsync()).Http;
            await other.PostAsync($"/api/notifications/{notification["id"]}/read", ct: ctx.Ct);
            var after = (await InboxHelpers.NotificationsAsync(ctx, client)).First(n => n?["id"]?.ToString() == notification["id"]!.ToString())!;
            Expect.True(after["isRead"]?.GetValue<bool>() == false, "Another client marked this client's notification read.");
        }, needs: Tg)
        .Test("isolation-404", "Notifications: another client's (or an unknown) notification id gives 404", async ctx =>
        {
            var (_, notification) = await NewContactNotificationAsync(ctx);
            var other = (await ctx.OtherClientAsync()).Http;
            var foreign = await other.PostAsync($"/api/notifications/{notification["id"]}/read", ct: ctx.Ct);
            var unknown = await other.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", ct: ctx.Ct);
            Expect.True(foreign.Status == 404 && unknown.Status == 404,
                $"Marking another client's notification read answered {foreign.Status} and an unknown id {unknown.Status}; both should be 404 like every other action.");
        }, needs: Tg, finding: "t3-inbox-19")
        .Build();
}

