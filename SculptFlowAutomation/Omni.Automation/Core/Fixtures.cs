using System.Text.Json.Nodes;

namespace Omni.Automation.Core;

/// <summary>
/// Shared test data in the run's main test client, created on first use so any selection of tests works on its own.
/// Each getter is cached for the run; the New* methods always create a fresh record. Methods that take a
/// <see cref="TestClient"/> work on that client instead of the main one.
/// </summary>
public sealed class Fixtures
{
    private readonly TestContext _ctx;
    private Task<Guid>? _contact;
    private readonly Dictionary<Guid, Task> _availability = new();
    private int _phoneCounter;

    public Fixtures(TestContext ctx) => _ctx = ctx;

    /// <summary>A shared contact in the main test client (service = its base service).</summary>
    public Task<Guid> ContactAsync() => _contact ??= NewContactAsync("shared");

    /// <summary>Creates a contact through the intake endpoint (POST /api/contacts). It is intake-only: the caller holds the
    /// ingest key and names the client in the body, as n8n does — so this needs Needs.IngestKey.</summary>
    public async Task<Guid> NewContactAsync(string label, TestClient? client = null, string language = "en")
    {
        client ??= await _ctx.ClientAsync();
        var response = (await _ctx.Ingest.PostAsync("/api/contacts", new
        {
            clientId = client.ClientId,
            serviceId = client.BaseServiceId,
            fullName = $"Auto {label} {_ctx.Tag}",
            firstName = "Auto",
            lastName = $"{label} {_ctx.Tag}",
            phone = NewPhone(),
            email = $"contact-{_ctx.Tag}-{Guid.NewGuid():N}@{TestContext.EmailDomain}",
            source = "automation",
            sourceDetail = "Omni.Automation",
            externalContactId = $"auto-{_ctx.Tag}-{label}-{Guid.NewGuid():N}",
            preferredLanguage = language,
        }, ct: _ctx.Ct)).ExpectStatus(200, 201);
        return response.Id();
    }

    /// <summary>A phone number in a fictional range (+1 555 01xx is reserved for fiction), unique within the run.</summary>
    public string NewPhone() => $"+1555010{Interlocked.Increment(ref _phoneCounter) % 100:00}";

    /// <summary>Opens the client every day 06:00-22:00 in <paramref name="timezone"/> with no minimum notice, so slots
    /// always exist (Settings → Business info → Availability, the same form a person uses).</summary>
    public Task EnsureAvailabilityAsync(TestClient? client = null, string timezone = "UTC")
    {
        return EnsureCore();

        async Task EnsureCore()
        {
            client ??= await _ctx.ClientAsync();
            Task task;
            lock (_availability)
            {
                if (!_availability.TryGetValue(client.ClientId, out task!))
                {
                    task = SaveAvailabilityAsync(client, timezone);
                    _availability[client.ClientId] = task;
                }
            }
            await task;
        }
    }

    /// <summary>Saves opening hours for every day (06:00-22:00 unless given) through the availability form.</summary>
    public async Task SaveAvailabilityAsync(TestClient client, string timezone = "UTC", string start = "06:00", string end = "22:00",
        int noticeHours = 0, int durationMinutes = 30, int bufferMinutes = 0, int maxAdvanceDays = 60, bool[]? openDays = null,
        string? start2 = null, string? end2 = null)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("TimezoneId", timezone),
            new("DefaultDurationMinutes", durationMinutes.ToString()),
            new("BufferMinutes", bufferMinutes.ToString()),
            new("NoticeHours", noticeHours.ToString()),
            new("MaxAdvanceDays", maxAdvanceDays.ToString()),
        };
        for (var day = 0; day < 7; day++)
        {
            var open = openDays?[day] ?? true;
            fields.Add(new($"Days[{day}].DayOfWeek", day.ToString()));
            fields.Add(new($"Days[{day}].IsOpen", open ? "true" : "false"));
            fields.Add(new($"Days[{day}].Start", start));
            fields.Add(new($"Days[{day}].End", end));
            if (start2 is not null) fields.Add(new($"Days[{day}].Start2", start2));   // a second window (QA t5-ops-18)
            if (end2 is not null) fields.Add(new($"Days[{day}].End2", end2));
        }

        var response = await client.Http.SubmitFormAsync("/settings/business-info", fields, handler: "SaveAvailability", ct: _ctx.Ct);
        if (response.Status != 302)
        {
            throw new TestFailure($"Saving opening hours failed (status {response.Status}): {TestContext.PageError(response.Body) ?? "no error shown"}.");
        }
    }

    /// <summary>Open slots for the client's base service from GET /api/bookings/available (opening hours set up first).</summary>
    public async Task<JsonArray> SlotsAsync(int days = 7, TestClient? client = null)
    {
        client ??= await _ctx.ClientAsync();
        await EnsureAvailabilityAsync(client);
        var response = (await client.Http.GetAsync($"/api/bookings/available?serviceId={client.BaseServiceId}&days={days}", ct: _ctx.Ct)).ExpectOk();
        var slots = response.Array("slots");
        if (slots.Count == 0) throw new TestFailure($"No open slots in the next {days} days even though the client is open every day.{response.Snippet()}");
        return slots;
    }

    /// <summary>Start time of the n-th open slot, as the server formatted it.</summary>
    public async Task<string> SlotStartAsync(int index = 0, TestClient? client = null)
    {
        var slots = await SlotsAsync(client: client);
        return slots[Math.Min(index, slots.Count - 1)]!["start"]!.ToString();
    }

    /// <summary>A new inbox conversation for a contact (website channel, so nothing is ever delivered anywhere).</summary>
    public async Task<Guid> NewConversationAsync(Guid contactId, TestClient? client = null)
    {
        client ??= await _ctx.ClientAsync();
        var response = (await client.Http.PostAsync("/api/conversations", new
        {
            contactId,
            channel = "website",
            externalThreadId = $"auto-{_ctx.Tag}-{Guid.NewGuid():N}",
        }, ct: _ctx.Ct)).ExpectStatus(200, 201);
        return response.Id();
    }

    /// <summary>Drops one cache key on the target through the admin cache API (after a direct DB write the app would
    /// otherwise not see for up to an hour). Needs the admin key.</summary>
    public async Task EvictCacheAsync(string key)
    {
        var response = await _ctx.Admin.DeleteAsync($"/api/platform-admin/cache/key?key={Uri.EscapeDataString(key)}", _ctx.Ct);
        // 404 = the key wasn't cached, which is the state we want.
        if (response.Status is not (200 or 204 or 404)) throw new TestFailure($"Evicting cache key {key} failed (status {response.Status}).{response.Snippet(200)}");
    }

    /// <summary>Drops every cache key starting with <paramref name="prefix"/> (e.g. all keys of one client). Needs the admin key.</summary>
    public async Task EvictCachePrefixAsync(string prefix)
    {
        var response = await _ctx.Admin.DeleteAsync($"/api/platform-admin/cache/keys?prefix={Uri.EscapeDataString(prefix)}", _ctx.Ct);
        if (response.Status is not (200 or 204)) throw new TestFailure($"Evicting cache prefix {prefix} failed (status {response.Status}).{response.Snippet(200)}");
    }
}
