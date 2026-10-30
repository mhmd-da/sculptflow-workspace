using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SculptFlowAutomation.Http;

namespace SculptFlowAutomation.Core;

/// <summary>A signed-in automation clinic: a throwaway clinic registered through the normal sign-up page.</summary>
public sealed class TestClinic
{
    public required string Label { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required string FullName { get; init; }
    public required SculptFlowClient Client { get; init; }
    public Guid ClinicId { get; set; }
    public Guid BaseProcedureId { get; set; }
}

/// <summary>
/// Everything a test can use: HTTP clients per identity, the run's test clinic (registered lazily, once), shared
/// fixtures, and logging into the current test's result. One context per run; tests run one at a time.
/// </summary>
public sealed partial class TestContext : IAsyncDisposable
{
    /// <summary>Must match the main app's AutomationAdminController — it only ever deletes clinics with both.</summary>
    public const string ClinicNamePrefix = "[Automation]";
    public const string EmailDomain = "sculptflow-automation.test";

    private readonly List<TestClinic> _clinics = new();
    private readonly List<SculptFlowClient> _clients = new();
    private Task<TestClinic>? _mainClinic;
    private SculptFlowClient? _anonymous;
    private SculptFlowClient? _ingest;
    private SculptFlowClient? _admin;

    public TestContext(TargetEnvironment env, RunState run)
    {
        Env = env;
        Run = run;
        Tag = run.Id[..8];
        Fixtures = new Fixtures(this);
    }

    public TargetEnvironment Env { get; }
    public RunState Run { get; }
    public CancellationToken Ct => Run.Cancellation.Token;

    /// <summary>Short id of this run, put into every name the run creates so its data is easy to spot.</summary>
    public string Tag { get; }

    public Fixtures Fixtures { get; }

    internal TestResult? Current { get; set; }

    public void Log(string line)
    {
        lock (Run.Sync)
        {
            if (Current is not null) Current.Log.Add(line);
            else Run.RunLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
        }
    }

    // ---- clients ------------------------------------------------------------------------------------------------

    public SculptFlowClient Anonymous => _anonymous ??= NewClient();

    /// <summary>Caller holding the ingest key (what n8n / the AI agent use).</summary>
    public SculptFlowClient Ingest => _ingest ??= Env.HasIngestKey
        ? NewClient(new Dictionary<string, string> { ["X-Ingest-Key"] = Env.IngestKey! })
        : throw new TestSkipped("No ingest key configured for this environment.");

    /// <summary>Caller holding the platform-admin key (what the admin portal uses).</summary>
    public SculptFlowClient Admin => _admin ??= Env.HasAdminKey
        ? NewClient(new Dictionary<string, string> { ["X-Platform-Admin-Key"] = Env.PlatformAdminKey!, ["X-Admin-Actor"] = "automation-tester" })
        : throw new TestSkipped("No platform-admin key configured for this environment.");

    /// <summary>A fresh client with its own cookie jar (and optional fixed headers). Disposed with the run.</summary>
    public SculptFlowClient NewClient(IReadOnlyDictionary<string, string>? headers = null)
    {
        var client = new SculptFlowClient(Env.BaseUrl, Log, headers);
        lock (_clients) _clients.Add(client);
        return client;
    }

    // ---- test clinic ----------------------------------------------------------------------------------------------

    /// <summary>The run's main test clinic, registered on first use. If registration fails, every test that needs it
    /// fails with the same reason.</summary>
    public Task<TestClinic> ClinicAsync() => _mainClinic ??= RegisterClinicAsync("main");

    /// <summary>The signed-in client of the main test clinic.</summary>
    public async Task<SculptFlowClient> ApiAsync() => (await ClinicAsync()).Client;

    /// <summary>Registers another automation clinic (e.g. to prove one clinic can't see another's data).
    /// It is deleted with the run like the main one.</summary>
    public async Task<TestClinic> RegisterClinicAsync(string label)
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var clinic = new TestClinic
        {
            Label = label,
            Name = $"{ClinicNamePrefix} {label} {Tag}",
            Email = $"auto-{Tag}-{label}-{suffix}@{EmailDomain}",
            Password = "Auto-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
            FullName = $"Automation {label}",
            Client = NewClient(),
        };

        var response = await clinic.Client.SubmitFormAsync("/Account/Register", new Dictionary<string, string>
        {
            ["FullName"] = clinic.FullName,
            ["ClinicName"] = clinic.Name,
            ["Email"] = clinic.Email,
            ["Password"] = clinic.Password,
            ["ConfirmPassword"] = clinic.Password,
        }, ct: Ct);
        if (response.Status != 302)
        {
            throw new TestFailure($"Registering the test clinic failed (status {response.Status}): {PageError(response.Body) ?? "no error shown"}.");
        }
        lock (_clinics) _clinics.Add(clinic);

        // The clinic id isn't exposed directly to a signed-in user; creating its first procedure returns it.
        var procedure = (await clinic.Client.PostAsync("/api/procedures", new
        {
            name = $"Consultation {Tag}",
            code = $"auto-{Tag}",
            description = "Created by SculptFlowAutomation",
            consultationDuration = 30,
        }, ct: Ct)).ExpectStatus(201);
        clinic.ClinicId = procedure.Id("clinicId");
        clinic.BaseProcedureId = procedure.Id();

        lock (Run.Sync) Run.TestClinic ??= $"{clinic.Name} ({clinic.ClinicId})";
        Run.Note($"Registered test clinic \"{clinic.Name}\" ({clinic.ClinicId}).");
        return clinic;
    }

    /// <summary>Signs in on a fresh client. Returns the client and the sign-in response (302 = signed in; 200 = the page
    /// redisplayed with an error).</summary>
    public async Task<(SculptFlowClient Client, ApiResponse Response)> SignInAsync(string email, string password)
    {
        var client = NewClient();
        var response = await client.SubmitFormAsync("/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
        }, ct: Ct);
        return (client, response);
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

    /// <summary>Deletes every automation clinic this run registered (all their data goes with them).</summary>
    public async Task CleanupAsync(bool keepData)
    {
        List<TestClinic> clinics;
        lock (_clinics) clinics = _clinics.ToList();
        if (clinics.Count == 0) return;

        foreach (var clinic in clinics)
        {
            var label = $"\"{clinic.Name}\"";
            if (keepData)
            {
                Run.Note($"Kept test clinic {label} as asked. Sign in as {clinic.Email} to look at it.");
                continue;
            }
            if (clinic.ClinicId == Guid.Empty)
            {
                Run.Note($"Couldn't clean up {label}: its id is unknown. Use \"Delete leftover test clinics\" with the admin key.");
                continue;
            }
            if (!Env.HasAdminKey)
            {
                Run.Note($"Left test clinic {label} behind: no platform-admin key is configured, so it can't be deleted.");
                continue;
            }

            try
            {
                var response = await Admin.DeleteAsync($"/api/platform-admin/automation/clinics/{clinic.ClinicId}", CancellationToken.None);
                Run.Note(response.Status switch
                {
                    204 => $"Deleted test clinic {label} and all its data.",
                    404 => $"Couldn't delete test clinic {label} (404). The target may not have the automation cleanup endpoint deployed yet.",
                    _ => $"Couldn't delete test clinic {label} (status {response.Status}).{response.Snippet(300)}",
                });
            }
            catch (Exception ex)
            {
                Run.Note($"Couldn't delete test clinic {label}: {ex.Message}");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_clients)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex("<div[^>]*(?:role=\"alert\"|class=\"[^\"]*\\balert\\b[^\"]*\")[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex SpaceRegex();
}
