using SculptFlowAutomation.Suites;

namespace SculptFlowAutomation.Core;

/// <summary>Every suite, in the order a full run executes them. Add a new suite here.</summary>
public sealed class TestCatalog
{
    private readonly Dictionary<string, TestCase> _byId;

    public TestCatalog()
    {
        Suites = new[]
        {
            HealthSuite.Build(),
            AuthSuite.Build(),
            SecuritySuite.Build(),
            PagesSuite.Build(),
            StaffSuite.Build(),
            ProceduresSuite.Build(),
            LeadsSuite.Build(),
            AvailabilitySuite.Build(),
            AppointmentsSuite.Build(),
            ProcedureBookingsSuite.Build(),
            InboxSuite.Build(),
            NotificationsSuite.Build(),
            DashboardSuite.Build(),
            KnowledgeSuite.Build(),
            WebsitesSuite.Build(),
            CampaignsSuite.Build(),
            WhatsAppSuite.Build(),
            ChannelsSuite.Build(),
            CalendarSuite.Build(),
            BillingSuite.Build(),
            AiAgentSuite.Build(),
            BenchmarkSuite.Build(),
            CallbacksSuite.Build(),
            OAuthSuite.Build(),
            ClinicEdgeCasesSuite.Build(),
            PlatformAdminSuite.Build(),
            AdminBillingSuite.Build(),
            AdminClinicsSuite.Build(),
            AdminStaffSuite.Build(),
            AdminLeadsSuite.Build(),
            AdminContentSuite.Build(),
            AdminChannelsSuite.Build(),
            AdminOverviewSuite.Build(),
            AdminSettingsSuite.Build(),
            AdminCacheSuite.Build(),
            JourneySuite.Build(),
        };
        _byId = AllTests.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TestSuite> Suites { get; }

    public IEnumerable<TestCase> AllTests => Suites.SelectMany(s => s.Tests);

    public TestCase? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Expands a mix of suite ids ("leads"), test ids ("leads.create") and "all" into test ids.</summary>
    public IReadOnlyList<string> Expand(IEnumerable<string> selectors)
    {
        var ids = new List<string>();
        foreach (var raw in selectors.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) ids.AddRange(AllTests.Select(t => t.Id));
            else if (Suites.FirstOrDefault(s => s.Id.Equals(raw, StringComparison.OrdinalIgnoreCase)) is { } suite) ids.AddRange(suite.Tests.Select(t => t.Id));
            else if (_byId.ContainsKey(raw)) ids.Add(raw);
            else throw new ArgumentException($"Unknown suite or test \"{raw}\".");
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
