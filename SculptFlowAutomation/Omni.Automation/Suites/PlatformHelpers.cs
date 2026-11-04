using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>
/// Helpers shared by the platform and accounts suites: per-run private test clients, DB-inserted account tokens
/// (email isn't configured on staging, so invites and reset links are made the way QA made them), a raw cookie
/// session for response headers and SignalR, and small HTML checks.
/// </summary>
internal static partial class PlatformHelpers
{
    private static readonly ConditionalWeakTable<TestContext, Dictionary<string, object>> PerRun = new();

    /// <summary>Runs <paramref name="create"/> once per run and caches its task (a failure is cached too, so every test
    /// that needs the same setup fails with the same reason).</summary>
    public static Task<T> Once<T>(TestContext ctx, string key, Func<Task<T>> create)
    {
        var bag = PerRun.GetOrCreateValue(ctx);
        lock (bag)
        {
            if (!bag.TryGetValue(key, out var task))
            {
                task = create();
                bag[key] = task;
            }
            return (Task<T>)task;
        }
    }

    // ---- private test clients -----------------------------------------------------------------------------------

    /// <summary>A private general client for business-info and settings tests (renamed, rescheduled, type-changed).</summary>
    public static Task<TestClient> SettingsClientAsync(TestContext ctx) => ctx.NamedClientAsync("settings");

    /// <summary>A private general client on the "business" plan (12 seats, every module): members, invites, roles and
    /// full-plan pages. The plan change is a billing write, so tests using it are Risk.Billing + AdminKey.</summary>
    public static Task<TestClient> TeamAsync(TestContext ctx) => Once(ctx, "team", async () =>
    {
        var team = await ctx.NamedClientAsync("team");
        await ctx.SetPlanAsync(team, "business");
        return team;
    });

    /// <summary>A private client whose owner's password and lockout the sign-in and reset tests change. Its current
    /// password lives here, not in TestClient.Password, because a reset changes it.</summary>
    public static Task<AcctState> AcctAsync(TestContext ctx) => Once(ctx, "acct", async () =>
    {
        var client = await ctx.NamedClientAsync("acct");
        var owner = await OwnerUserIdAsync(ctx, client);
        return new AcctState(client, owner) { Password = client.Password };
    });

    public sealed class AcctState(TestClient client, string userId)
    {
        public TestClient Client { get; } = client;
        public string UserId { get; } = userId;
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>The owner's identity user id (GET /api/members as the owner).</summary>
    public static async Task<string> OwnerUserIdAsync(TestContext ctx, TestClient client)
    {
        var members = (await client.Http.GetAsync("/api/members", ct: ctx.Ct)).ExpectOk().Array();
        var owner = members.FirstOrDefault(m => string.Equals(m?["email"]?.ToString(), client.Email, StringComparison.OrdinalIgnoreCase))
                    ?? throw new TestFailure($"The owner {client.Email} is not in GET /api/members.");
        return owner["userId"]!.ToString();
    }

    // ---- data in a test client ----------------------------------------------------------------------------------

    /// <summary>Creates a contact for any test client through the ingest API (the only create route; it needs the key).</summary>
    public static async Task<Guid> IngestContactAsync(TestContext ctx, TestClient client, string fullName)
    {
        var response = (await ctx.Ingest.PostAsync("/api/contacts", new
        {
            clientId = client.ClientId,
            serviceId = client.BaseServiceId,
            fullName,
            phone = ctx.Fixtures.NewPhone(),
            email = $"contact-{ctx.Tag}-{Guid.NewGuid():N}@{TestContext.EmailDomain}",
            source = "automation",
            externalContactId = $"auto-{ctx.Tag}-{Guid.NewGuid():N}",
            preferredLanguage = "en",
        }, ct: ctx.Ct)).ExpectStatus(200, 201);
        return response.Id();
    }

    /// <summary>A website-channel conversation (nothing is ever delivered on it) created by the client's own session.</summary>
    public static async Task<Guid> ConversationAsync(TestContext ctx, OmniClient http, Guid contactId)
    {
        var response = (await http.PostAsync("/api/conversations", new
        {
            contactId,
            channel = "website",
            externalThreadId = $"auto-{ctx.Tag}-{Guid.NewGuid():N}",
        }, ct: ctx.Ct)).ExpectStatus(200, 201);
        return response.Id();
    }

