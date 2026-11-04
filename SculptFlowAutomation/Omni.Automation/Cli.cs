using Microsoft.Extensions.Configuration;
using Omni.Automation.Core;

namespace Omni.Automation;

/// <summary>Command-line mode: same tests and reports as the web page, exit code 1 when anything fails.</summary>
public static class Cli
{
    public static readonly string[] Commands = { "run", "list", "cleanup", "help" };

    public const string Usage = """
        Omni automation tester

          dotnet run -- list [--findings]
              Lists every suite and test id (--findings: only tests that reproduce a QA finding, with the finding id).

          dotnet run -- run [--env staging] [--tests all] [--allow external,ai,billing,shared,messaging] [--keep]
              Runs tests. --tests takes suite ids, test ids, QA finding ids (t4-ai-01), "findings" and/or "all",
              comma separated (default: all). --allow lets risky tests run (they are skipped otherwise).
              --keep keeps the test clients afterwards (the report prints their logins).

          dotnet run -- cleanup [--env staging]
              Deletes test clients left behind by crashed or stopped runs (needs the platform-admin key).

        With no command, it starts the web page at http://localhost:5290.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(typeof(Cli).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = config.GetSection("Automation").Get<AutomationOptions>() ?? new AutomationOptions();
        var catalog = new TestCatalog();
        var command = args[0].ToLowerInvariant();
        var envName = Option(args, "--env") ?? options.DefaultEnvironment;

        if (command == "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (command == "list")
        {
            var onlyFindings = args.Contains("--findings", StringComparer.OrdinalIgnoreCase);
            foreach (var suite in catalog.Suites)
            {
                var tests = suite.Tests.Where(t => !onlyFindings || t.Finding is not null).ToList();
                if (tests.Count == 0) continue;
                Console.WriteLine($"{suite.Id,-18} {suite.Name}");
                foreach (var test in tests)
                {
                    var tags = string.Join(" ", new[]
                    {
                        test.Finding is null ? null : $"[QA {test.Finding}]",
                        test.Risk != Risk.None ? $"[{test.Risk}]" : null,
                        test.Needs.HasFlag(Needs.IngestKey) ? "[ingest key]" : null,
                        test.Needs.HasFlag(Needs.AdminKey) ? "[admin key]" : null,
                        test.Needs.HasFlag(Needs.MetaSecret) ? "[meta secret]" : null,
                        test.Needs.HasFlag(Needs.Database) ? "[database]" : null,
                        test.Needs.HasFlag(Needs.Browser) ? "[browser]" : null,
                    }.Where(t => t is not null));
                    Console.WriteLine($"   {test.Id,-46} {test.Name} {tags}");
                }
            }
            Console.WriteLine($"\n{catalog.AllTests.Count()} tests in {catalog.Suites.Count} suites; {catalog.AllTests.Count(t => t.Finding is not null)} reproduce QA findings.");
            return 0;
        }

        if (!options.Environments.TryGetValue(envName, out var env) || string.IsNullOrWhiteSpace(env.BaseUrl))
        {
            Console.Error.WriteLine($"Unknown environment \"{envName}\". Configured: {string.Join(", ", options.Environments.Keys)}");
            return 2;
        }

        if (command == "cleanup")
        {
            if (!env.HasAdminKey)
            {
                Console.Error.WriteLine("No platform-admin key configured for this environment.");
                return 2;
            }
            foreach (var line in await Cleanup.SweepAsync(env)) Console.WriteLine(line);
            return 0;
        }

        IReadOnlyList<string> testIds;
        try
        {
            testIds = catalog.Expand((Option(args, "--tests") ?? "all").Split(','));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var allow = (Option(args, "--allow") ?? string.Empty).ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries);
        var request = new RunRequest(envName, testIds,
            AllowExternal: allow.Contains("external"),
            AllowMessaging: allow.Contains("messaging"),
            AllowBilling: allow.Contains("billing"),
            KeepTestData: args.Contains("--keep", StringComparer.OrdinalIgnoreCase),
            AllowShared: allow.Contains("shared"),
            AllowAi: allow.Contains("ai"));

        var store = new RunStore(Path.Combine(Directory.GetCurrentDirectory(), options.RunsFolder));
        var runner = new TestRunner(catalog, store.FilesFolder);
        var run = runner.Prepare(request, env);
        Console.WriteLine($"Running {run.Results.Count} tests against {envName} ({env.BaseUrl})…");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; run.Cancellation.Cancel(); };

        var printed = new HashSet<string>();
        var watch = Task.Run(async () =>
        {
            while (true)
            {
                Print(run, printed);
                if (run.FinishedAt is not null) break;
                await Task.Delay(300);
            }
        });
        await runner.RunAsync(run, env);
        await watch;

        var snapshot = run.Snapshot();
        foreach (var line in snapshot.RunLog) Console.WriteLine("  " + line);
        var report = store.Save(snapshot);
        Console.WriteLine($"\n{snapshot.Passed} passed, {snapshot.Failed} failed, {snapshot.Skipped} skipped. Report: {report}");
        return snapshot.Failed > 0 ? 1 : 0;
    }

    private static void Print(RunState run, HashSet<string> printed)
    {
        foreach (var r in run.Snapshot().Results.Where(r => r.Status is "passed" or "failed" or "skipped"))
        {
            if (!printed.Add(r.Id)) continue;
            var (mark, color) = r.Status switch
            {
                "passed" => ("PASS", ConsoleColor.Green),
                "failed" => ("FAIL", ConsoleColor.Red),
                _ => ("SKIP", ConsoleColor.DarkGray),
            };
            Console.ForegroundColor = color;
            Console.Write($"{mark} ");
            Console.ResetColor();
            var finding = r.Finding is null ? "" : $" [QA {r.Finding}]";
            Console.WriteLine($"{r.Id}{finding} ({r.DurationMs} ms){(r.Message is null ? "" : " — " + r.Message)}");
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
