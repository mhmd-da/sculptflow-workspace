using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omni.Automation.Http;

namespace Omni.Automation.Core;

/// <summary>A fake channel connection of a test client: nothing real behind it, so inbound traffic is simulated and every
/// outbound send fails at the provider (by design — no message can reach a real person).</summary>
public sealed record FakeChannel(Guid IntegrationId, string Channel, string? WebhookSecret, string? PhoneNumberId, string? WabaId);

/// <summary>
/// Simulated provider traffic: WhatsApp deliveries signed exactly as Meta signs them (X-Hub-Signature-256 with the
/// target's Meta:AppSecret) and Telegram updates with the connection's secret token. Fake connections are created by a
/// direct DB insert on the test client (Needs.Database), so they don't depend on the unverified PUT that t8-01 reports.
/// </summary>
public sealed class Webhooks
{
    public const string WhatsAppPath = "/api/integrations/whatsapp/webhook";
    private readonly TestContext _ctx;
    private long _updateId = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 10;
    private int _wamid;

    public Webhooks(TestContext ctx) => _ctx = ctx;

    // ---- fake connections ----------------------------------------------------------------------------------------

    /// <summary>Inserts a connected fake WhatsApp connection for the client (fake token, phone number id and WABA id).</summary>
    public async Task<FakeChannel> FakeWhatsAppAsync(TestClient client, string? phoneNumberId = null, string? wabaId = null)
    {
        var id = Guid.NewGuid();
        phoneNumberId ??= $"auto-{_ctx.Tag}-{Random.Shared.Next(100000, 999999)}";
        wabaId ??= $"auto-waba-{_ctx.Tag}-{Random.Shared.Next(100000, 999999)}";
        await _ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into channels.channel_integrations (id, client_id, channel, status, display_name, phone_number_id, whatsapp_business_id,
                access_token, created_at, updated_at)
            values (@id, @clientId, 'whatsapp', 'connected', @name, @phone, @waba, @token, now(), now())
            """, new { id, name = $"{TestContext.ClientNamePrefix} WA fake", phone = phoneNumberId, waba = wabaId, token = "auto-fake-token-" + Guid.NewGuid().ToString("N") }, _ctx.Ct);
        await EvictChannelsAsync(client);
        return new FakeChannel(id, "whatsapp", null, phoneNumberId, wabaId);
    }

    /// <summary>Inserts a connected fake Telegram connection with a known webhook secret. Sends from it fail ("Unauthorized").</summary>
    public async Task<FakeChannel> FakeTelegramAsync(TestClient client)
    {
        var id = Guid.NewGuid();
        var secret = "auto" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var botId = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        await _ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into channels.channel_integrations (id, client_id, channel, status, display_name, access_token, webhook_verify_token,
                telegram_bot_id, telegram_bot_username, webhook_status, created_at, updated_at)
            values (@id, @clientId, 'telegram', 'connected', @name, @token, @secret, @botId, @username, 'active', now(), now())
            """, new { id, name = $"@auto_{_ctx.Tag}_bot", token = $"{botId}:AutoFakeToken{Guid.NewGuid():N}", secret, botId, username = $"auto_{_ctx.Tag}_bot" }, _ctx.Ct);
        await EvictChannelsAsync(client);
        return new FakeChannel(id, "telegram", secret, null, null);
    }

    /// <summary>The app caches a client's channel list (CacheKeys.ChannelIntegrations, "channels:integrations:{id:N}") for up
    /// to an hour; after a direct insert, drop it so pages and senders see the fake connection. Needs the admin key; without
    /// it the test continues and only a cached list would be stale.</summary>
    private async Task EvictChannelsAsync(TestClient client)
    {
        if (_ctx.Env.HasAdminKey) await _ctx.Fixtures.EvictCacheAsync($"channels:integrations:{client.ClientId:N}");
    }

    // ---- WhatsApp ------------------------------------------------------------------------------------------------

    /// <summary>Posts a body to the WhatsApp webhook signed with the Meta app secret (skips the test without it).</summary>
    public Task<ApiResponse> WhatsAppSignedAsync(object payload) => WhatsAppSignedRawAsync(Json(payload));

    public async Task<ApiResponse> WhatsAppSignedRawAsync(string body)
    {
        if (!_ctx.Env.HasMetaSecret) throw new TestSkipped("No Meta app secret configured for this environment.");
        var response = await _ctx.Anonymous.PostRawAsync(WhatsAppPath, body, "application/json",
            new Dictionary<string, string> { ["X-Hub-Signature-256"] = Sign(body, _ctx.Env.MetaAppSecret!) }, _ctx.Ct);
        if (response.Status == 200) await WaitProcessedAsync(body);
        return response;
    }

    /// <summary>The app answers Meta once a delivery is stored and processes it in the background (QA t10-10), so a test waits
    /// for its delivery to be processed before it looks at the result: the row in channels.whatsapp_webhook_deliveries with
    /// this body's hash turns done (or failed). Without database access it waits a few seconds; against an app from before
    /// the inbox (no such table) it doesn't wait.</summary>
    private async Task WaitProcessedAsync(string body)
    {
        if (!_ctx.Env.HasDatabase)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _ctx.Ct);
            return;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < until)
        {
            string? status;
            try
            {
                status = await _ctx.Db.ScalarAsync<string?>("select status from channels.whatsapp_webhook_deliveries where body_hash = @hash", new { hash }, _ctx.Ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return; // an app from before the webhook inbox processes during the request
            }
            if (status is "done" or "failed") return;
            await Task.Delay(200, _ctx.Ct);
        }
        throw new TestFailure("The WhatsApp delivery wasn't processed within 30 seconds of being accepted.");
    }

    /// <summary>"sha256=" + hex(HMAC-SHA256(body, secret)), Meta's X-Hub-Signature-256.</summary>
    public static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>A Meta delivery with one change. <paramref name="value"/> is the change's "value" object.</summary>
    public static JsonObject Delivery(string wabaId, string field, JsonObject value) => new()
    {
        ["object"] = "whatsapp_business_account",
        ["entry"] = new JsonArray(new JsonObject
        {
            ["id"] = wabaId,
            ["changes"] = new JsonArray(new JsonObject { ["field"] = field, ["value"] = value }),
        }),
    };

    /// <summary>The "messages" change value for inbound messages from one contact.</summary>
    public static JsonObject MessagesValue(FakeChannel channel, string waId, string profileName, params JsonObject[] messages) => new()
    {
        ["messaging_product"] = "whatsapp",
        ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = channel.PhoneNumberId },
        ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = profileName }, ["wa_id"] = waId }),
        ["messages"] = new JsonArray(messages.Cast<JsonNode>().ToArray()),
    };

    /// <summary>One inbound text message (unique wamid unless given). Timestamp defaults to now.</summary>
    public JsonObject TextMessage(string from, string text, string? wamid = null, DateTimeOffset? at = null) => new()
    {
        ["from"] = from,
        ["id"] = wamid ?? NewWamid(),
        ["timestamp"] = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(),
        ["type"] = "text",
        ["text"] = new JsonObject { ["body"] = text },
    };

    /// <summary>A signed delivery of one text message from <paramref name="waId"/> to the fake connection.</summary>
    public Task<ApiResponse> WhatsAppTextAsync(FakeChannel channel, string waId, string text, string profileName = "Automation Contact", string? wamid = null, DateTimeOffset? at = null) =>
        WhatsAppSignedAsync(Delivery(channel.WabaId!, "messages", MessagesValue(channel, waId, profileName, TextMessage(waId, text, wamid, at))));

    public string NewWamid() => $"wamid.AUTO{_ctx.Tag}{Interlocked.Increment(ref _wamid):D6}";

    /// <summary>A wa_id (phone digits) in a fictional range, unique within the run.</summary>
    public string NewWaId() => $"1555{Random.Shared.Next(1000000, 9999999)}";

    // ---- Telegram ------------------------------------------------------------------------------------------------

    /// <summary>Posts a Telegram update to the fake connection's webhook with its secret token.</summary>
    public Task<ApiResponse> TelegramAsync(FakeChannel channel, object update, string? secretOverride = null) =>
        _ctx.Anonymous.PostRawAsync($"/api/integrations/telegram/webhook/{channel.IntegrationId}", Json(update), "application/json",
            new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = secretOverride ?? channel.WebhookSecret! }, _ctx.Ct);

    /// <summary>A Telegram "message" update from a private chat. <paramref name="extra"/> is merged into the message
    /// (e.g. photo, sticker, location instead of text).</summary>
    public JsonObject TelegramMessage(long chatId, string? text, JsonObject? extra = null, string language = "en")
    {
        var from = new JsonObject { ["id"] = chatId, ["is_bot"] = false, ["first_name"] = "Automation", ["last_name"] = chatId.ToString()[^4..], ["language_code"] = language };
        var message = new JsonObject
        {
            ["message_id"] = Interlocked.Increment(ref _updateId) % 1_000_000,
            ["from"] = from,
            ["chat"] = new JsonObject { ["id"] = chatId, ["type"] = "private", ["first_name"] = "Automation" },
            ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        if (text is not null) message["text"] = text;
        if (extra is not null) foreach (var (k, v) in extra) message[k] = v?.DeepClone();
        return new JsonObject { ["update_id"] = Interlocked.Increment(ref _updateId), ["message"] = message };
    }

    /// <summary>A fake Telegram chat id, unique within the run (never a real chat).</summary>
    public long NewChatId() => 9_100_000_000 + Random.Shared.Next(0, 99_999_999);

    private static string Json(object payload) => payload switch
    {
        string s => s,
        JsonNode n => n.ToJsonString(),
        _ => JsonSerializer.Serialize(payload, OmniClient.JsonOptions),
    };
}