    // ---- account tokens (invites and reset links) ---------------------------------------------------------------

    /// <summary>A raw link token like the app's (32 random bytes, base64url).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What identity.account_tokens.token_hash stores: lowercase hex SHA-256 of the raw token.</summary>
    public static string HashToken(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>Inserts an open invite for <paramref name="email"/> into the client (as if the owner had sent it).
    /// Negative minutes make an expired one.</summary>
    public static async Task<(Guid Id, string Token)> InsertInviteAsync(TestContext ctx, TestClient client, string email,
        string? fullName = null, int minutes = 7 * 24 * 60)
    {
        var id = Guid.NewGuid();
        var raw = NewToken();
        var rows = await ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into identity.account_tokens (id, purpose, user_id, client_id, email, full_name, token_hash, created_by_user_id, expires_at, created_at)
            select @id, 'invite', null, @clientId, @email, @fullName, @hash,
                   (select cu.user_id from identity.client_users cu join identity.users u on u.id = cu.user_id
                    where cu.client_id = @clientId and u.role = 'client' limit 1),
                   now() + make_interval(mins => @minutes), now()
            """, new { id, email, fullName, hash = HashToken(raw), minutes }, ctx.Ct);
        Expect.Equal(1, rows, "Invite rows inserted");
        return (id, raw);
    }

    /// <summary>Inserts a password reset link for a user of the client (as "Forgot password?" would).</summary>
    public static async Task<string> InsertResetTokenAsync(TestContext ctx, TestClient client, string userId, int minutes = 60)
    {
        var raw = NewToken();
        var rows = await ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into identity.account_tokens (id, purpose, user_id, client_id, email, token_hash, expires_at, created_at)
            select gen_random_uuid(), 'password_reset', u.id, null, u.email, @hash, now() + make_interval(mins => @minutes), now()
            from identity.users u join identity.client_users cu on cu.user_id = u.id
            where cu.client_id = @clientId and u.id = @userId
            """, new { userId, hash = HashToken(raw), minutes }, ctx.Ct);
        Expect.Equal(1, rows, "Reset links inserted");
        return raw;
    }

    /// <summary>Clears lockout and failed sign-in counts of every user of the client.</summary>
    public static Task UnlockUsersAsync(TestContext ctx, TestClient client) =>
        ctx.Db.ExecuteForClientAsync(client.ClientId, """
            update identity.users u set lockout_end = null, access_failed_count = 0
            from identity.client_users cu where cu.user_id = u.id and cu.client_id = @clientId
            """, ct: ctx.Ct);

    // ---- forms and pages ----------------------------------------------------------------------------------------

    /// <summary>Signs in on a fresh client and fails unless it worked.</summary>
    public static async Task<OmniClient> SignedInAsync(TestContext ctx, string email, string password)
    {
        var (http, response) = await ctx.SignInAsync(email, password);
        if (response.Status != 302)
        {
            throw new TestFailure($"Signing in as {email} failed (status {response.Status}): {TestContext.PageError(response.Body) ?? "no error shown"}.");
        }
        return http;
    }

    /// <summary>True when the client still has a working session (an API call answers 200).</summary>
    public static async Task<int> SessionStatusAsync(TestContext ctx, OmniClient http) =>
        (await http.GetAsync("/api/members", ct: ctx.Ct)).Status;

    public static bool HasPasswordField(string html) => html.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>The visible text of a page's alert, or "" (never null, for messages).</summary>
    public static string Alert(ApiResponse response) => TestContext.PageError(response.Body) ?? string.Empty;

    /// <summary>The page's &lt;html&gt; tag (lang and dir).</summary>
    public static string HtmlTag(string html) => HtmlTagRegex().Match(html) is { Success: true } m ? m.Value : "(no html tag)";

