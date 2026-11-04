using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>
/// Shared steps of the operations suites. Bookings, calendars, campaigns and reports need a plan that includes them,
/// so each suite works on a named client of its own that is moved to "business" once per run (a plan change: the
/// tests are Risk.Billing and need the admin key). Contacts are created the way n8n does it (ingest key).
/// </summary>
internal static class OpsHelpers
{
    /// <summary>Needs of a test on a plan-upgraded client that also creates contacts.</summary>
    public const Needs PlanNeeds = Needs.Session | Needs.AdminKey | Needs.IngestKey;

    private static readonly ConditionalWeakTable<TestContext, Dictionary<string, Task<TestClient>>> Ready = new();

    /// <summary>A named client of the run on <paramref name="plan"/> (registered and upgraded once).</summary>
    public static Task<TestClient> ClientAsync(TestContext ctx, string label = "ops", string plan = "business")
    {
        var map = Ready.GetOrCreateValue(ctx);
        lock (map)
        {
            if (!map.TryGetValue(label, out var task))
            {
                task = SetupAsync();
                map[label] = task;
            }
            return task;
        }

        async Task<TestClient> SetupAsync()
        {
            var client = await ctx.NamedClientAsync(label);
            await ctx.SetPlanAsync(client, plan);
            return client;
        }
    }

    /// <summary>The ops client with opening hours every day 06:00-22:00 UTC and no notice (slots and bookings always fit).</summary>
    public static async Task<TestClient> BookingClientAsync(TestContext ctx, string label = "ops")
    {
        var client = await ClientAsync(ctx, label);
        await ctx.Fixtures.EnsureAvailabilityAsync(client);
        return client;
    }

    /// <summary>Creates a contact through the ingest API (what n8n does; the dashboard has no create-contact API).</summary>
    public static async Task<Guid> ContactAsync(TestContext ctx, TestClient client, string label, bool phone = true, bool optIn = true)
    {
        var response = (await ctx.Ingest.PostAsync("/api/contacts", new
        {
            clientId = client.ClientId,
            serviceId = client.BaseServiceId,
            fullName = $"Auto {label} {ctx.Tag}",
            phone = phone ? ctx.Fixtures.NewPhone() : null,
            source = "automation",
            sourceDetail = "Omni.Automation",
            externalContactId = $"auto-{ctx.Tag}-{label}-{Guid.NewGuid():N}",
            preferredLanguage = "en",
            marketingOptIn = optIn,
        }, ct: ctx.Ct)).ExpectStatus(200, 201);
        return response.Id();
    }

