namespace Omni.Automation.Core;

public enum TestStatus { Pending, Running, Passed, Failed, Skipped }

/// <summary>A file in the runs/files folder (a screenshot) attached to a test result.</summary>
public sealed record Attachment(string File, string? Caption);

public sealed class TestResult
{
    public required string Id { get; init; }
    public required string SuiteId { get; init; }
    public required string Name { get; init; }
    /// <summary>QA finding id(s) this test reproduces, if any.</summary>
    public string? Finding { get; init; }
    public TestStatus Status { get; set; } = TestStatus.Pending;
    public long DurationMs { get; set; }
    public string? Message { get; set; }
    public List<string> Log { get; } = new();
    public List<Attachment> Attachments { get; } = new();
}

/// <summary>A run in progress or finished. Mutated only under <see cref="Sync"/>; readers take a snapshot.</summary>
public sealed class RunState
{
    public object Sync { get; } = new();

    public required string Id { get; init; }
    public required string Environment { get; init; }
    public required string BaseUrl { get; init; }
    public required RunRequest Request { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Phase { get; set; } = "starting";
    /// <summary>"name (id) — email" of every test client the run registered.</summary>
    public List<string> TestClients { get; } = new();
    public List<TestResult> Results { get; } = new();
    public List<string> RunLog { get; } = new();
    public CancellationTokenSource Cancellation { get; } = new();

    public void Note(string line)
    {
        lock (Sync) RunLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
    }

    public RunSnapshot Snapshot()
    {
        lock (Sync)
        {
            return new RunSnapshot(
                Id, Environment, BaseUrl, StartedAt, FinishedAt, Phase, TestClients.ToList(),
                Request.AllowExternal, Request.AllowMessaging, Request.AllowBilling, Request.AllowShared, Request.AllowAi, Request.KeepTestData,
                Results.Select(r => new TestResultSnapshot(r.Id, r.SuiteId, r.Name, r.Finding, r.Status.ToString().ToLowerInvariant(),
                    r.DurationMs, r.Message, r.Log.ToList(), r.Attachments.ToList())).ToList(),
                RunLog.ToList(),
                Results.Count(r => r.Status == TestStatus.Passed),
                Results.Count(r => r.Status == TestStatus.Failed),
                Results.Count(r => r.Status == TestStatus.Skipped),
                Results.Count);
        }
    }
}

public sealed record TestResultSnapshot(string Id, string SuiteId, string Name, string? Finding, string Status, long DurationMs,
    string? Message, List<string> Log, List<Attachment> Attachments);

public sealed record RunSnapshot(
    string Id, string Environment, string BaseUrl, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Phase,
    List<string> TestClients, bool AllowExternal, bool AllowMessaging, bool AllowBilling, bool AllowShared, bool AllowAi, bool KeepTestData,
    List<TestResultSnapshot> Results, List<string> RunLog, int Passed, int Failed, int Skipped, int Total);