    /// <summary>True when the location points at the target itself (a relative path, not "//host" or "/\host").</summary>
    public static bool IsSameSite(TestContext ctx, string? location)
    {
        if (string.IsNullOrEmpty(location)) return false;
        var baseUri = new Uri(ctx.Env.BaseUrl.TrimEnd('/') + "/");
        if (!Uri.TryCreate(baseUri, location, out var target)) return false;
        return string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
               && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>Fails when a page answered with a server error or the error page.</summary>
    public static void ExpectNoServerError(ApiResponse page, string what)
    {
        if (page.Status >= 500 || page.Body.Contains("Something went wrong", StringComparison.OrdinalIgnoreCase))
        {
            throw new TestFailure($"{what}: {page.Method} {page.Path} answered {page.Status} (server error page).");
        }
    }

    /// <summary>Setting value from the platform-admin settings API (stored row or null).</summary>
    public static async Task<string?> StoredSettingAsync(TestContext ctx, string section, string key)
    {
        var all = (await ctx.Admin.GetAsync("/api/platform-admin/settings", ct: ctx.Ct)).ExpectOk().Array();
        var row = all.FirstOrDefault(s => s?["section"]?.ToString() == section && s?["key"]?.ToString() == key)
                  ?? throw new TestFailure($"Setting {section}:{key} isn't declared on the target.");
        return row["storedValue"]?.ToString();
    }

    public static async Task SetSettingAsync(TestContext ctx, string section, string key, string value) =>
        (await ctx.Admin.PutAsync($"/api/platform-admin/settings/{section}/{key}", new { value, note = $"Omni.Automation {ctx.Tag}" }, ctx.Ct))
        .ExpectStatus(200);

    /// <summary>Puts a setting back the way it was: the stored value, or no row at all.</summary>
    public static async Task RestoreSettingAsync(TestContext ctx, string section, string key, string? stored)
    {
        if (stored is null)
        {
            var response = await ctx.Admin.DeleteAsync($"/api/platform-admin/settings/{section}/{key}", CancellationToken.None);
            if (response.Status is not (204 or 404)) throw new TestFailure($"Couldn't restore {section}:{key} (status {response.Status}).");
        }
        else
        {
            (await ctx.Admin.PutAsync($"/api/platform-admin/settings/{section}/{key}", new { value = stored, note = "restored by Omni.Automation" }, CancellationToken.None))
                .ExpectStatus(200);
        }
        ctx.Log($"Restored {section}:{key}.");
    }

    [GeneratedRegex("<html[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();
}

/// <summary>
/// A plain HttpClient with its own cookie jar, for what <see cref="OmniClient"/> doesn't expose: response headers,
/// Set-Cookie flags, and the cookies a SignalR WebSocket needs. Requests are logged like OmniClient's.
/// </summary>
internal sealed class RawSession : IDisposable
{
    private readonly TestContext _ctx;

    public RawSession(TestContext ctx)
    {
        _ctx = ctx;
        BaseUri = new Uri(ctx.Env.BaseUrl.TrimEnd('/') + "/");
        Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = Cookies })
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(60),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Omni.Automation/2.0");
    }

    public Uri BaseUri { get; }
    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var sw = Stopwatch.StartNew();
        var response = await Http.SendAsync(new HttpRequestMessage(method, path.TrimStart('/')) { Content = content }, _ctx.Ct);
        _ctx.Log($"{method} /{path.TrimStart('/')} -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds} ms (raw)");
        return response;
    }

    /// <summary>Signs in through the form; returns the sign-in POST response (302 on success).</summary>
    public async Task<HttpResponseMessage> SignInAsync(string email, string password)
    {
        var page = await SendAsync(HttpMethod.Get, "/Account/Login");
        var token = OmniClient.AntiforgeryToken(await page.Content.ReadAsStringAsync(_ctx.Ct))
                    ?? throw new TestFailure("The sign-in page has no antiforgery token.");
        var response = await SendAsync(HttpMethod.Post, "/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
        if ((int)response.StatusCode != 302) throw new TestFailure($"Raw sign-in as {email} answered {(int)response.StatusCode}.");
        return response;
    }

    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : response.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(", ", contentValues)
        : null;

    public void Dispose() => Http.Dispose();
}

/// <summary>
/// A minimal SignalR client for /hubs/inbox over WebSockets (JSON protocol): negotiate with the session's cookies,
/// handshake, invoke hub methods and record every server event. Enough to check who receives which events.
/// </summary>
internal sealed class HubProbe : IAsyncDisposable
{
    private const char Separator = '\u001e';
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _events = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly TestContext _ctx;
    private Task? _loop;
    private int _invocation;

