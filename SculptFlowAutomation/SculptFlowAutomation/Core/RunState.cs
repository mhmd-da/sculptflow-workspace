namespace SculptFlowAutomation.Core;

public enum TestStatus { Pending, Running, Passed, Failed, Skipped }

public sealed class TestResult
{
    public required string Id { get; init; }
    public required string SuiteId { get; init; }
    public required string Name { get; init; }
    public TestStatus Status { get; set; } = TestStatus.Pending;
    public long DurationMs { get; set; }
    public string? Message { get; set; }
    public List<string> Log { get; } = new();
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
    public string? TestClinic { get; set; }
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
                Id, Environment, BaseUrl, StartedAt, FinishedAt, Phase, TestClinic,
                Request.AllowExternal, Request.AllowMessaging, Request.AllowBilling, Request.KeepTestData,
                Results.Select(r => new TestResultSnapshot(r.Id, r.SuiteId, r.Name, r.Status.ToString().ToLowerInvariant(),
                    r.DurationMs, r.Message, r.Log.ToList())).ToList(),
                RunLog.ToList(),
                Results.Count(r => r.Status == TestStatus.Passed),
                Results.Count(r => r.Status == TestStatus.Failed),
                Results.Count(r => r.Status == TestStatus.Skipped),
                Results.Count);
        }
    }
}

public sealed record TestResultSnapshot(string Id, string SuiteId, string Name, string Status, long DurationMs, string? Message, List<string> Log);

public sealed record RunSnapshot(
    string Id, string Environment, string BaseUrl, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Phase,
    string? TestClinic, bool AllowExternal, bool AllowMessaging, bool AllowBilling, bool KeepTestData,
    List<TestResultSnapshot> Results, List<string> RunLog, int Passed, int Failed, int Skipped, int Total);
