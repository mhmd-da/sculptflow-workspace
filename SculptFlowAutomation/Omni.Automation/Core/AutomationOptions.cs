namespace Omni.Automation.Core;

/// <summary>The "Automation" config section. Keys are secrets and come from user-secrets or env vars only.</summary>
public sealed class AutomationOptions
{
    public string DefaultEnvironment { get; set; } = "staging";
    public string RunsFolder { get; set; } = "runs";
    public Dictionary<string, TargetEnvironment> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetEnvironment
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The main app's N8n:IngestApiKey (header X-Ingest-Key): AI tools, message ingest, provider callbacks.</summary>
    public string? IngestKey { get; set; }

    /// <summary>The main app's PlatformAdmin:ApiKey (header X-Platform-Admin-Key). Also what lets a run delete its
    /// test clients afterwards — without it they are left behind and the report says so.</summary>
    public string? PlatformAdminKey { get; set; }

    /// <summary>The main app's Meta:WebhookVerifyToken, for the WhatsApp webhook handshake check.</summary>
    public string? MetaWebhookVerifyToken { get; set; }

    /// <summary>The main app's Meta:AppSecret, used to sign simulated WhatsApp webhook deliveries (X-Hub-Signature-256)
    /// exactly as Meta does. Without it every signed-delivery test is skipped.</summary>
    public string? MetaAppSecret { get; set; }

    /// <summary>Optional Npgsql connection string to the target's database. Enables Needs.Database tests: setup that has no
    /// API (a fake channel connection, moving a 24-hour window or trial end into the past) and checks of rows the API
    /// doesn't show (usage records, traces, logs). Writes are only ever allowed on [Automation] clients.</summary>
    public string? Database { get; set; }

    public bool HasIngestKey => !string.IsNullOrWhiteSpace(IngestKey);
    public bool HasAdminKey => !string.IsNullOrWhiteSpace(PlatformAdminKey);
    public bool HasMetaSecret => !string.IsNullOrWhiteSpace(MetaAppSecret);
    public bool HasDatabase => !string.IsNullOrWhiteSpace(Database);
}

/// <summary>What the person picked for one run.</summary>
public sealed record RunRequest(
    string Environment,
    IReadOnlyList<string> TestIds,
    bool AllowExternal = false,
    bool AllowMessaging = false,
    bool AllowBilling = false,
    bool KeepTestData = false,
    bool AllowShared = false,
    bool AllowAi = false)
{
    public Risk AllowedRisks =>
        (AllowExternal ? Risk.External : Risk.None)
        | (AllowMessaging ? Risk.Messaging : Risk.None)
        | (AllowBilling ? Risk.Billing : Risk.None)
        | (AllowShared ? Risk.Shared : Risk.None)
        | (AllowAi ? Risk.Ai : Risk.None);
}