    private HubProbe(TestContext ctx) => _ctx = ctx;

    /// <summary>Server events received so far, as "Target:arguments".</summary>
    public IReadOnlyList<string> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    /// <summary>True when the socket is still open (the server may close it).</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open;

    public static async Task<HubProbe> ConnectAsync(TestContext ctx, RawSession session)
    {
        var negotiate = await session.SendAsync(HttpMethod.Post, "/hubs/inbox/negotiate?negotiateVersion=1");
        var body = await negotiate.Content.ReadAsStringAsync(ctx.Ct);
        if (!negotiate.IsSuccessStatusCode) throw new TestFailure($"SignalR negotiate answered {(int)negotiate.StatusCode}.");
        var token = JsonNode.Parse(body)?["connectionToken"]?.ToString() ?? throw new TestFailure("SignalR negotiate returned no connection token.");

        var probe = new HubProbe(ctx);
        probe._socket.Options.Cookies = session.Cookies;
        var ws = new UriBuilder(new Uri(session.BaseUri, "hubs/inbox")) { Scheme = session.BaseUri.Scheme == "https" ? "wss" : "ws", Query = "id=" + Uri.EscapeDataString(token) };
        await probe._socket.ConnectAsync(ws.Uri, ctx.Ct);
        await probe.SendAsync("{\"protocol\":\"json\",\"version\":1}");
        var handshake = await probe.ReceiveFrameAsync(ctx.Ct);
        if (handshake.Contains("\"error\"", StringComparison.Ordinal)) throw new TestFailure($"SignalR handshake refused: {handshake}");
        probe._loop = Task.Run(probe.LoopAsync);
        ctx.Log("SignalR connected to /hubs/inbox.");
        return probe;
    }

    /// <summary>Invokes a hub method and waits for its completion (fails on a hub error).</summary>
    public async Task InvokeAsync(string target, params object[] arguments)
    {
        var id = Interlocked.Increment(ref _invocation).ToString();
        var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = done;
        await SendAsync(JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments }));
        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20), _ctx.Ct));
        if (finished != done.Task) throw new TestFailure($"Hub method {target} didn't complete within 20 s.");
        var completion = await done.Task;
        if (completion["error"] is { } error) throw new TestFailure($"Hub method {target} failed: {error}");
    }

    /// <summary>Waits until an event matching <paramref name="match"/> arrives (true) or the time is up (false).</summary>
    public async Task<bool> WaitForAsync(Func<string, bool> match, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Events.Any(match)) return true;
            await Task.Delay(250, _ctx.Ct);
        }
        return Events.Any(match);
    }

    private Task SendAsync(string json) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(json + Separator), WebSocketMessageType.Text, true, _ctx.Ct);

    private async Task<string> ReceiveFrameAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return text.ToString();
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage && text.ToString().EndsWith(Separator)) return text.ToString();
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var frame = await ReceiveFrameAsync(_stop.Token);
                foreach (var part in frame.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var message = JsonNode.Parse(part);
                    var type = message?["type"]?.GetValue<int>();
                    if (type == 1)
                    {
                        lock (_events) _events.Add($"{message!["target"]}:{message["arguments"]?.ToJsonString()}");
                    }
                    else if (type == 3 && message!["invocationId"]?.ToString() is { } id)
                    {
                        TaskCompletionSource<JsonNode>? done;
                        lock (_pending) _pending.Remove(id, out done);
                        done?.TrySetResult(message);
                    }
                    else if (type == 7)
                    {
                        lock (_events) _events.Add($"(closed by server):{message!.ToJsonString()}");
                    }
                }
                if (string.IsNullOrEmpty(frame)) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The run or the server ended the connection; the events so far stay readable.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Already gone.
        }
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
        _socket.Dispose();
        _stop.Dispose();
    }
}

