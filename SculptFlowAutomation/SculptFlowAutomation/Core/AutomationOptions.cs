namespace SculptFlowAutomation.Core;

/// <summary>The "Automation" config section. Keys are secrets and come from user-secrets or env vars only.</summary>
public sealed class AutomationOptions
{
    public string DefaultEnvironment { get; set; } = "local";
    public string RunsFolder { get; set; } = "runs";
    public Dictionary<string, TargetEnvironment> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetEnvironment
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The main app's N8n:IngestApiKey (header X-Ingest-Key).</summary>
    public string? IngestKey { get; set; }

    /// <summary>The main app's PlatformAdmin:ApiKey (header X-Platform-Admin-Key). Also what lets a run delete its
    /// test clinic afterwards — without it the clinic is left behind and the report says so.</summary>
    public string? PlatformAdminKey { get; set; }

    /// <summary>The main app's Meta:WebhookVerifyToken, for the WhatsApp webhook handshake check (optional).</summary>
    public string? MetaWebhookVerifyToken { get; set; }

    /// <summary>The main app's Meta:AppSecret, used to sign fake WhatsApp webhook deliveries (X-Hub-Signature-256).
    /// Optional: without it the signed-delivery test is skipped and only the "unsigned is refused" check runs.</summary>
    public string? MetaAppSecret { get; set; }

    public bool HasIngestKey => !string.IsNullOrWhiteSpace(IngestKey);
    public bool HasAdminKey => !string.IsNullOrWhiteSpace(PlatformAdminKey);
}

/// <summary>What the person picked for one run.</summary>
public sealed record RunRequest(
    string Environment,
    IReadOnlyList<string> TestIds,
    bool AllowExternal = false,
    bool AllowMessaging = false,
    bool AllowBilling = false,
    bool KeepTestData = false,
    bool AllowShared = false)
{
    public Risk AllowedRisks =>
        (AllowExternal ? Risk.External : Risk.None)
        | (AllowMessaging ? Risk.Messaging : Risk.None)
        | (AllowBilling ? Risk.Billing : Risk.None)
        | (AllowShared ? Risk.Shared : Risk.None);
}
