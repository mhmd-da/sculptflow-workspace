using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Contacts API and the n8n contact intake (t3-inbox-08, -19, -06).</summary>
[Suite(40)]
public static class InboxContactsSuite
{
    private const Needs Api = Needs.Session | Needs.IngestKey;

    public static TestSuite Build() => new SuiteBuilder("inbox-contacts", "Inbox: contacts",
            "Contact list, search, stages, editing, export/delete and the n8n intake (POST /api/contacts with the ingest key).")
        .Test("signed-out", "Contacts: signed-out callers get 401", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/contacts", ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Test("list-paging", "Contacts: list pages with skip/take and clamps silly values", async ctx =>
        {
            var client = await ctx.ClientAsync();
            for (var i = 0; i < 3; i++) await InboxHelpers.NewContactAsync(ctx, client, $"page{i}");
            var page = (await client.Http.GetAsync("/api/contacts?take=2", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(2, page.Array("items").Count, "Items on a page of 2");
            Expect.True(page.Prop("totalCount").GetValue<int>() >= 3, $"totalCount should count every contact (got {page.Prop("totalCount")}).");
            var clamped = (await client.Http.GetAsync("/api/contacts?take=100000&skip=-5", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(200, clamped.Prop("take").GetValue<int>(), "take=100000 is clamped to");
            Expect.Equal(0, clamped.Prop("skip").GetValue<int>(), "skip=-5 is clamped to");
        }, needs: Api)
        .Test("get", "Contacts: a new contact is returned with its details and the entry stage", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "get", fullName: $"Auto Get {ctx.Tag}", phone: "+15550100555");
            var contact = await InboxHelpers.ContactAsync(ctx, client, id);
            Expect.EqualText($"Auto Get {ctx.Tag}", contact["fullName"], "Full name");
            Expect.EqualText("+15550100555", contact["phone"], "Phone");
            Expect.EqualText("new", contact["stageKey"], "Stage of a new contact");
        }, needs: Api)
        .Test("search-name", "Contacts: search finds a contact by name", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var name = $"Searchable {ctx.Tag} {Guid.NewGuid().ToString("N")[..6]}";
            var id = await InboxHelpers.NewContactAsync(ctx, client, "search", fullName: name);
            var items = (await client.Http.GetAsync($"/api/contacts?search={Uri.EscapeDataString(name)}", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.ContainsId(items, id, "Search by the full name");
            Expect.Equal(1, items.Count, "Contacts matching a unique name");
        }, needs: Api)
        .Test("search-arabic", "Contacts: search works with Arabic names", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var word = "سارة" + Guid.NewGuid().ToString("N")[..4];
            var id = await InboxHelpers.NewContactAsync(ctx, client, "arabic", fullName: $"{word} {ctx.Tag}");
            var items = (await client.Http.GetAsync($"/api/contacts?search={Uri.EscapeDataString(word)}", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.ContainsId(items, id, "Arabic search");
        }, needs: Api)
        .Test("search-wildcards", "Contacts: searching for % or _ matches those characters, not everyone", async ctx =>
        {
            var client = await ctx.ClientAsync();
            await InboxHelpers.NewContactAsync(ctx, client, "wild");
            foreach (var term in new[] { "%", "_" })
            {
                var items = (await client.Http.GetAsync($"/api/contacts?search={Uri.EscapeDataString(term)}", ct: ctx.Ct)).ExpectOk().Array("items");
                Expect.True(items.Count == 0, $"Searching for \"{term}\" returned {items.Count} contacts although no name, phone or email contains it: the search doesn't escape SQL wildcards.");
            }
        }, needs: Api, finding: "t3-inbox-19")
        .Test("search-sql-like", "Contacts: a search that looks like SQL reaches the app (not blocked by the edge firewall)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var response = await client.Http.GetAsync($"/api/contacts?search={Uri.EscapeDataString("x' or 1=1 --")}", ct: ctx.Ct);
            Expect.True(response.Status == 200, $"The search was answered {response.Status}{(response.Status == 403 ? " (blocked by the edge firewall before reaching the app)" : "")} instead of 200.{response.Snippet(200)}");
            Expect.Equal(0, response.Array("items").Count, "Contacts matching the SQL-like text");
        }, finding: "t3-inbox-06")
        .Test("stage-filter", "Contacts: the stage filter lists only contacts in that stage", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "stagefilter");
            (await client.Http.PostAsync($"/api/contacts/{id}/stage", new { stageKey = "qualified" }, ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId((await client.Http.GetAsync("/api/contacts?stage=qualified&take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "stage=qualified");
            Expect.LacksId((await client.Http.GetAsync("/api/contacts?stage=new&take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "stage=new");
            var bogus = await client.Http.GetAsync("/api/contacts?stage=bogus", ct: ctx.Ct);
            Expect.True(bogus.Status < 500, $"An unknown stage filter crashed the list ({bogus.Status}).");
        }, needs: Api)
        .Test("stage-moves", "Contacts: a general-business contact moves through every stage of its pipeline", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "stages");
            foreach (var stage in new[] { "contacted", "qualified", "booked", "attended", "no_show", "customer", "not_interested", "lost", "needs_human", "new" })
            {
                var contact = (await client.Http.PostAsync($"/api/contacts/{id}/stage", new { stageKey = stage }, ct: ctx.Ct)).ExpectOk();
                Expect.EqualText(stage, contact.Prop("stageKey"), $"Stage after moving to {stage}");
            }
        }, needs: Api)
        .Test("stage-unknown", "Contacts: another business type's stage key, a blank or wrong-case key is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "badstage");
            foreach (var stage in new[] { "surgery_booked", "consultation_booked", "", "NEW" })
            {
                (await client.Http.PostAsync($"/api/contacts/{id}/stage", new { stageKey = stage }, ct: ctx.Ct)).ExpectStatus(400);
            }
            Expect.EqualText("new", (await InboxHelpers.ContactAsync(ctx, client, id))["stageKey"], "Stage after refused moves");
        }, needs: Api)
        .Test("qualification", "Contacts: a known qualification is saved, an unknown one is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "qual");
            var hot = (await client.Http.PostAsync($"/api/contacts/{id}/stage", new { stageKey = "new", qualification = "hot" }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("hot", hot.Prop("qualification"), "Qualification");
            (await client.Http.PostAsync($"/api/contacts/{id}/stage", new { stageKey = "new", qualification = "super" }, ct: ctx.Ct)).ExpectStatus(400);
        }, needs: Api)
        .Test("language", "Contacts: language names and codes are stored as two-letter codes; nonsense is refused", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "lang");
            foreach (var (typed, code) in new[] { ("Arabic", "ar"), ("العربية", "ar"), ("fr-FR", "fr"), ("Français", "fr"), ("EN", "en"), ("english", "en") })
            {
                var contact = (await client.Http.PatchAsync($"/api/contacts/{id}", new { preferredLanguage = typed }, ctx.Ct)).ExpectOk();
                Expect.EqualText(code, contact.Prop("preferredLanguage"), $"Language saved for \"{typed}\"");
            }
            (await client.Http.PatchAsync($"/api/contacts/{id}", new { preferredLanguage = "klingon" }, ctx.Ct)).ExpectStatus(400);
        }, needs: Api)
        .Test("patch", "Contacts: city, notes and follow-up date are saved", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "patch");
            var contact = (await client.Http.PatchAsync($"/api/contacts/{id}", new { city = "Beyrouth", notes = "<b>note</b>", nextFollowupAt = "2030-01-02T10:00:00Z" }, ctx.Ct)).ExpectOk();
            Expect.EqualText("Beyrouth", contact.Prop("city"), "City");
            Expect.EqualText("<b>note</b>", contact.Prop("notes"), "Notes (stored as text)");
            Expect.True(contact.Str("nextFollowupAt").StartsWith("2030-01-02"), $"Follow-up date: got {contact.Str("nextFollowupAt")}.");
        }, needs: Api)
        .Test("patch-email", "Contacts: an invalid email is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "bademail");
            (await client.Http.PatchAsync($"/api/contacts/{id}", new { email = "not-an-email" }, ctx.Ct)).ExpectClientError("Saving the email \"not-an-email\"");
        }, needs: Api, finding: "t3-inbox-08")
        .Test("patch-phone", "Contacts: a phone that isn't a number is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "badphone");
            (await client.Http.PatchAsync($"/api/contacts/{id}", new { phone = "call me maybe" }, ctx.Ct)).ExpectClientError("Saving the phone \"call me maybe\"");
        }, needs: Api, finding: "t3-inbox-08")
        .Test("patch-empty-name", "Contacts: an empty name is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "noname");
            (await client.Http.PatchAsync($"/api/contacts/{id}", new { fullName = "" }, ctx.Ct)).ExpectClientError("Saving an empty name");
        }, needs: Api, finding: "t3-inbox-08")
        .Test("patch-too-long", "Contacts: a 60-character phone or a 300-character name is refused with 400, not a 500", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "long");
            (await client.Http.PatchAsync($"/api/contacts/{id}", new { phone = new string('1', 60) }, ctx.Ct)).ExpectClientError("Saving a 60-character phone");
            (await client.Http.PatchAsync($"/api/contacts/{id}", new { fullName = new string('N', 300) }, ctx.Ct)).ExpectClientError("Saving a 300-character name");
        }, needs: Api, finding: "t3-inbox-08")
        .Test("export", "Contacts: the owner can export a contact as a JSON file", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var id = await InboxHelpers.NewContactAsync(ctx, client, "export", fullName: $"Auto Export {ctx.Tag}");
            var file = (await client.Http.GetAsync($"/api/contacts/{id}/export", ct: ctx.Ct)).ExpectOk();
            Expect.True(file.ContentType?.Contains("json") == true, $"Export content type: {file.ContentType}.");
            file.ExpectBodyContains($"Auto Export {ctx.Tag}");
        }, needs: Api)
        .Test("delete", "Contacts: delete needs the typed name; then the contact is gone", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var name = $"Auto Delete {ctx.Tag}";
            var id = await InboxHelpers.NewContactAsync(ctx, client, "delete", fullName: name);
            (await client.Http.SendJsonAsync(HttpMethod.Delete, $"/api/contacts/{id}", new { confirmation = "nope" }, ct: ctx.Ct)).ExpectStatus(400);
            (await client.Http.GetAsync($"/api/contacts/{id}", ct: ctx.Ct)).ExpectOk();
            (await client.Http.SendJsonAsync(HttpMethod.Delete, $"/api/contacts/{id}", new { confirmation = name }, ct: ctx.Ct)).ExpectStatus(204);
            (await client.Http.GetAsync($"/api/contacts/{id}", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Api)
        .Test("isolation", "Contacts: another client's contact id gives 404 for every action", async ctx =>
        {
            var owner = await ctx.ClientAsync();
            var name = $"Auto Isolated {ctx.Tag}";
            var id = await InboxHelpers.NewContactAsync(ctx, owner, "isolated", fullName: name);
            var other = (await ctx.OtherClientAsync()).Http;
            (await other.GetAsync($"/api/contacts/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PatchAsync($"/api/contacts/{id}", new { notes = "x" }, ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/contacts/{id}/stage", new { stageKey = "new" }, ct: ctx.Ct)).ExpectStatus(404);
            (await other.GetAsync($"/api/contacts/{id}/export", ct: ctx.Ct)).ExpectStatus(404);
            (await other.SendJsonAsync(HttpMethod.Delete, $"/api/contacts/{id}", new { confirmation = name }, ct: ctx.Ct)).ExpectStatus(404);
            (await owner.Http.GetAsync($"/api/contacts/{id}", ct: ctx.Ct)).ExpectOk();
        }, needs: Api)
        .Test("intake-no-key", "Contact intake: POST /api/contacts without the ingest key is refused (401)", async ctx =>
        {
            (await ctx.Anonymous.PostAsync("/api/contacts", new { clientId = Guid.NewGuid(), fullName = "x" }, ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Test("intake-dedup", "Contact intake: the same external contact id returns the existing contact (200)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var external = $"auto-{ctx.Tag}-dedup-{Guid.NewGuid():N}";
            var first = (await ctx.Ingest.PostAsync("/api/contacts", new { clientId = client.ClientId, fullName = $"Auto Dedup {ctx.Tag}", externalContactId = external }, ct: ctx.Ct)).ExpectStatus(201);
            var second = (await ctx.Ingest.PostAsync("/api/contacts", new { clientId = client.ClientId, fullName = "Someone else", externalContactId = external }, ct: ctx.Ct)).ExpectStatus(200);
            Expect.Equal(first.Id(), second.Id(), "Contact id for a repeated external id");
            Expect.EqualText($"Auto Dedup {ctx.Tag}", second.Prop("fullName"), "Name kept from the first intake");
        }, needs: Api)
        .Test("intake-unknown-client", "Contact intake: an unknown client id gives 404", async ctx =>
        {
            (await ctx.Ingest.PostAsync("/api/contacts", new { clientId = Guid.NewGuid(), fullName = "x" }, ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Needs.IngestKey)
        .Test("intake-no-identity", "Contact intake: a contact without name, phone or email is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            (await ctx.Ingest.PostAsync("/api/contacts", new { clientId = client.ClientId, source = "automation" }, ct: ctx.Ct)).ExpectStatus(400);
        }, needs: Api)
        .Build();
}

