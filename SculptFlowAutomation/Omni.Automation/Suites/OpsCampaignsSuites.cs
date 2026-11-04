using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

[Suite(35)]
public static class OpsCampaignsSuite
{
    private const string Label = "ops-camp";

    private static async Task<(TestClient Client, Guid Template)> SetupAsync(TestContext ctx)
    {
        var client = await OpsHelpers.ClientAsync(ctx, Label);
        return (client, await OpsHelpers.TemplateAsync(ctx, client));
    }

    /// <summary>A second campaigns client with a fake connected WhatsApp number (the main one stays without, for the
    /// no-WhatsApp tests): campaigns can be scheduled, and anything sent fails at Meta, since the number doesn't exist.</summary>
    private static async Task<(TestClient Client, Guid Template)> SetupWithWhatsAppAsync(TestContext ctx)
    {
        var client = await OpsHelpers.ClientAsync(ctx, Label + "-wa");
        await InboxHelpers.OncePerRunAsync(ctx, "wa:" + Label + "-wa", () => ctx.Webhooks.FakeWhatsAppAsync(client));
        return (client, await OpsHelpers.TemplateAsync(ctx, client));
    }

    /// <summary>Scheduling without WhatsApp is refused (422).</summary>
    private static async Task ExpectScheduleRefusedAsync(TestContext ctx, TestClient client, Guid id, DateTimeOffset when)
    {
        var refused = await client.Http.PostAsync($"/api/campaigns/{id}/schedule", new { scheduledAt = when }, ct: ctx.Ct);
        if (refused.Status == 200)
        {
            await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct);
            throw new TestFailure("A campaign was scheduled with no WhatsApp connected (QA t5-ops-05).");
        }
        refused.ExpectStatus(422);
    }

    private static async Task<int> PreviewCountAsync(TestContext ctx, TestClient client, string query = "audienceType=all_eligible") =>
        (await client.Http.GetAsync($"/api/campaigns/audience-preview?{query}", ct: ctx.Ct)).ExpectOk().Prop("matchingContacts").GetValue<int>();

    public static TestSuite Build() => new SuiteBuilder("ops-campaigns", "Operations: campaigns",
            "Campaign creation, audience, validation, scheduling and cancel (WhatsApp is never connected, so nothing is sent).")
        .Test("audience-preview", "Audience: only contacts with a phone who opted in count as reachable", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            var before = await PreviewCountAsync(ctx, client);
            await OpsHelpers.ContactAsync(ctx, client, "aud-ok");
            Expect.Equal(before + 1, await PreviewCountAsync(ctx, client), "Reachable count after adding a contact with a phone");
            await OpsHelpers.ContactAsync(ctx, client, "aud-nophone", phone: false);
            await OpsHelpers.ContactAsync(ctx, client, "aud-optout", optIn: false);
            Expect.Equal(before + 1, await PreviewCountAsync(ctx, client), "Reachable count after adding a contact without a phone and one who opted out");
            var contacts = (await client.Http.GetAsync("/api/campaigns/audience-preview/contacts?audienceType=all_eligible&limit=1", ct: ctx.Ct)).ExpectOk();
            Expect.True(contacts.Array("contacts").Count <= 1, "The contacts preview ignores limit=1.");
            Expect.Equal(before + 1, contacts.Prop("matchingContacts").GetValue<int>(), "Matching contacts in the contacts preview");
            (await client.Http.GetAsync("/api/campaigns/audience-preview?audienceType=reactivation_no_booking", ct: ctx.Ct)).ExpectOk();
            (await client.Http.GetAsync("/api/campaigns/audience-preview?audienceType=everyone", ct: ctx.Ct)).ExpectStatus(400);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Test("create-manual", "Create: a hand-picked list skips the unreachable, ignores other clients' contacts and starts as a draft", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            var ok = await OpsHelpers.ContactAsync(ctx, client, "man-ok");
            var noPhone = await OpsHelpers.ContactAsync(ctx, client, "man-nophone", phone: false);
            var optOut = await OpsHelpers.ContactAsync(ctx, client, "man-optout", optIn: false);
            var foreign = await OpsHelpers.ContactAsync(ctx, await ctx.OtherClientAsync(), "man-foreign");
            var created = (await OpsHelpers.CreateCampaignAsync(ctx, client, new
            {
                name = $"Automation manual {ctx.Tag}", whatsAppTemplateId = template, contactIds = new[] { ok, noPhone, optOut, foreign },
                variablesByContactId = new Dictionary<Guid, string[]> { [ok] = new[] { "Auto" } },
            })).ExpectStatus(201);
            Expect.EqualText("draft", created.Prop("status"), "Status");
            var details = await OpsHelpers.CampaignAsync(ctx, client, created.Id());
            var recipients = details["recipients"]!.AsArray();
            Expect.Equal(3, recipients.Count, "Recipients (the other client's contact must be ignored)");
            JsonNode Of(Guid id) => recipients.First(r => r?["contactId"]?.ToString() == id.ToString())!;
            Expect.EqualText("pending", Of(ok)["status"], "Reachable contact");
            Expect.EqualText("skipped", Of(noPhone)["status"], "Contact without a phone");
            Expect.EqualText("skipped", Of(optOut)["status"], "Contact who opted out");
            Expect.ContainsId((await client.Http.GetAsync("/api/campaigns", ct: ctx.Ct)).ExpectOk().Array(), created.Id(), "Campaign list");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing)
        .Test("skip-reasons-worded", "Details page: skipped recipients show a worded reason, not the raw code", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template,
                await OpsHelpers.ContactAsync(ctx, client, "code-ok"), await OpsHelpers.ContactAsync(ctx, client, "code-nophone", phone: false));
            var page = (await client.Http.GetAsync($"/Campaigns/{id}", ct: ctx.Ct)).ExpectOk();
            var raw = System.Text.RegularExpressions.Regex.Match(page.Body, @">\s*(no_phone|marketing_opt_in_false|opted_out|do_not_contact)\s*<");
            Expect.True(!raw.Success, $"The campaign details page shows the raw skip code \"{raw.Groups[1].Value}\" to the client.");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-12")
        .Test("validation", "Create: blank name, unknown template, unknown audience, bad filters or only foreign contacts are 400", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            await OpsHelpers.ContactAsync(ctx, client, "val");
            var foreign = await OpsHelpers.ContactAsync(ctx, await ctx.OtherClientAsync(), "val-foreign");
            var cases = new (string What, object Body)[]
            {
                ("blank name", new { name = "  ", whatsAppTemplateId = template, contactIds = Array.Empty<Guid>(), audienceType = "all_eligible" }),
                ("unknown template", new { name = "Automation", whatsAppTemplateId = Guid.NewGuid(), contactIds = Array.Empty<Guid>(), audienceType = "all_eligible" }),
                ("unknown audience", new { name = "Automation", whatsAppTemplateId = template, contactIds = Array.Empty<Guid>(), audienceType = "everyone" }),
                ("malformed filters", new { name = "Automation", whatsAppTemplateId = template, contactIds = Array.Empty<Guid>(), audienceType = "custom", audienceFilters = "{bad" }),
                ("only another client's contact", new { name = "Automation", whatsAppTemplateId = template, contactIds = new[] { foreign } }),
            };
            foreach (var (what, body) in cases)
            {
                var r = await OpsHelpers.CreateCampaignAsync(ctx, client, body);
                Expect.True(r.Status == 400, $"Campaign with {what}: expected 400, got {r.Status}.{r.Snippet(200)}");
            }
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing)
        .Test("long-name", "Create: a name over 200 characters is 400, not a 500 page", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            await OpsHelpers.ContactAsync(ctx, client, "long");
            var r = await OpsHelpers.CreateCampaignAsync(ctx, client, new { name = new string('n', 255), whatsAppTemplateId = template, contactIds = Array.Empty<Guid>(), audienceType = "all_eligible" });
            Expect.True(r.Status == 400 && !r.Body.Contains("Template not found"), $"A 255-character campaign name answered {r.Status} (expected 400 \"name too long\").{r.Snippet(150)}");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-08")
        .Test("typo-filter", "Create: an unknown filter key is refused instead of targeting everyone", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            await OpsHelpers.ContactAsync(ctx, client, "typo");
            var r = await OpsHelpers.CreateCampaignAsync(ctx, client, new
            {
                name = $"Automation typo {ctx.Tag}", whatsAppTemplateId = template, contactIds = Array.Empty<Guid>(), audienceType = "custom",
                audienceFilters = "{\"stagekey\":[\"nonexistent\"]}",
            });
            Expect.True(r.Status == 400 && !r.Body.Contains("Template not found"), $"Filters with the typo key \"stagekey\" answered {r.Status}: the unknown key is ignored and every reachable contact is targeted.");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-09")
        .Test("schedule", "Schedule: a draft can be scheduled once; stop pauses it and cancel ends it", async ctx =>
        {
            // Scheduling needs WhatsApp connected (t5-ops-05): refused on the plain client, allowed on one with a fake connection.
            var when = DateTimeOffset.UtcNow.AddDays(10);
            var (plain, plainTemplate) = await SetupAsync(ctx);
            await ExpectScheduleRefusedAsync(ctx, plain, await OpsHelpers.DraftCampaignAsync(ctx, plain, plainTemplate, await OpsHelpers.ContactAsync(ctx, plain, "sch0")), when);
            var (client, template) = await SetupWithWhatsAppAsync(ctx);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template, await OpsHelpers.ContactAsync(ctx, client, "sch"));
            Expect.EqualText("scheduled", (await client.Http.PostAsync($"/api/campaigns/{id}/schedule", new { scheduledAt = when }, ct: ctx.Ct)).ExpectOk().Prop("status"), "Status after scheduling");
            (await client.Http.PostAsync($"/api/campaigns/{id}/schedule", new { scheduledAt = when.AddDays(1) }, ct: ctx.Ct)).ExpectStatus(422);
            Expect.EqualText("paused", (await client.Http.PostAsync($"/api/campaigns/{id}/stop", ct: ctx.Ct)).ExpectOk().Prop("status"), "Status after stop");
            Expect.EqualText("cancelled", (await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct)).ExpectOk().Prop("status"), "Status after cancel");
            (await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct)).ExpectStatus(422);
            (await client.Http.PostAsync($"/api/campaigns/{id}/send", ct: ctx.Ct)).ExpectStatus(422);
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-05",
            description: "Connects a fake WhatsApp number (no real Meta account), so nothing can be delivered; the campaign is cancelled before its date.")
        .Test("stop-draft", "Stop: a draft can't be stopped (422)", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template, await OpsHelpers.ContactAsync(ctx, client, "stop"));
            (await client.Http.PostAsync($"/api/campaigns/{id}/stop", ct: ctx.Ct)).ExpectStatus(422);
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing)
        .Test("schedule-past", "Schedule: a time in the past is refused", async ctx =>
        {
            var (client, template) = await SetupWithWhatsAppAsync(ctx);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template, await OpsHelpers.ContactAsync(ctx, client, "past"));
            var r = await client.Http.PostAsync($"/api/campaigns/{id}/schedule", new { scheduledAt = "2020-01-01T00:00:00Z" }, ct: ctx.Ct);
            var status = r.Json?["status"]?.ToString();
            if (r.IsSuccess)
            {
                // Don't let the worker send it on its next tick.
                await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct);
            }
            Expect.True(r.Status == 400, $"Scheduling for 2020-01-01 answered {r.Status} (status {status ?? "?"}); the worker would start it on the next tick.");
            var soon = await client.Http.PostAsync($"/api/campaigns/{id}/schedule", new { scheduledAt = DateTimeOffset.UtcNow.AddSeconds(20) }, ct: ctx.Ct);
            if (soon.IsSuccess) await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct);
            Expect.True(soon.Status == 400, $"Scheduling 20 seconds ahead answered {soon.Status}; a time must be at least a minute ahead.");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-10")
        .Test("cancel-draft", "Cancel: cancelling a draft doesn't mark its recipients as failed", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template,
                await OpsHelpers.ContactAsync(ctx, client, "cd1"), await OpsHelpers.ContactAsync(ctx, client, "cd2"));
            (await client.Http.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct)).ExpectOk();
            var failed = (await OpsHelpers.CampaignAsync(ctx, client, id))["stats"]!["failed"]!.GetValue<int>();
            Expect.True(failed == 0, $"Cancelling a draft that never sent marked {failed} recipients \"failed\" (\"Campaign cancelled.\").");
            var skipped = await ctx.Db.ScalarAsync<long>(
                "select count(*) from marketing.campaign_recipients where campaign_id = @id and status = 'skipped' and skip_reason = 'campaign_cancelled'", new { id }, ctx.Ct);
            Expect.Equal(2L, skipped, "Recipients of the cancelled draft marked skipped (campaign_cancelled)");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-11")
        .Test("send-without-whatsapp", "Send: with no WhatsApp connected the send is refused, or recipients fail saying WhatsApp isn't connected", async ctx =>
        {
            var (client, template) = await SetupAsync(ctx);
            var id = await OpsHelpers.DraftCampaignAsync(ctx, client, template, await OpsHelpers.ContactAsync(ctx, client, "send"));
            var r = await client.Http.PostAsync($"/api/campaigns/{id}/send", ct: ctx.Ct);
            if (r.Status is 400 or 422) return;
            r.ExpectOk();
            var details = await OpsHelpers.CampaignAsync(ctx, client, id);
            var reasons = details["recipients"]!.AsArray().Select(x => x?["failureReason"]?.ToString() ?? x?["status"]?.ToString() ?? "").ToList();
            Expect.True(reasons.All(x => x.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) && !x.Contains("priced", StringComparison.OrdinalIgnoreCase)),
                $"Sending with no WhatsApp connected was accepted and recipients failed with: \"{string.Join("\", \"", reasons)}\" (the real cause, no WhatsApp connection, is hidden).");
        }, needs: OpsHelpers.PlanNeeds | Needs.Database, risk: Risk.Billing, finding: "t5-ops-05",
            description: "No channel is connected on the test client, so nothing can be delivered.")
        .Test("isolation", "Campaigns: an unknown or another client's campaign is 404", async ctx =>
        {
            var client = await OpsHelpers.ClientAsync(ctx, Label);
            (await client.Http.GetAsync($"/api/campaigns/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await client.Http.PostAsync($"/api/campaigns/{Guid.NewGuid()}/cancel", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: OpsHelpers.PlanNeeds, risk: Risk.Billing)
        .Build();
}

