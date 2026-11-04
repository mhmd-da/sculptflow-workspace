using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>WhatsApp test plumbing: fake connections per test client (one WhatsApp connection per client), ingest calls on a
/// WhatsApp conversation, signed Meta deliveries in the documented shapes, and the rows the API doesn't expose.</summary>
internal static class WhatsAppHelpers
{
    /// <summary>A run-shared test client with a connected fake WhatsApp number (fake token, so every send fails at Meta).</summary>
    public static async Task<(TestClient Client, FakeChannel Channel)> WhatsAppAsync(TestContext ctx, string label)
    {
        var client = await ctx.NamedClientAsync(label);
        var channel = await InboxHelpers.OncePerRunAsync(ctx, "wa:" + label, () => ctx.Webhooks.FakeWhatsAppAsync(client));
        return (client, channel);
    }

    /// <summary>The contact with phone +<paramref name="waId"/> and its WhatsApp conversation, or null.</summary>
    public static async Task<Dictionary<string, object?>?> ByWaIdAsync(TestContext ctx, Guid clientId, string waId) =>
        (await ctx.Db.QueryAsync("""
            select c.id as contact_id, c.full_name, c.phone, c.source, c.opted_out_at, c.marketing_opt_in,
                   cv.id as conversation_id, cv.mode, cv.status, coalesce(cv.service_window_expires_at > now(), false) as window_open
            from crm.contacts c left join crm.conversations cv on cv.contact_id = c.id and cv.channel = 'whatsapp'
            where c.client_id = @clientId and c.phone = @phone
            order by c.created_at
            """, new { clientId, phone = "+" + waId }, ctx.Ct)).FirstOrDefault();

    public static async Task<Dictionary<string, object?>> RequireByWaIdAsync(TestContext ctx, Guid clientId, string waId) =>
        await ByWaIdAsync(ctx, clientId, waId) ?? throw new TestFailure($"No contact was created for wa_id {waId}.");

    public static async Task<long> ContactCountAsync(TestContext ctx, Guid clientId, string waId) =>
        await ctx.Db.ScalarAsync<long>("select count(*) from crm.contacts where client_id = @clientId and phone = @phone",
            new { clientId, phone = "+" + waId }, ctx.Ct);

    /// <summary>A contact and a dashboard-created WhatsApp conversation (the ingest path needs both ids up front, t8-04).</summary>
    public static async Task<(Guid ContactId, Guid ConversationId)> IngestThreadAsync(TestContext ctx, TestClient client, string label, bool withPhone = true)
    {
        Guid contactId;
        if (withPhone)
        {
            contactId = await InboxHelpers.NewContactAsync(ctx, client, label, phone: "+" + ctx.Webhooks.NewWaId());
        }
        else
        {
            contactId = (await ctx.Ingest.PostAsync("/api/contacts", new { clientId = client.ClientId, fullName = $"Auto {label} {ctx.Tag}", externalContactId = $"auto-{ctx.Tag}-{label}-{Guid.NewGuid():N}" }, ct: ctx.Ct))
                .ExpectStatus(201).Id();
        }
        return (contactId, await InboxHelpers.NewConversationAsync(ctx, client, contactId, "whatsapp"));
    }

