using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Omni.Automation.Http;

namespace Omni.Automation.Core;

/// <summary>A signed-in automation client: a throwaway business registered through the normal sign-up page.</summary>
public sealed class TestClient
{
    public required string Label { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required string FullName { get; init; }
    /// <summary>Business type picked at sign-up ("general" or "clinic").</summary>
    public required string BusinessType { get; init; }
    /// <summary>Cookie-authenticated HTTP client of the owner.</summary>
    public required OmniClient Http { get; init; }
    public Guid ClientId { get; set; }
    /// <summary>The first service, created right after sign-up (that is also how the client id is learned).</summary>
    public Guid BaseServiceId { get; set; }
}

/// <summary>
/// Everything a test can use: HTTP clients per identity, the run's test clients (registered lazily, once), shared
/// fixtures, the optional database and browser, webhook signers, and logging into the current test's result.
/// One context per run; tests run one at a time.
/// </summary>
public sealed partial class TestContext : IAsyncDisposable
{
    /// <summary>Must match the main app's AutomationClients — it only ever deletes clients with both.</summary>
    public const string ClientNamePrefix = "[Automation]";
    public const string EmailDomain = "omni-automation.test";

    private readonly List<TestClient> _testClients = new();
    private readonly List<OmniClient> _http = new();
    private readonly Dictionary<string, Task<TestClient>> _named = new(StringComparer.OrdinalIgnoreCase);
    private OmniClient? _anonymous;
    private OmniClient? _ingest;
    private OmniClient? _admin;
    private Db? _db;
    private Browser? _browser;

    public TestContext(TargetEnvironment env, RunState run, string runFolder)
    {
        Env = env;
        Run = run;
        RunFolder = runFolder;
        Tag = run.Id[..8];
        Fixtures = new Fixtures(this);
        Webhooks = new Webhooks(this);
    }

    public TargetEnvironment Env { get; }
    public RunState Run { get; }
    public CancellationToken Ct => Run.Cancellation.Token;

    /// <summary>Folder for this run's files (screenshots); the report links to them.</summary>
    public string RunFolder { get; }

    /// <summary>Short id of this run, put into every name the run creates so its data is easy to spot.</summary>
    public string Tag { get; }

    public Fixtures Fixtures { get; }

    /// <summary>Signed WhatsApp deliveries and fake Telegram updates.</summary>
    public Webhooks Webhooks { get; }

    internal TestResult? Current { get; set; }

    public void Log(string line)
    {
        lock (Run.Sync)
        {
            if (Current is not null) Current.Log.Add(line);
            else Run.RunLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
        }
    }

    /// <summary>Attaches a file in <see cref="RunFolder"/> (e.g. a screenshot) to the current test's result.</summary>
    public void Attach(string fileName, string? caption = null)
    {
        lock (Run.Sync) Current?.Attachments.Add(new Attachment(fileName, caption));
    }

