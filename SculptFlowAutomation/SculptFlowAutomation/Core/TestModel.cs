namespace SculptFlowAutomation.Core;

/// <summary>What a test can affect beyond the throwaway test clinic. Every flag is off by default for a run; a test
/// whose risk isn't allowed is skipped, never run.</summary>
[Flags]
public enum Risk
{
    None = 0,
    /// <summary>Calls a paid or third-party service (OpenAI embeddings, Meta, Telegram, scraping a real website).</summary>
    External = 1,
    /// <summary>Could deliver a message to a real phone or account.</summary>
    Messaging = 2,
    /// <summary>Writes billing records (wallet top-ups, plan changes) — on the test clinic only, no real money moves.</summary>
    Billing = 4,
    /// <summary>Touches state every clinic shares (e.g. clearing the whole cache). Harmless to data, but felt by everyone.</summary>
    Shared = 8,
}

/// <summary>Credentials a test needs. Missing ones skip the test with a clear reason.</summary>
[Flags]
public enum Needs
{
    None = 0,
    /// <summary>A signed-in test clinic (registered at the start of the run).</summary>
    Session = 1,
    /// <summary>The main app's N8n:IngestApiKey (n8n / AI-agent endpoints).</summary>
    IngestKey = 2,
    /// <summary>The main app's PlatformAdmin:ApiKey (admin portal endpoints).</summary>
    AdminKey = 4,
}

public sealed record TestCase(
    string Id,
    string SuiteId,
    string Name,
    string? Description,
    Risk Risk,
    Needs Needs,
    Func<TestContext, Task> Run);

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

    public SuiteBuilder Test(string id, string name, Func<TestContext, Task> run,
        Needs needs = Needs.Session, Risk risk = Risk.None, string? description = null)
    {
        _tests.Add(new TestCase($"{_id}.{id}", _id, name, description, risk, needs, run));
        return this;
    }

    public TestSuite Build() => new(_id, _name, _description, _tests);
}

/// <summary>Thrown by assertions; the message is what the report shows.</summary>
public sealed class TestFailure(string message) : Exception(message);

/// <summary>Thrown to skip a test at run time (e.g. a prerequisite isn't configured on the target).</summary>
public sealed class TestSkipped(string reason) : Exception(reason);