    /// <summary>A UTC time <paramref name="days"/> days from today at hh:mm (inside the 06:00-22:00 UTC opening hours).</summary>
    public static DateTimeOffset At(int days, int hour, int minute = 0)
    {
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        return new DateTimeOffset(today.AddDays(days).AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
    }

    public static Task<ApiResponse> BookAsync(TestContext ctx, TestClient client, Guid contactId, DateTimeOffset start, DateTimeOffset? end,
        Guid? serviceId = null, string bookingType = "standard", string? locationType = null, string? locationName = null, string? notes = "Omni.Automation",
        bool noService = false) =>
        client.Http.PostAsync("/api/bookings", new
        {
            contactId,
            serviceId = noService ? (Guid?)null : serviceId ?? client.BaseServiceId,
            bookingType,
            scheduledStart = start,
            scheduledEnd = end,
            locationType,
            locationName,
            notes,
        }, ct: ctx.Ct);

    /// <summary>Creates a 30-minute booking and returns its id.</summary>
    public static async Task<Guid> BookOkAsync(TestContext ctx, TestClient client, Guid contactId, DateTimeOffset start) =>
        (await BookAsync(ctx, client, contactId, start, start.AddMinutes(30))).ExpectStatus(201).Id();

    public static Task<ApiResponse> SetStatusAsync(TestContext ctx, TestClient client, Guid bookingId, string status) =>
        client.Http.PatchAsync($"/api/bookings/{bookingId}/status", new { status }, ctx.Ct);

    /// <summary>An approved WhatsApp template inserted in the DB (WhatsApp can't be connected on staging, so templates can't sync).</summary>
    public static async Task<Guid> TemplateAsync(TestContext ctx, TestClient client)
    {
        var id = Guid.NewGuid();
        await ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into channels.whatsapp_templates (id, client_id, name, category, language, status, body)
            values (@id, @clientId, @name, 'marketing', 'en_US', 'approved', 'Hi {{1}}, we miss you!')
            """, new { id, name = $"auto_{ctx.Tag}_{id.ToString("N")[..8]}" }, ctx.Ct);
        // The app caches a client's templates; drop the list so it sees the new row (404 = it wasn't cached).
        var key = Uri.EscapeDataString($"channels:templates:{client.ClientId:N}");
        var evict = await ctx.Admin.DeleteAsync($"/api/platform-admin/cache/key?key={key}", ctx.Ct);
        if (evict.Status is not (200 or 204 or 404)) throw new TestFailure($"Evicting the templates cache failed (status {evict.Status}).");
        return id;
    }

    public static Task<ApiResponse> CreateCampaignAsync(TestContext ctx, TestClient client, object body) =>
        client.Http.PostAsync("/api/campaigns", body, ct: ctx.Ct);

    /// <summary>A draft campaign for the given contacts (or the audience when the list is empty).</summary>
    public static async Task<Guid> DraftCampaignAsync(TestContext ctx, TestClient client, Guid template, params Guid[] contactIds) =>
        (await CreateCampaignAsync(ctx, client, new
        {
            name = $"Automation {ctx.Tag} {Guid.NewGuid():N}"[..40],
            whatsAppTemplateId = template,
            contactIds,
            audienceType = contactIds.Length == 0 ? "all_eligible" : "custom",
        })).ExpectStatus(201).Id();

    public static async Task<JsonNode> CampaignAsync(TestContext ctx, TestClient client, Guid id) =>
        (await client.Http.GetAsync($"/api/campaigns/{id}", ct: ctx.Ct)).ExpectOk().RequireJson();

    public static async Task<JsonArray> SlotsAsync(TestContext ctx, TestClient client, string query)
    {
        var r = (await client.Http.GetAsync($"/api/bookings/available?serviceId={client.BaseServiceId}&{query}", ct: ctx.Ct)).ExpectOk();
        return r.Array("slots");
    }

    public static DateTimeOffset Start(JsonNode? slot) => DateTimeOffset.Parse(slot!["start"]!.ToString(), CultureInfo.InvariantCulture);

    /// <summary>Submits the Business info availability form and returns the response (302 = saved, 200 = redisplayed with an error).</summary>
    public static Task<ApiResponse> SubmitAvailabilityAsync(TestContext ctx, TestClient client, string timezone, string start, string end, int bufferMinutes = 0)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("TimezoneId", timezone), new("DefaultDurationMinutes", "30"), new("BufferMinutes", bufferMinutes.ToString()),
            new("NoticeHours", "0"), new("MaxAdvanceDays", "60"),
        };
        for (var day = 0; day < 7; day++)
        {
            fields.Add(new($"Days[{day}].DayOfWeek", day.ToString()));
            fields.Add(new($"Days[{day}].IsOpen", "true"));
            fields.Add(new($"Days[{day}].Start", start));
            fields.Add(new($"Days[{day}].End", end));
        }
        return client.Http.SubmitFormAsync("/settings/business-info", fields, handler: "SaveAvailability", ct: ctx.Ct);
    }

    public static async Task<JsonNode?> CalendarRowAsync(TestContext ctx, TestClient client, string provider) =>
        (await client.Http.GetAsync("/api/calendar-integrations", ct: ctx.Ct)).ExpectOk().Array()
        .FirstOrDefault(r => r?["provider"]?.ToString() == provider);

    public static string? QueryValue(string url, string name)
    {
        var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : string.Empty;
        foreach (var part in query.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv[0] == name) return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
        }
        return null;
    }
}