    /// <summary>POST /api/messages/ingest for a WhatsApp conversation, with extra body fields.</summary>
    public static Task<ApiResponse> IngestAsync(TestContext ctx, TestClient client, (Guid ContactId, Guid ConversationId) thread, string eventType,
        params (string Key, object? Value)[] extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientId"] = client.ClientId,
            ["conversationId"] = thread.ConversationId,
            ["contactId"] = thread.ContactId,
            ["eventType"] = eventType,
            ["channel"] = "whatsapp",
        };
        foreach (var (key, value) in extra) body[key] = value;
        return ctx.Ingest.PostAsync("/api/messages/ingest", body, ct: ctx.Ct);
    }

    public static async Task<JsonNode> ConversationAsync(TestContext ctx, TestClient client, Guid conversationId) =>
        (await client.Http.GetAsync($"/api/conversations/{conversationId}", ct: ctx.Ct)).ExpectOk().Prop("conversation");

    /// <summary>The message row with this external id (delivery state included).</summary>
    public static async Task<Dictionary<string, object?>?> MessageByExternalIdAsync(TestContext ctx, Guid clientId, string externalId) =>
        (await ctx.Db.QueryAsync("""
            select id, direction, sender_type, origin, message_type, content, contact_id, conversation_id, delivery_status, failure_code,
                   delivered_at is not null as delivered, read_at is not null as read, failed_at is not null as failed
            from crm.messages where client_id = @clientId and external_message_id = @externalId
            """, new { clientId, externalId }, ctx.Ct)).FirstOrDefault();

    public static async Task<JsonNode> HealthAsync(TestContext ctx, TestClient client) =>
        (await client.Http.GetAsync("/api/whatsapp/health", ct: ctx.Ct)).ExpectOk().RequireJson();

    /// <summary>Puts the fake connection back to a clean, healthy, connected state (health tests share one client).</summary>
    public static Task ResetHealthAsync(TestContext ctx, TestClient client) =>
        ctx.Db.ExecuteForClientAsync(client.ClientId, """
            update channels.channel_integrations
            set status = 'connected', account_status = null, account_review_status = null, phone_quality_rating = null, phone_status = null,
                name_status = null, is_healthy = true, health_level = 'healthy', last_problem_code = null, last_problem_message = null, last_error = null
            where client_id = @clientId and channel = 'whatsapp'
            """, ct: ctx.Ct);

    public static async Task<int> UnhealthyNotificationsAsync(TestContext ctx, TestClient client) =>
        (await InboxHelpers.NotificationsAsync(ctx, client)).Count(n => n?["type"]?.ToString() == "INTEGRATION_UNHEALTHY");

    // ---- Meta shapes ----------------------------------------------------------------------------------------------

    /// <summary>One inbound message of any type: <paramref name="body"/> goes under the type's own property.</summary>
    public static JsonObject Message(TestContext ctx, string from, string type, JsonNode? body, string? id = null, long? timestamp = null)
    {
        var message = new JsonObject
        {
            ["from"] = from,
            ["id"] = id ?? ctx.Webhooks.NewWamid(),
            ["timestamp"] = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(),
            ["type"] = type,
        };
        if (body is not null) message[type] = body;
        return message;
    }

    public static JsonObject Text(TestContext ctx, string from, string text, string? id = null, long? timestamp = null) =>
        Message(ctx, from, "text", new JsonObject { ["body"] = text }, id, timestamp);

    /// <summary>A delivery with several changes (or entries) for one WABA.</summary>
    public static JsonObject Delivery(string wabaId, params JsonObject[] changes) => new()
    {
        ["object"] = "whatsapp_business_account",
        ["entry"] = new JsonArray(new JsonObject { ["id"] = wabaId, ["changes"] = new JsonArray(changes.Cast<JsonNode>().ToArray()) }),
    };

    public static JsonObject Change(string field, JsonObject value) => new() { ["field"] = field, ["value"] = value };

    /// <summary>A "messages" change: messages from one contact.</summary>
    public static JsonObject Messages(FakeChannel channel, string waId, string name, params JsonObject[] messages) =>
        Change("messages", Webhooks.MessagesValue(channel, waId, name, messages));

    /// <summary>A status change for an outbound wamid.</summary>
    public static JsonObject Status(FakeChannel channel, string wamid, string status, long timestamp, string recipient, JsonArray? errors = null)
    {
        var s = new JsonObject { ["id"] = wamid, ["status"] = status, ["timestamp"] = timestamp.ToString(), ["recipient_id"] = recipient };
        if (errors is not null) s["errors"] = errors;
        return Change("messages", new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = channel.PhoneNumberId },
            ["statuses"] = new JsonArray(s),
        });
    }

    /// <summary>A Business-app echo in the shape the parser reads (messages[] + the customer in contacts[]).</summary>
    public static JsonObject ParserEcho(TestContext ctx, FakeChannel channel, string customerWaId, string text, string wamid) =>
        Change("smb_message_echoes", new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = channel.PhoneNumberId },
            ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = "Automation Contact" }, ["wa_id"] = customerWaId }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["from"] = "15550000000", ["to"] = customerWaId, ["id"] = wamid,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ["type"] = "text", ["text"] = new JsonObject { ["body"] = text },
            }),
        });

    /// <summary>Posts a signed delivery and fails unless Meta would get a 200.</summary>
    public static async Task<ApiResponse> PostAsync(TestContext ctx, JsonObject delivery) => (await ctx.Webhooks.WhatsAppSignedAsync(delivery)).ExpectOk();
}

