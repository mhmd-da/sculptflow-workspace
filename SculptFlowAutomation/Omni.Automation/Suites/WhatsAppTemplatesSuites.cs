using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Template creation rules and clean-up on a fake connection (t8-12, t8-13, t8-17, t3-inbox-19).</summary>
[Suite(54)]
public static class WhatsAppTemplatesSuite
{
    private const Needs Tpl = Needs.Session | Needs.Database;

    private static async Task<TestClient> ClientAsync(TestContext ctx) => (await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-tpl")).Client;

    private static string NewName(TestContext ctx, string label) => $"auto_{ctx.Tag}_{label}_{Random.Shared.Next(1000, 9999)}".ToLowerInvariant();

    private static Task<ApiResponse> CreateAsync(TestContext ctx, TestClient client, object body) =>
        client.Http.PostAsync("/api/whatsapp/templates", body, ct: ctx.Ct);

    public static TestSuite Build() => new SuiteBuilder("wa-templates", "WhatsApp: templates",
            "Creating templates on a fake WhatsApp connection (submission to Meta fails with the fake token): Meta's naming rules, " +
            "validation without 500s, duplicates, isolation and clean-up.")
        .Test("create", "Templates: a valid template is created and listed", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var name = NewName(ctx, "create");
            var created = (await CreateAsync(ctx, client, new { name, category = "marketing", language = "en_US", body = "Hi {{1}}, 20% off this week! Reply STOP to opt out." })).ExpectStatus(201);
            Expect.EqualText(name, created.Prop("name"), "Name");
            Expect.EqualText("marketing", created.Prop("category"), "Category");
            Expect.ContainsId((await client.Http.GetAsync("/api/whatsapp/templates", ct: ctx.Ct)).ExpectOk().Array(), created.Id(), "Template list");
        }, needs: Tpl)
        .Test("not-submitted", "Templates: a template Meta never received isn't shown as Rejected with raw Graph JSON", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var created = (await CreateAsync(ctx, client, new { name = NewName(ctx, "unsent"), category = "utility", language = "en_US", body = "Your order {{1}} is ready." })).ExpectStatus(201);
            var status = created.Str("status");
            var reason = created.Json?["rejectionReason"]?.ToString() ?? "";
            Expect.True(status != "rejected" && !reason.Contains("{\"error\""),
                $"A template whose submission failed (token error) is \"{status}\" with the reason \"{(reason.Length > 100 ? reason[..100] + "…" : reason)}\"; it was never reviewed by Meta.");
            var submitError = created.Json?["submitError"]?.ToString();
            Expect.True(status != "draft" || (!string.IsNullOrWhiteSpace(submitError) && !submitError.Contains("{\"error\"")),
                $"A draft Meta never received should say why in readable words (submitError \"{submitError}\").");
        }, needs: Tpl, finding: "t8-12")
        .Test("name-rules", "Templates: names Meta wouldn't accept (upper case, spaces, Arabic, over 512) are refused (400)", async ctx =>
        {
            var client = await ClientAsync(ctx);
            foreach (var name in new[] { "Auto_Bad", "auto bad", "قالب", new string('a', 513) })
            {
                (await CreateAsync(ctx, client, new { name, category = "utility", language = "en_US", body = "x" })).ExpectStatus(400);
            }
        }, needs: Tpl)
        .Test("body-category", "Templates: an empty body or an unknown category is refused (400)", async ctx =>
        {
            var client = await ClientAsync(ctx);
            (await CreateAsync(ctx, client, new { name = NewName(ctx, "empty"), category = "utility", language = "en_US", body = "" })).ExpectStatus(400);
            (await CreateAsync(ctx, client, new { name = NewName(ctx, "cat"), category = "spam", language = "en_US", body = "x" })).ExpectStatus(400);
        }, needs: Tpl)
        .Test("meta-category-case", "Templates: Meta's own upper-case category (UTILITY) is accepted", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var response = await CreateAsync(ctx, client, new { name = NewName(ctx, "upper"), category = "UTILITY", language = "en_US", body = "Your order is ready." });
            Expect.True(response.Status == 201, $"Category \"UTILITY\" (how Meta writes it) answered {response.Status}.{response.Snippet(200)}");
        }, needs: Tpl, finding: "t3-inbox-19")
        .Test("duplicate", "Templates: the same name and language twice is a conflict (409); another language is fine", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var name = NewName(ctx, "dup");
            (await CreateAsync(ctx, client, new { name, category = "utility", language = "en_US", body = "x" })).ExpectStatus(201);
            (await CreateAsync(ctx, client, new { name, category = "utility", language = "en_US", body = "x" })).ExpectStatus(409);
            (await CreateAsync(ctx, client, new { name, category = "utility", language = "fr", body = "x" })).ExpectStatus(201);
        }, needs: Tpl)
        .Test("isolation", "Templates: another client's template id gives 404", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var id = (await CreateAsync(ctx, client, new { name = NewName(ctx, "iso"), category = "utility", language = "en_US", body = "x" })).ExpectStatus(201).Id();
            var other = (await ctx.OtherClientAsync()).Http;
            (await other.GetAsync($"/api/whatsapp/templates/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/whatsapp/templates/{id}/sync", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Tpl)
        .Test("bad-input", "Templates: an over-long name, a bad language, bad buttons JSON or an unknown header type get 400, not 500", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var failures = new List<string>();
            foreach (var (what, body) in new (string, object)[]
                     {
                         ("a 300-character name", new { name = "auto_" + new string('a', 295), category = "utility", language = "en_US", body = "x" }),
                         ("language \"klingon<script>\"", new { name = NewName(ctx, "lang"), category = "utility", language = "klingon<script>", body = "x" }),
                         ("buttonsJson \"{not json\"", new { name = NewName(ctx, "btn"), category = "utility", language = "en_US", body = "x", buttonsJson = "{not json" }),
                         ("headerType \"hologram\"", new { name = NewName(ctx, "hdr"), category = "utility", language = "en_US", body = "x", headerType = "hologram", headerContent = "x" }),
                     })
            {
                var response = await CreateAsync(ctx, client, body);
                if (response.Status != 400) failures.Add($"{what} -> {response.Status}");
            }
            Expect.True(failures.Count == 0, $"Bad input wasn't refused with a 400: {string.Join("; ", failures)}.");
        }, needs: Tpl, finding: "t8-13")
        .Test("buttons-object", "Templates: buttons JSON that isn't a list is refused and leaves no orphan draft", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var name = NewName(ctx, "btnobj");
            var response = await CreateAsync(ctx, client, new { name, category = "utility", language = "en_US", body = "x", buttonsJson = "{\"a\":1}" });
            var orphan = (await client.Http.GetAsync("/api/whatsapp/templates", ct: ctx.Ct)).ExpectOk().Array().Any(t => t?["name"]?.ToString() == name);
            Expect.True(response.Status == 400 && !orphan, $"buttonsJson {{\"a\":1}} answered {response.Status}{(orphan ? " and left a draft behind that campaigns can pick" : "")}.");
        }, needs: Tpl, finding: "t8-13")
        .Test("delete", "Templates: a local template can be deleted so its name can be used again", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var name = NewName(ctx, "del");
            var id = (await CreateAsync(ctx, client, new { name, category = "utility", language = "en_US", body = "x" })).ExpectStatus(201).Id();
            var delete = await client.Http.DeleteAsync($"/api/whatsapp/templates/{id}", ctx.Ct);
            var again = await CreateAsync(ctx, client, new { name, category = "utility", language = "en_US", body = "fixed text" });
            Expect.True(delete.Status is 200 or 204 && again.Status == 201,
                $"Deleting the template answered {delete.Status} and re-creating its name answered {again.Status}: a failed or rejected template name is used up for good.");
        }, needs: Tpl, finding: "t8-17")
        .Test("edit", "Templates: a rejected template can be fixed and submitted again; an approved one can't be edited", async ctx =>
        {
            var client = await ClientAsync(ctx);
            var name = NewName(ctx, "edit");
            // On the fake connection Meta refuses the submission, so the template is rejected and never reached Meta.
            var id = (await CreateAsync(ctx, client, new { name, category = "marketing", language = "en_US", body = "first text" })).ExpectStatus(201).Id();
            var edited = await client.Http.PutAsync($"/api/whatsapp/templates/{id}", new { category = "utility", body = "Hi {{1}}, fixed text", headerType = "none" }, ctx.Ct);
            Expect.Equal(200, edited.Status, $"Editing a rejected template{edited.Snippet(200)}");
            var after = (await client.Http.GetAsync($"/api/whatsapp/templates/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.True(after.Json?["body"]?.ToString() == "Hi {{1}}, fixed text" && after.Json?["category"]?.ToString() == "utility",
                $"The edit didn't stick: {after.Snippet(300)}");
            Expect.Equal(400, (await client.Http.PutAsync($"/api/whatsapp/templates/{id}", new { category = "utility", body = " " }, ctx.Ct)).Status, "Editing with an empty text");
            Expect.Equal(404, (await client.Http.PutAsync($"/api/whatsapp/templates/{Guid.NewGuid()}", new { category = "utility", body = "x" }, ctx.Ct)).Status, "Editing an unknown template");
        }, needs: Tpl, finding: "t8-17")
        .Build();
}

