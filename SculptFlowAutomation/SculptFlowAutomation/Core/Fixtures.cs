using System.Text.Json.Nodes;

namespace SculptFlowAutomation.Core;

/// <summary>
/// Shared test data in the run's test clinic, created on first use so any selection of tests works on its own.
/// Each getter is cached for the run; the New* methods always create a fresh record.
/// </summary>
public sealed class Fixtures
{
    private readonly TestContext _ctx;
    private Task<Guid>? _lead;
    private Task? _availability;
    private int _phoneCounter;

    public Fixtures(TestContext ctx) => _ctx = ctx;

    /// <summary>A shared lead in the test clinic (procedure = the clinic's base procedure).</summary>
    public Task<Guid> LeadAsync() => _lead ??= NewLeadAsync("shared");

    /// <summary>Creates a lead through the intake endpoint, the same path n8n uses (it needs the ingest key).</summary>
    public async Task<Guid> NewLeadAsync(string label)
    {
        var clinic = await _ctx.ClinicAsync();
        var response = (await _ctx.Ingest.PostAsync("/api/leads", new
        {
            clinicId = clinic.ClinicId,
            procedureId = clinic.BaseProcedureId,
            fullName = $"Auto {label} {_ctx.Tag}",
            firstName = "Auto",
            lastName = $"{label} {_ctx.Tag}",
            phone = NewPhone(),
            email = $"lead-{label}-{_ctx.Tag}@{TestContext.EmailDomain}",
            source = "automation",
            sourceDetail = "SculptFlowAutomation",
            externalLeadId = $"auto-{_ctx.Tag}-{label}-{Guid.NewGuid():N}",
            preferredLanguage = "en",
        }, ct: _ctx.Ct)).ExpectStatus(201);
        return response.Id();
    }

    /// <summary>A phone number in a fictional range (+1 555 01xx is reserved for fiction), unique within the run.</summary>
    public string NewPhone() => $"+1555010{Interlocked.Increment(ref _phoneCounter) % 100:00}";

    /// <summary>Opens the test clinic every day 06:00-22:00 UTC with no minimum notice, so slots always exist.</summary>
    public Task EnsureAvailabilityAsync() => _availability ??= SaveAvailabilityAsync();

    private async Task SaveAvailabilityAsync()
    {
        var api = await _ctx.ApiAsync();
        var fields = new List<KeyValuePair<string, string>>
        {
            new("TimezoneId", "UTC"),
            new("DefaultDurationMinutes", "30"),
            new("BufferMinutes", "0"),
            new("NoticeHours", "0"),
            new("MaxAdvanceDays", "60"),
        };
        for (var day = 0; day < 7; day++)
        {
            fields.Add(new($"Days[{day}].DayOfWeek", day.ToString()));
            fields.Add(new($"Days[{day}].IsOpen", "true"));
            fields.Add(new($"Days[{day}].Start", "06:00"));
            fields.Add(new($"Days[{day}].End", "22:00"));
        }

        var response = await api.SubmitFormAsync("/settings/clinic-info", fields, handler: "SaveAvailability", ct: _ctx.Ct);
        if (response.Status != 302)
        {
            throw new TestFailure($"Saving opening hours failed (status {response.Status}): {TestContext.PageError(response.Body) ?? "no error shown"}.");
        }
    }

    /// <summary>Open consultation slots from the staff availability endpoint (opening hours are set up first).</summary>
    public async Task<JsonArray> SlotsAsync(int days = 14)
    {
        await EnsureAvailabilityAsync();
        var api = await _ctx.ApiAsync();
        var clinic = await _ctx.ClinicAsync();
        var response = (await api.GetAsync($"/api/appointments/available?procedureId={clinic.BaseProcedureId}&days={days}", ct: _ctx.Ct)).ExpectOk();
        var slots = response.Array("slots");
        if (slots.Count == 0)
        {
            throw new TestFailure($"No open slots in the next {days} days even though the clinic is open every day.{response.Snippet()}");
        }
        return slots;
    }

    /// <summary>Start time of the n-th open slot, as the server formatted it.</summary>
    public async Task<string> SlotStartAsync(int index = 0)
    {
        var slots = await SlotsAsync();
        return slots[Math.Min(index, slots.Count - 1)]!["start"]!.ToString();
    }

    /// <summary>A new inbox conversation for a lead (website channel, so nothing is ever delivered anywhere).</summary>
    public async Task<Guid> NewConversationAsync(Guid leadId)
    {
        var api = await _ctx.ApiAsync();
        var response = (await api.PostAsync("/api/conversations", new
        {
            leadId,
            channel = "website",
            externalThreadId = $"auto-{_ctx.Tag}-{Guid.NewGuid():N}",
        }, ct: _ctx.Ct)).ExpectStatus(201);
        return response.Id();
    }
}