    /// <summary>Waits for something asynchronous on the server (an AI reply, a worker) to become true.</summary>
    public async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, string what, TimeSpan? timeout = null, TimeSpan? every = null) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            if (await probe() is { } value) return value;
            if (DateTimeOffset.UtcNow > deadline) throw new TestFailure($"Timed out waiting for {what}.");
            await Task.Delay(every ?? TimeSpan.FromSeconds(2), Ct);
        }
    }

    // ---- HTTP identities ----------------------------------------------------------------------------------------

    public OmniClient Anonymous => _anonymous ??= NewHttp();

    /// <summary>Caller holding the ingest key (what n8n and the AI tools use).</summary>
    public OmniClient Ingest => _ingest ??= Env.HasIngestKey
        ? NewHttp(new Dictionary<string, string> { ["X-Ingest-Key"] = Env.IngestKey! })
        : throw new TestSkipped("No ingest key configured for this environment.");

    /// <summary>Caller holding the platform-admin key (what the admin portal uses).</summary>
    public OmniClient Admin => _admin ??= Env.HasAdminKey
        ? NewHttp(new Dictionary<string, string> { ["X-Platform-Admin-Key"] = Env.PlatformAdminKey!, ["X-Admin-Actor"] = "omni-automation" })
        : throw new TestSkipped("No platform-admin key configured for this environment.");

    /// <summary>The target's database (reads anywhere, writes only on [Automation] clients).</summary>
    public Db Db => _db ??= Env.HasDatabase
        ? new Db(Env.Database!, Log)
        : throw new TestSkipped("No database connection configured for this environment.");

    /// <summary>The Playwright browser for UI suites (launched on first use).</summary>
    public Browser Browser => _browser ??= new Browser(this);

    /// <summary>A fresh HTTP client with its own cookie jar (and optional fixed headers). Disposed with the run. Its
    /// sign-up/sign-in form posts skip the target's per-IP account limits (they carry the admin key) unless
    /// <paramref name="limited"/>: only the tests of those limits need them to apply.</summary>
    public OmniClient NewHttp(IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, bool limited = false)
    {
        var client = new OmniClient(Env.BaseUrl, Log, headers, timeout) { AccountFormHeaders = limited ? null : AccountLimitBypass };
        lock (_http) _http.Add(client);
        return client;
    }

    /// <summary>The header that lets this run's account form posts past the target's per-IP limits (null without the admin key).</summary>
    public IReadOnlyDictionary<string, string>? AccountLimitBypass => Env.HasAdminKey
        ? new Dictionary<string, string> { ["X-Platform-Admin-Key"] = Env.PlatformAdminKey! }
        : null;

    // ---- test clients -------------------------------------------------------------------------------------------

    /// <summary>The run's main test client (business type "general"), registered on first use. If registration fails,
    /// every test that needs it fails with the same reason.</summary>
    public Task<TestClient> ClientAsync() => NamedClientAsync("main", "general");

    /// <summary>A clinic-type test client shared by the run (for business-type wording and clinic-only stages).</summary>
    public Task<TestClient> ClinicClientAsync() => NamedClientAsync("clinic", "clinic");

    /// <summary>A second general client shared by the run, for isolation checks (one client must never see another's data).</summary>
    public Task<TestClient> OtherClientAsync() => NamedClientAsync("other", "general");

    /// <summary>The signed-in HTTP client of the main test client.</summary>
    public async Task<OmniClient> ApiAsync() => (await ClientAsync()).Http;

    /// <summary>A shared test client by label, registered once per run.</summary>
    public Task<TestClient> NamedClientAsync(string label, string businessType = "general")
    {
        lock (_named)
        {
            if (!_named.TryGetValue(label, out var task))
            {
                task = RegisterClientAsync(label, businessType);
                _named[label] = task;
            }
            return task;
        }
    }

    /// <summary>A client a test opened through the platform-admin API (no user yet, QA t6-admin-20): deleted with the run like
    /// the registered ones (the main app's cleanup accepts it while its invited owner's email is a test address).</summary>
    public void TrackOpenedClient(Guid clientId, string name, string ownerEmail)
    {
        var client = new TestClient
        {
            Label = "opened", Name = name, Email = ownerEmail, Password = "(none: the owner never joined)", FullName = "(invited owner)",
            BusinessType = "general", Http = NewHttp(), ClientId = clientId,
        };
        lock (_testClients) _testClients.Add(client);
    }

    /// <summary>Registers a new automation client through the sign-up page. It is deleted with the run.</summary>
    public async Task<TestClient> RegisterClientAsync(string label, string businessType = "general")
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var client = new TestClient
        {
            Label = label,
            Name = $"{ClientNamePrefix} {label} {Tag}",
            Email = $"auto-{Tag}-{label}-{suffix}@{EmailDomain}",
            Password = "Auto-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
            FullName = $"Automation {label}",
            BusinessType = businessType,
            Http = NewHttp(),
        };

        var response = await client.Http.SubmitFormAsync("/Account/Register", new Dictionary<string, string>
        {
            ["FullName"] = client.FullName,
            ["ClientName"] = client.Name,
            ["Email"] = client.Email,
            ["BusinessTypeCode"] = businessType,
            ["Password"] = client.Password,
            ["ConfirmPassword"] = client.Password,
        }, ct: Ct);
        if (response.Status != 302)
        {
            throw new TestFailure($"Registering test client \"{label}\" failed (status {response.Status}): {PageError(response.Body) ?? "no error shown"}.");
        }
        lock (_testClients) _testClients.Add(client);

        // The client id isn't exposed directly to a signed-in user; creating its first service returns it. On the trial plan
        // that only works while the services API ignores the plan (t2-billing-03); once it's gated, fall back to the admin
        // API to find the id, and leave BaseServiceId empty until a test moves the client to a plan with services.
        var service = await client.Http.PostAsync("/api/services", new
        {
            name = $"Consultation {Tag}",
            code = $"auto-{Tag}-{label}",
            description = "Created by Omni.Automation",
            bookingDuration = 30,
        }, ct: Ct);
        if (service.Status == 201)
        {
            client.ClientId = service.Id("clientId");
            client.BaseServiceId = service.Id();
        }
        else
        {
            client.ClientId = await FindClientIdAsync(client.Name)
                ?? throw new TestFailure($"Couldn't learn the id of test client \"{label}\": creating a service gave {service.Status} and the admin search found nothing.{service.Snippet(200)}");
            Run.Note($"Creating a first service for \"{label}\" gave {service.Status}; found its id through the admin API instead.");
        }

        lock (Run.Sync) Run.TestClients.Add($"{client.Name} ({client.ClientId}) — {client.Email}");
        Run.Note($"Registered test client \"{client.Name}\" ({client.ClientId}, {businessType}).");
        return client;
    }

    /// <summary>A client's id by its exact (unique, run-tagged) name through the admin clients search. Null without the admin key.</summary>
    private async Task<Guid?> FindClientIdAsync(string name)
    {
        if (!Env.HasAdminKey) return null;
        var response = await Admin.GetAsync($"/api/platform-admin/clients?search={Uri.EscapeDataString(name)}", ct: Ct);
        if (response.Status != 200) return null;
        var items = response.Json as System.Text.Json.Nodes.JsonArray ?? response.Json?["items"] as System.Text.Json.Nodes.JsonArray;
        var match = items?.FirstOrDefault(i => string.Equals(i?["name"]?.ToString(), name, StringComparison.Ordinal));
        return Guid.TryParse(match?["id"]?.ToString(), out var id) ? id : null;
    }

    /// <summary>Signs in on a fresh HTTP client. Returns the client and the sign-in response (302 = signed in; 200 = the
    /// page redisplayed with an error).</summary>
    public async Task<(OmniClient Http, ApiResponse Response)> SignInAsync(string email, string password)
    {
        var http = NewHttp();
        var response = await http.SubmitFormAsync("/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
        }, ct: Ct);
        return (http, response);
    }

    /// <summary>Moves a test client to a plan through the platform-admin API, without charging the first period.
    /// Needs the admin key; callers mark the test Risk.Billing.</summary>
    public async Task SetPlanAsync(TestClient client, string planCode)
    {
        (await Admin.PostAsync($"/api/platform-admin/billing/clients/{client.ClientId}/subscription",
            new { planCode, chargeFirstPeriod = false, reason = $"Omni.Automation {Tag}" },
            new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{Tag}-{client.ClientId:N}-{planCode}-{Guid.NewGuid():N}" }, Ct))
            .ExpectStatus(200, 201);
        Log($"Moved {client.Name} to plan {planCode}.");
    }

    /// <summary>The visible error message of a Razor page that redisplayed itself (e.g. a rejected form).</summary>
    public static string? PageError(string html)
    {
        var match = ErrorRegex().Match(html);
        if (!match.Success) return null;
        var start = html.IndexOf('>', match.Index) + 1;
        // The message comes first inside the alert; a short window with tags stripped is enough to show it.
        var window = html[start..Math.Min(html.Length, start + 500)];
        var text = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(window, " "));
        text = SpaceRegex().Replace(text, " ").Trim().TrimStart('!').Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    // ---- cleanup --------------------------------------------------------------------------------------------------

    /// <summary>Deletes every automation client this run registered (all their data goes with them).</summary>
    public async Task CleanupAsync(bool keepData)
    {
        List<TestClient> clients;
        lock (_testClients) clients = _testClients.ToList();

        foreach (var client in clients)
        {
            var label = $"\"{client.Name}\"";
            if (keepData)
            {
                Run.Note($"Kept test client {label} as asked. Sign in as {client.Email} / {client.Password} to look at it.");
                continue;
            }
            if (client.ClientId == Guid.Empty)
            {
                Run.Note($"Couldn't clean up {label}: its id is unknown. Use \"Delete leftover test clients\" with the admin key.");
                continue;
            }
            if (!Env.HasAdminKey)
            {
                Run.Note($"Left test client {label} behind: no platform-admin key is configured, so it can't be deleted.");
                continue;
            }

            try
            {
                var response = await Admin.DeleteAsync($"/api/platform-admin/automation/clients/{client.ClientId}", CancellationToken.None);
                Run.Note(response.Status switch
                {
                    204 => $"Deleted test client {label} and all its data.",
                    404 => $"Couldn't delete test client {label} (404): already gone, or the target lacks the automation cleanup endpoint.",
                    _ => $"Couldn't delete test client {label} (status {response.Status}).{response.Snippet(300)}",
                });
            }
            catch (Exception ex)
            {
                Run.Note($"Couldn't delete test client {label}: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        if (_db is not null) await _db.DisposeAsync();
        lock (_http)
        {
            foreach (var client in _http) client.Dispose();
            _http.Clear();
        }
    }

    [GeneratedRegex("<div[^>]*(?:role=\"alert\"|class=\"[^\"]*\\b(?:alert|au-alert)\\b[^\"]*\")[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex SpaceRegex();
}
