namespace Omni.Automation.Core;

/// <summary>What a test can affect beyond the throwaway test clients. Every flag is off by default for a run; a test
/// whose risk isn't allowed is skipped, never run.</summary>
[Flags]
public enum Risk
{
    None = 0,
    /// <summary>Calls a paid or third-party service (OpenAI embeddings, Meta, Telegram, scraping a real website, n8n).</summary>
    External = 1,
    /// <summary>Could deliver a message to a real phone or account.</summary>
    Messaging = 2,
    /// <summary>Writes billing records (wallet top-ups, plan changes, rate cards) — on test clients only, no real money moves.</summary>
    Billing = 4,
    /// <summary>Touches state every client shares (clearing the whole cache, switching a global engine setting and back).
    /// Harmless to data, but felt by everyone while it runs.</summary>
    Shared = 8,
    /// <summary>Runs the AI model (bot replies, benchmark generation): a few cents of OpenAI cost per test.</summary>
    Ai = 16,
}

/// <summary>Credentials or tools a test needs. Missing ones skip the test with a clear reason.</summary>
[Flags]
public enum Needs
{
    None = 0,
    /// <summary>A signed-in test client (registered on first use).</summary>
    Session = 1,
    /// <summary>The main app's N8n:IngestApiKey (AI tools, ingest, callbacks).</summary>
    IngestKey = 2,
    /// <summary>The main app's PlatformAdmin:ApiKey (admin portal endpoints, plan changes, cleanup).</summary>
    AdminKey = 4,
    /// <summary>The main app's Meta:AppSecret (signed WhatsApp webhook deliveries).</summary>
    MetaSecret = 8,
    /// <summary>A connection string to the target's database (setup without an API, row checks).</summary>
    Database = 16,
    /// <summary>A Playwright browser (UI suites). Installed once with the playwright script; see README.</summary>
    Browser = 32,
}

public sealed record TestCase(
    string Id,
    string SuiteId,
    string Name,
    string? Description,
    Risk Risk,
    Needs Needs,
    string? Finding,
    Func<TestContext, Task> Run,
    TimeSpan? Timeout = null);

public sealed record TestSuite(string Id, string Name, string Description, IReadOnlyList<TestCase> Tests);

public sealed class SuiteBuilder
{
    private readonly string _id;
    private readonly string _name;
    private readonly string _description;
    private readonly List<TestCase> _tests = new();

    public SuiteBuilder(string id, string name, string description)
    {
        _id = id;
        _name = name;
        _description = description;
    }

    /// <summary>Adds a test. <paramref name="finding"/> is the QA finding id it reproduces (e.g. "t4-ai-01", comma
    /// separated for several). Such tests assert the CORRECT behaviour, so they fail until the bug is fixed and the
    /// report shows which finding is still open. <paramref name="timeout"/> overrides the runner's default limit
    /// (e.g. a test that waits for a 5-minute background worker).</summary>
    public SuiteBuilder Test(string id, string name, Func<TestContext, Task> run,
        Needs needs = Needs.Session, Risk risk = Risk.None, string? description = null, string? finding = null, TimeSpan? timeout = null)
    {
        _tests.Add(new TestCase($"{_id}.{id}", _id, name, description, risk, needs, finding, run, timeout));
        return this;
    }

    public TestSuite Build() => new(_id, _name, _description, _tests);
}

/// <summary>Marks a static class in Omni.Automation.Suites as a suite. It must have
/// <c>public static TestSuite Build()</c>. Suites run in ascending <see cref="Order"/> (then by id).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SuiteAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>Thrown by assertions; the message is what the report shows.</summary>
public sealed class TestFailure(string message) : Exception(message);

/// <summary>Thrown to skip a test at run time (e.g. a prerequisite isn't configured on the target).</summary>
public sealed class TestSkipped(string reason) : Exception(reason);
