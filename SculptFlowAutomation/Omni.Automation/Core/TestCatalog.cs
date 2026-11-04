using System.Reflection;

namespace Omni.Automation.Core;

/// <summary>Every suite, in the order a full run executes them. Suites are found by reflection: any static class marked
/// [Suite(order)] with <c>public static TestSuite Build()</c>. Add a class; nothing to register.</summary>
public sealed class TestCatalog
{
    private readonly Dictionary<string, TestCase> _byId;

    public TestCatalog()
    {
        Suites = typeof(TestCatalog).Assembly.GetTypes()
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<SuiteAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Attr!.Order, Suite: Build(x.Type)))
            .OrderBy(x => x.Order).ThenBy(x => x.Suite.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Suite)
            .ToList();

        var duplicate = AllTests.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate test id \"{duplicate.Key}\".");
        _byId = AllTests.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TestSuite> Suites { get; }

    public IEnumerable<TestCase> AllTests => Suites.SelectMany(s => s.Tests);

    public TestCase? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Expands a mix of suite ids ("contacts"), test ids ("contacts.create"), finding ids ("t4-ai-01"),
    /// "findings" (every test that reproduces a QA finding) and "all" into test ids.</summary>
    public IReadOnlyList<string> Expand(IEnumerable<string> selectors)
    {
        var ids = new List<string>();
        foreach (var raw in selectors.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) ids.AddRange(AllTests.Select(t => t.Id));
            else if (raw.Equals("findings", StringComparison.OrdinalIgnoreCase)) ids.AddRange(AllTests.Where(t => t.Finding is not null).Select(t => t.Id));
            else if (Suites.FirstOrDefault(s => s.Id.Equals(raw, StringComparison.OrdinalIgnoreCase)) is { } suite) ids.AddRange(suite.Tests.Select(t => t.Id));
            else if (_byId.ContainsKey(raw)) ids.Add(raw);
            else if (AllTests.Where(t => HasFinding(t, raw)).Select(t => t.Id).ToList() is { Count: > 0 } byFinding) ids.AddRange(byFinding);
            else throw new ArgumentException($"Unknown suite, test or finding \"{raw}\".");
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool HasFinding(TestCase test, string finding) =>
        test.Finding?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(finding, StringComparer.OrdinalIgnoreCase) == true;

    private static TestSuite Build(Type type)
    {
        var method = type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
            ?? throw new InvalidOperationException($"Suite {type.Name} has no public static Build().");
        return (TestSuite)method.Invoke(null, null)!;
    }
}
