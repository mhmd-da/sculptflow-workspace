using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;


/// <summary>Shared setup for the Inbox, Telegram and WhatsApp suites: contacts and conversations created the way n8n and the
/// dashboard create them, fake channel connections cached per run, and the database reads the API doesn't offer
/// (message rows, trace steps, usage records).</summary>
internal static class InboxHelpers
{
    private static readonly ConditionalWeakTable<TestContext, ConcurrentDictionary<string, Lazy<Task<object>>>> Cache = new();

    /// <summary>Runs <paramref name="factory"/> once per run for <paramref name="key"/> (e.g. a fake connection).</summary>
    public static async Task<T> OncePerRunAsync<T>(TestContext ctx, string key, Func<Task<T>> factory) where T : class
    {
        var map = Cache.GetOrCreateValue(ctx);
        var lazy = map.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await factory()));
        return (T)await lazy.Value;
    }

    /// <summary>A run-shared test client with a fake Telegram connection. One connection per client, so each label is its
    /// own client (settings such as auto-replies on one label never leak into another's tests).</summary>
    public static async Task<(TestClient Client, FakeChannel Channel)> TelegramAsync(TestContext ctx, string label = "tg")
    {
        var client = await ctx.NamedClientAsync(label);
        var channel = await OncePerRunAsync(ctx, "tg:" + label, () => ctx.Webhooks.FakeTelegramAsync(client));
        return (client, channel);
    }

    /// <summary>Creates a contact through the n8n intake (POST /api/contacts with the ingest key) — the only create path the
    /// API has.</summary>
    public static async Task<Guid> NewContactAsync(TestContext ctx, TestClient client, string label, string? fullName = null, string? phone = null)
    {
        var response = (await ctx.Ingest.PostAsync("/api/contacts", new
        {
            clientId = client.ClientId,
            serviceId = client.BaseServiceId,
            fullName = fullName ?? $"Auto {label} {ctx.Tag}",
            phone = phone ?? ctx.Fixtures.NewPhone(),
            email = $"contact-{ctx.Tag}-{Guid.NewGuid():N}@{TestContext.EmailDomain}",
            source = "automation",
            sourceDetail = "Omni.Automation",
            externalContactId = $"auto-{ctx.Tag}-{label}-{Guid.NewGuid():N}",
        }, ct: ctx.Ct)).ExpectStatus(201);
        return response.Id();
    }

    /// <summary>A conversation created by the owner from the dashboard (website channel unless given: nothing is ever
    /// delivered anywhere).</summary>
    public static async Task<Guid> NewConversationAsync(TestContext ctx, TestClient client, Guid contactId, string channel = "website") =>
        (await client.Http.PostAsync("/api/conversations", new
        {
            contactId,
            channel,
            externalThreadId = $"auto-{ctx.Tag}-{Guid.NewGuid():N}",
        }, ct: ctx.Ct)).ExpectStatus(201).Id();

    /// <summary>A contact plus a website conversation for it.</summary>
    public static async Task<(Guid ContactId, Guid ConversationId)> NewThreadAsync(TestContext ctx, TestClient client, string label)
    {
        var contactId = await NewContactAsync(ctx, client, label);
        return (contactId, await NewConversationAsync(ctx, client, contactId));
    }

    public static Task<ApiResponse> IngestAsync(TestContext ctx, object body) => ctx.Ingest.PostAsync("/api/messages/ingest", body, ct: ctx.Ct);

    /// <summary>A text from a (new, unless given) Telegram chat; fails unless the webhook answers 200.</summary>
    public static async Task<long> TelegramTextAsync(TestContext ctx, FakeChannel channel, string text, long? chatId = null)
    {
        var chat = chatId ?? ctx.Webhooks.NewChatId();
        (await ctx.Webhooks.TelegramAsync(channel, ctx.Webhooks.TelegramMessage(chat, text))).ExpectOk();
        return chat;
    }

    /// <summary>The conversation (and its contact) a Telegram chat created, or null.</summary>
    public static async Task<Dictionary<string, object?>?> TelegramThreadOrNullAsync(TestContext ctx, Guid clientId, long chatId) =>
        (await ctx.Db.QueryAsync("""
            select cv.id, cv.contact_id, cv.mode, cv.status, c.full_name, c.opted_out_at, c.marketing_opt_in, c.source, c.source_detail
            from crm.conversations cv join crm.contacts c on c.id = cv.contact_id
            where cv.client_id = @clientId and cv.channel = 'telegram' and cv.external_thread_id = @thread
            """, new { clientId, thread = chatId.ToString() }, ctx.Ct)).FirstOrDefault();

    public static async Task<Dictionary<string, object?>> TelegramThreadAsync(TestContext ctx, Guid clientId, long chatId) =>
        await TelegramThreadOrNullAsync(ctx, clientId, chatId)
        ?? throw new TestFailure($"Telegram chat {chatId} created no conversation.");

    /// <summary>Message rows of a conversation, oldest first.</summary>
    public static Task<List<Dictionary<string, object?>>> MessagesAsync(TestContext ctx, Guid conversationId) =>
        ctx.Db.QueryAsync("""
            select id, direction, sender_type, origin, channel, message_type, content, external_message_id, delivery_status,
                   metadata_json is not null as has_metadata, contact_id
            from crm.messages where conversation_id = @conversationId order by created_at, id
            """, new { conversationId }, ctx.Ct);

    /// <summary>Trace steps of a conversation (kind/reason pulled out of the detail).</summary>
    public static Task<List<Dictionary<string, object?>>> StepsAsync(TestContext ctx, Guid clientId, Guid conversationId) =>
        ctx.Db.QueryAsync("""
            select step, outcome, detail->>'kind' as kind, detail->>'reason' as reason
            from activity.message_trace_steps
            where client_id = @clientId and conversation_id = @conversationId and occurred_at > now() - interval '1 hour'
            order by occurred_at, id
            """, new { clientId, conversationId }, ctx.Ct);

    /// <summary>Waits until the conversation's trace satisfies <paramref name="done"/> (steps are written in the background).</summary>
    public static Task<List<Dictionary<string, object?>>> WaitForStepsAsync(TestContext ctx, Guid clientId, Guid conversationId,
        Func<List<Dictionary<string, object?>>, bool> done, string what, int seconds = 45) =>
        ctx.EventuallyAsync(async () =>
        {
            var steps = await StepsAsync(ctx, clientId, conversationId);
            return done(steps) ? steps : null;
        }, what, TimeSpan.FromSeconds(seconds));

    public static int Count(List<Dictionary<string, object?>> steps, string step, string? kind = null, string? reason = null) =>
        steps.Count(s => S(s, "step") == step && (kind is null || S(s, "kind") == kind) && (reason is null || S(s, "reason") == reason));

    public static string Describe(List<Dictionary<string, object?>> steps) =>
        string.Join(", ", steps.Select(s => S(s, "step") + (S(s, "kind") is { } k ? $"[{k}]" : "") + (S(s, "reason") is { } r ? $"({r})" : "")));

    public static string? S(Dictionary<string, object?> row, string column) => row.TryGetValue(column, out var value) ? value?.ToString() : null;

    public static Guid G(Dictionary<string, object?> row, string column) => (Guid)row[column]!;

    /// <summary>GET /api/contacts/{id} as the client.</summary>
    public static async Task<JsonNode> ContactAsync(TestContext ctx, TestClient client, Guid contactId) =>
        (await client.Http.GetAsync($"/api/contacts/{contactId}", ct: ctx.Ct)).ExpectOk().RequireJson();

    public static bool IsOptedOut(JsonNode contact) =>
        contact["optedOutAt"] is { } at && at.GetValueKind() != System.Text.Json.JsonValueKind.Null || contact["marketingOptIn"]?.GetValue<bool>() == false;

    /// <summary>The client's notifications (newest first).</summary>
    public static async Task<JsonArray> NotificationsAsync(TestContext ctx, TestClient client) =>
        (await client.Http.GetAsync("/api/notifications?take=100", ct: ctx.Ct)).ExpectOk().Array("items");

    public static async Task<int> UnreadCountAsync(TestContext ctx, TestClient client) =>
        (await client.Http.GetAsync("/api/notifications/unread-count", ct: ctx.Ct)).ExpectOk().Prop("count").GetValue<int>();

    /// <summary>Fails when a refusal came back as a server error (an unhandled exception) instead of a clear 4xx.</summary>
    public static ApiResponse ExpectClientError(this ApiResponse response, string what)
    {
        if (response.Status >= 500) throw new TestFailure($"{what}: the server crashed with {response.Status} instead of refusing the input with a 400.{response.Snippet(300)}");
        if (response.IsSuccess) throw new TestFailure($"{what}: it was accepted ({response.Status}) but should have been refused.{response.Snippet(300)}");
        return response;
    }
}

