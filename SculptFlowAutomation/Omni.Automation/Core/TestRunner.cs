using System.Diagnostics;

namespace Omni.Automation.Core;

/// <summary>Runs the selected tests one at a time, in catalog order, then deletes the run's test clients.</summary>
public sealed class TestRunner
{
    /// <summary>Per-test limit. AI tests wait for model replies (several runs of up to a minute), so they get longer.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AiTestTimeout = TimeSpan.FromMinutes(5);

    private readonly TestCatalog _catalog;
    private readonly string _filesFolder;

    public TestRunner(TestCatalog catalog, string filesFolder)
    {
        _catalog = catalog;
        _filesFolder = filesFolder;
    }

    /// <summary>Creates the run's state with every selected test listed as pending (unknown ids are ignored).</summary>
    public RunState Prepare(RunRequest request, TargetEnvironment env)
    {
        var selected = new HashSet<string>(request.TestIds, StringComparer.OrdinalIgnoreCase);
        var run = new RunState
        {
            Id = Guid.NewGuid().ToString("N"),
            Environment = request.Environment,
            BaseUrl = env.BaseUrl,
            Request = request,
        };
        foreach (var test in _catalog.AllTests.Where(t => selected.Contains(t.Id)))
        {
            run.Results.Add(new TestResult { Id = test.Id, SuiteId = test.SuiteId, Name = test.Name, Finding = test.Finding });
        }
        return run;
    }

    public async Task RunAsync(RunState run, TargetEnvironment env)
    {
        await using var ctx = new TestContext(env, run, _filesFolder);
        run.Note($"Run started against {run.Environment} ({env.BaseUrl}) with {run.Results.Count} tests.");
        lock (run.Sync) run.Phase = "running";

        try
        {
            foreach (var result in run.Results)
            {
                if (run.Cancellation.IsCancellationRequested)
                {
                    Finish(run, result, TestStatus.Skipped, "Run was stopped.", 0);
                    continue;
                }

                var test = _catalog.Find(result.Id)!;
                var skipReason = SkipReason(test, run.Request, env);
                if (skipReason is not null)
                {
                    Finish(run, result, TestStatus.Skipped, skipReason, 0);
                    continue;
                }

                await RunOneAsync(ctx, run, result, test);
            }
        }
        finally
        {
            lock (run.Sync) run.Phase = "cleaning up";
            ctx.Current = null;
            await ctx.CleanupAsync(run.Request.KeepTestData);
            lock (run.Sync)
            {
                run.Phase = run.Cancellation.IsCancellationRequested ? "stopped" : "finished";
                run.FinishedAt = DateTimeOffset.UtcNow;
            }
            var s = run.Snapshot();
            run.Note($"Done: {s.Passed} passed, {s.Failed} failed, {s.Skipped} skipped.");
        }
    }

    private static async Task RunOneAsync(TestContext ctx, RunState run, TestResult result, TestCase test)
    {
        lock (run.Sync) result.Status = TestStatus.Running;
        ctx.Current = result;
        var sw = Stopwatch.StartNew();
        var timeout = test.Timeout ?? (test.Risk.HasFlag(Risk.Ai) ? AiTestTimeout : TestTimeout);
        try
        {
            var work = test.Run(ctx);
            var finished = await Task.WhenAny(work, Task.Delay(timeout, run.Cancellation.Token));
            if (finished != work)
            {
                throw run.Cancellation.IsCancellationRequested
                    ? new TestSkipped("Run was stopped.")
                    : new TestFailure($"Timed out after {timeout.TotalSeconds:0} s.");
            }
            await work; // surfaces the test's own exception
            Finish(run, result, TestStatus.Passed, null, sw.ElapsedMilliseconds);
        }
        catch (TestSkipped ex)
        {
            Finish(run, result, TestStatus.Skipped, ex.Message, sw.ElapsedMilliseconds);
        }
        catch (TestFailure ex)
        {
            Finish(run, result, TestStatus.Failed, ex.Message, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Finish(run, result, TestStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        }
        finally
        {
            ctx.Current = null;
        }
    }

    private static void Finish(RunState run, TestResult result, TestStatus status, string? message, long ms)
    {
        lock (run.Sync)
        {
            result.Status = status;
            result.Message = message;
            result.DurationMs = ms;
        }
    }

    private static string? SkipReason(TestCase test, RunRequest request, TargetEnvironment env)
    {
        var blocked = test.Risk & ~request.AllowedRisks;
        if (blocked.HasFlag(Risk.Messaging)) return "Could message a real person. Allow \"messaging\" to run it.";
        if (blocked.HasFlag(Risk.Ai)) return "Runs the AI model (OpenAI cost). Allow \"AI\" to run it.";
        if (blocked.HasFlag(Risk.External)) return "Calls a paid or third-party service. Allow \"external services\" to run it.";
        if (blocked.HasFlag(Risk.Billing)) return "Writes billing records. Allow \"billing writes\" to run it.";
        if (blocked.HasFlag(Risk.Shared)) return "Touches state shared by every client. Allow \"shared-state actions\" to run it.";
        if (test.Needs.HasFlag(Needs.IngestKey) && !env.HasIngestKey) return "No ingest key configured for this environment.";
        if (test.Needs.HasFlag(Needs.AdminKey) && !env.HasAdminKey) return "No platform-admin key configured for this environment.";
        if (test.Needs.HasFlag(Needs.MetaSecret) && !env.HasMetaSecret) return "No Meta app secret configured for this environment.";
        if (test.Needs.HasFlag(Needs.Database) && !env.HasDatabase) return "No database connection configured for this environment.";
        return null;
    }
}
