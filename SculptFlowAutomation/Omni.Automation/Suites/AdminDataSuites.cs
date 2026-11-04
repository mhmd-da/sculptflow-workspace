using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Contacts, conversations, messages, bookings, overview and content (cross-client admin lists) on the run's data.</summary>
[Suite(84)]
public static class AdminDataSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-data", "Admin: contacts, conversations, overview, content",
            "Cross-client admin lists filtered to the run's clients, and admin edits on records the test creates.")
        .Test("contacts-list", "Contacts list filters by client, search and stage", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await AdminHelpers.NewContactAsync(ctx, await ctx.ClientAsync(), "adm-list");
            var path = $"{AdminHelpers.Api}/contacts?clientId={client.ClientId}";
            var items = (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.ContainsId(items, contactId, "Contacts of the client");
            Expect.True(items.All(c => c?["clientId"] is null || c["clientId"]!.ToString() == client.ClientId.ToString()), "The client filter returned another client's contacts.");
            Expect.ContainsId((await ctx.Admin.GetAsync($"{path}&search={Uri.EscapeDataString("adm-list " + ctx.Tag)}", ct: ctx.Ct)).ExpectOk().Array("items"), contactId, "Search by name");
            var bogus = await ctx.Admin.GetAsync($"{path}&stage=bogus_stage", ct: ctx.Ct);
            Expect.True(bogus.Status is 200 or 400, $"A bogus stage filter answered {bogus.Status}.");
            if (bogus.Status == 200) Expect.LacksId(bogus.Array("items"), contactId, "Bogus stage filter");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey)
        .Test("contact-edit", "Contact edit saves stage, qualification and notes; refuses an unknown stage or qualification", async ctx =>
        {
            var contactId = await AdminHelpers.NewContactAsync(ctx, await ctx.ClientAsync(), "adm-edit");
            var path = $"{AdminHelpers.Api}/contacts/{contactId}";
            (await ctx.Admin.PutAsync(path, new { stageKey = "qualified", qualification = "hot", marketingOptIn = true, notes = "Automation note عربي" }, ctx.Ct)).ExpectOk();
            var detail = (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk().RequireJson().ToJsonString();
            Expect.True(detail.Contains("qualified") && detail.Contains("hot"), $"The contact details don't show the saved stage/qualification: {detail[..Math.Min(300, detail.Length)]}");
            Expect.Equal(400, (await ctx.Admin.PutAsync(path, new { stageKey = "bogus_stage", qualification = "hot", marketingOptIn = false, notes = (string?)null }, ctx.Ct)).Status, "Unknown stage");
            Expect.Equal(400, (await ctx.Admin.PutAsync(path, new { stageKey = "new", qualification = "lukewarm", marketingOptIn = false, notes = (string?)null }, ctx.Ct)).Status, "Unknown qualification");
            Expect.Equal(400, (await ctx.Admin.PutAsync(path, new { stageKey = "consultation_booked", qualification = "hot", marketingOptIn = false, notes = (string?)null }, ctx.Ct)).Status,
                "A clinic-only stage on a general client");
            (await ctx.Admin.GetAsync($"{path}/events", ct: ctx.Ct)).ExpectOk().Array();
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey)
        .Test("contact-export-delete", "Contact export has no secrets; delete removes the contact for good", async ctx =>
        {
            var contactId = await AdminHelpers.NewContactAsync(ctx, await ctx.ClientAsync(), "adm-del");
            var path = $"{AdminHelpers.Api}/contacts/{contactId}";
            var export = (await ctx.Admin.GetAsync($"{path}/export", ct: ctx.Ct)).ExpectOk();
            AdminHelpers.NoSecretProperties(export.Json, "The contact export");
            Expect.True(export.Body.Contains(contactId.ToString(), StringComparison.OrdinalIgnoreCase), "The export doesn't mention the contact.");
            (await ctx.Admin.DeleteAsync(path, ctx.Ct)).ExpectOk();
            Expect.Equal(404, (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).Status, "Details of a deleted contact");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{path}/export", ct: ctx.Ct)).Status, "Export of a deleted contact");
            Expect.Equal(404, (await ctx.Admin.DeleteAsync(path, ctx.Ct)).Status, "Deleting it again");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey)
        .Test("conversations", "Conversation list, details, messages; mode and status changes are validated", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await AdminHelpers.NewContactAsync(ctx, await ctx.ClientAsync(), "adm-conv");
            var conversationId = await ctx.Fixtures.NewConversationAsync(contactId);
            var list = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/contacts/conversations?clientId={client.ClientId}&contactId={contactId}", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.ContainsId(list, conversationId, "Conversations of the contact");
            var path = $"{AdminHelpers.Api}/contacts/conversations/{conversationId}";
            (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{path}/messages", ct: ctx.Ct)).ExpectOk().Array();

            (await ctx.Admin.PutAsync($"{path}/mode", new { mode = "human" }, ctx.Ct)).ExpectOk();
            Expect.Equal(400, (await ctx.Admin.PutAsync($"{path}/mode", new { mode = "robot" }, ctx.Ct)).Status, "Mode robot");
            (await ctx.Admin.PutAsync($"{path}/status", new { status = "closed" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("closed", (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk().Prop("status"), "Status after closing");
            (await ctx.Admin.PutAsync($"{path}/status", new { status = "active" }, ctx.Ct)).ExpectOk();
            Expect.Equal(400, (await ctx.Admin.PutAsync($"{path}/status", new { status = "bogus" }, ctx.Ct)).Status, "Status bogus");
            Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/contacts/conversations/{Guid.NewGuid()}/status", new { status = "closed" }, ctx.Ct)).Status, "Unknown conversation");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey)
        .Test("plan-gate-wording", "Returning a conversation to AI on a plan without AI is refused in admin words", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await AdminHelpers.NewContactAsync(ctx, client, "adm-gate");
            var conversationId = await ctx.Fixtures.NewConversationAsync(contactId);
            var r = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/contacts/conversations/{conversationId}/mode", new { mode = "ai" }, ctx.Ct);
            Expect.Equal(422, r.Status, "Returning to AI on the trial plan");
            var error = r.Str("error");
            Expect.True(!error.Contains("your plan", StringComparison.OrdinalIgnoreCase) && !error.Contains("Settings", StringComparison.Ordinal),
                $"The admin API refuses with the client's own wording (\"{error}\"): the admin has no Settings page and it isn't their plan.");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey, finding: "t6-admin-19")
        .Test("messages-bookings", "Messages and bookings lists answer for a client and tolerate odd filters", async ctx =>
        {
            var client = await ctx.ClientAsync();
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/contacts/messages?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array("items");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/contacts/messages?clientId={client.ClientId}&failedOnly=true&sender=bogus&search=%27&page=-1", ct: ctx.Ct)).ExpectOk().Array("items");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/contacts/bookings?clientId={client.ClientId}&upcomingOnly=true", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/contacts/bookings/{Guid.NewGuid()}/status", new { status = "canceled" }, ctx.Ct)).Status, "Status of an unknown booking");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("overview", "Overview totals, daily messages, problems, recent clients and events answer", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var totals = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/totals", ct: ctx.Ct)).ExpectOk();
            Expect.True(totals.Prop("clients").GetValue<int>() >= 1, "Overview totals show no clients.");
            var days = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/daily-messages?days=3", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(days.Count == 3, $"daily-messages?days=3 answered {days.Count} days.");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/problems", ct: ctx.Ct)).ExpectOk().Array();
            // A client registered just now: a full run creates more than 100 clients after the main one.
            var newest = await ctx.RegisterClientAsync("adm-recent", "general");
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/recent-clients?take=100", ct: ctx.Ct)).ExpectOk().Array(), newest.ClientId, "Recent clients");
            var types = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/event-types", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(types.Count > 0, "No event types.");
            var events = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/events?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.True(events.All(e => e?["clientId"]?.ToString() == client.ClientId.ToString()), "The events filter returned another client's events.");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/overview/events?eventType=%3Cb%3E&page=99999", ct: ctx.Ct)).ExpectOk();
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("content-lists", "Campaigns, templates, services and knowledge lists answer for a client; unknown ids are 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var q = $"clientId={client.ClientId}";
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/campaigns?{q}", ct: ctx.Ct)).ExpectOk().Array("items");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/campaigns?{q}&status=bogus", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/templates?{q}", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/services?{q}", ct: ctx.Ct)).ExpectOk().Array(), client.BaseServiceId, "Services of the client");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/knowledge?{q}", ct: ctx.Ct)).ExpectOk().Array("items");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/knowledge/websites?{q}", ct: ctx.Ct)).ExpectOk().Array();
            foreach (var path in new[] { "content/campaigns", "content/knowledge" })
            {
                Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/{path}/{Guid.NewGuid()}", ct: ctx.Ct)).Status, $"Unknown id on {path}");
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("service-active", "An admin can switch a service off and on", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var service = (await client.Http.PostAsync("/api/services", new { name = $"Admin toggle {ctx.Tag}", code = $"auto-{ctx.Tag}-toggle", description = "Omni.Automation", bookingDuration = 30 }, ct: ctx.Ct))
                .ExpectStatus(201).Id();
            foreach (var active in new[] { false, true })
            {
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/content/services/{service}/active", new { active }, ctx.Ct)).ExpectOk();
                var row = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/content/services?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array()
                    .FirstOrDefault(s => s?["id"]?.ToString() == service.ToString()) ?? throw new TestFailure("The service isn't listed.");
                Expect.Equal(active, row["isActive"]!.GetValue<bool>(), "Service active flag");
            }
            Expect.Equal(404, (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/content/services/{Guid.NewGuid()}/active", new { active = false }, ctx.Ct)).Status, "Unknown service");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("search-settings", "Knowledge search settings: Top K 1-10 and similarity 0-1 enforced", async ctx =>
        {
            var client = await ctx.NamedClientAsync("adm-content");
            var path = $"{AdminHelpers.Api}/content/knowledge/settings/{client.ClientId}";
            var current = await ctx.Admin.GetAsync(path, ct: ctx.Ct);
            if (current.Status == 404)
            {
                // A new client has no settings row until its Knowledge settings are first used.
                (await client.Http.GetAsync("/api/knowledge/settings", ct: ctx.Ct)).ExpectOk();
                current = await ctx.Admin.GetAsync(path, ct: ctx.Ct);
            }
            current.ExpectOk();
            (await ctx.Admin.PutAsync(path, new { topK = 8, minimumSimilarity = 0.35 }, ctx.Ct)).ExpectOk();
            var saved = (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk();
            Expect.Equal(8, saved.Prop("topK").GetValue<int>(), "Saved Top K");
            var problems = new List<string>();
            foreach (var (topK, sim) in new (object, object)[] { (0, 0.5), (11, 0.5), (-1, 0.5), (15, 0.5), (5, 2), (5, -0.1), (5, "abc") })
            {
                var r = await ctx.Admin.PutAsync(path, new { topK, minimumSimilarity = sim }, ctx.Ct);
                if (r.Status != 400) problems.Add($"topK {topK} / similarity {sim} -> {r.Status}");
            }
            Expect.True(problems.Count == 0, $"Out-of-range search settings weren't refused: {string.Join("; ", problems)}.");
            Expect.Equal(8, (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk().Prop("topK").GetValue<int>(), "Top K after refused changes");
        }, needs: Needs.Session | Needs.AdminKey)
        .Build();
}
