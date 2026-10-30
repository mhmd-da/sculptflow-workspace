using System.Net.Http.Headers;
using System.Text;
using SculptFlowAutomation.Core;

namespace SculptFlowAutomation.Suites;

public static class KnowledgeSuite
{
    public static TestSuite Build() => new SuiteBuilder("knowledge", "Knowledge base", "Knowledge settings, entries (embedded with OpenAI) and file upload.")
        .Test("settings", "Read and update retrieval settings", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var current = (await api.GetAsync("/api/knowledge/settings", ct: ctx.Ct)).ExpectOk();
            var body = new
            {
                chunkSizeTokens = int.Parse(current.Str("chunkSizeTokens")),
                chunkOverlapTokens = int.Parse(current.Str("chunkOverlapTokens")),
                topK = 4,
                minimumSimilarity = 0.3,
            };
            Expect.EqualText("4", (await api.PutAsync("/api/knowledge/settings", body, ctx.Ct)).ExpectOk().Prop("topK"), "topK");
            (await api.PutAsync("/api/knowledge/settings", body with { topK = 99 }, ctx.Ct)).ExpectStatus(400);
        })
        .Test("list", "Knowledge list loads", async ctx =>
        {
            (await (await ctx.ApiAsync()).GetAsync("/api/knowledge", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Test("entry", "Create, edit, deactivate and delete an entry", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var created = (await api.PostAsync("/api/knowledge", new
            {
                title = $"Recovery after rhinoplasty {ctx.Tag}", category = "aftercare",
                content = "Most patients return to light work after 7 to 10 days. Swelling settles over several weeks.", isActive = true,
            }, ct: ctx.Ct)).ExpectStatus(201);
            var id = created.Id();

            (await api.GetAsync($"/api/knowledge/{id}", ct: ctx.Ct)).ExpectOk();
            (await api.PutAsync($"/api/knowledge/{id}", new
            {
                title = $"Recovery after rhinoplasty v2 {ctx.Tag}", category = "aftercare",
                content = "Most patients return to light work after about a week.", isActive = true,
            }, ctx.Ct)).ExpectOk();
            Expect.EqualText("false", (await api.PostAsync($"/api/knowledge/{id}/active", new { isActive = false }, ct: ctx.Ct)).ExpectOk().Prop("isActive"), "isActive");
            (await api.DeleteAsync($"/api/knowledge/{id}", ctx.Ct)).ExpectStatus(204, 200);
            (await api.GetAsync($"/api/knowledge/{id}", ct: ctx.Ct)).ExpectStatus(404);
        }, risk: Risk.External)
        .Test("upload", "Upload a text file as an entry", async ctx =>
        {
            var api = await ctx.ApiAsync();
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"Consultations last 30 minutes and are free. ({ctx.Tag})"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "File", $"faq-{ctx.Tag}.txt");
            form.Add(new StringContent($"FAQ {ctx.Tag}"), "Title");
            form.Add(new StringContent("faq"), "Category");
            var id = (await api.PostMultipartAsync("/api/knowledge/upload", form, ctx.Ct)).ExpectStatus(201).Id();
            (await api.DeleteAsync($"/api/knowledge/{id}", ctx.Ct)).ExpectStatus(204, 200);
        }, risk: Risk.External)
        .Build();
}

public static class WebsitesSuite
{
    public static TestSuite Build() => new SuiteBuilder("websites", "Website sources", "Adding a website to the knowledge base (scrapes example.com).")
        .Test("list", "Website sources list loads", async ctx =>
        {
            (await (await ctx.ApiAsync()).GetAsync("/api/knowledge/websites", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Test("scrape", "Scrape a single page, then remove the source", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var id = (await api.PostAsync("/api/knowledge/websites", new { url = "https://example.com/", crawlMode = "single_page", category = "website", isActive = true }, ct: ctx.Ct))
                .ExpectStatus(202).Id();

            string status = "pending";
            for (var i = 0; i < 30 && status is "pending" or "crawling"; i++)
            {
                await Task.Delay(2000, ctx.Ct);
                status = (await api.GetAsync($"/api/knowledge/websites/{id}", ct: ctx.Ct)).ExpectOk().Prop("source")["status"]?.ToString() ?? "(missing)";
            }
            ctx.Log($"Final crawl status: {status}");
            Expect.True(status == "completed", $"The crawl ended as \"{status}\" (completed_with_errors usually means embeddings failed).");
            (await api.GetAsync($"/api/knowledge/websites/{id}/pages", ct: ctx.Ct)).ExpectOk().Array();
            (await api.DeleteAsync($"/api/knowledge/websites/{id}", ctx.Ct)).ExpectStatus(204);
        }, risk: Risk.External)
        .Test("invalid-url", "A non-http URL is rejected", async ctx =>
        {
            (await (await ctx.ApiAsync()).PostAsync("/api/knowledge/websites", new { url = "file:///etc/passwd", crawlMode = "single_page" }, ct: ctx.Ct))
                .ExpectStatus(400);
        })
        .Build();
}

public static class CampaignsSuite
{
    public static TestSuite Build() => new SuiteBuilder("campaigns", "Campaigns", "Campaign list and audience preview. Sending is never tested here.")
        .Test("list", "Campaign list loads", async ctx =>
        {
            (await (await ctx.ApiAsync()).GetAsync("/api/campaigns", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Test("audience", "Audience preview counts eligible leads", async ctx =>
        {
            await ctx.Fixtures.LeadAsync();
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/api/campaigns/audience-preview?audienceType=all_eligible", ct: ctx.Ct)).ExpectOk().RequireJson();
            (await api.GetAsync("/api/campaigns/audience-preview/leads?audienceType=all_eligible&limit=10", ct: ctx.Ct)).ExpectOk().RequireJson();
        })
        .Test("create-needs-template", "Creating a campaign without an approved template is refused", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            (await (await ctx.ApiAsync()).PostAsync("/api/campaigns", new
            {
                name = $"Automation {ctx.Tag}", whatsAppTemplateId = Guid.NewGuid(), leadIds = new[] { leadId },
            }, ct: ctx.Ct)).ExpectStatus(400, 404, 422);
        })
        .Build();
}

public static class WhatsAppSuite
{
    public static TestSuite Build() => new SuiteBuilder("whatsapp", "WhatsApp", "WhatsApp health and templates for a clinic (no number connected).")
        .Test("health", "Health endpoint answers", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/api/whatsapp/health", ct: ctx.Ct)).ExpectStatus(200, 404);
            (await api.GetAsync("/api/whatsapp/health/events", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Test("templates", "Template list loads", async ctx =>
        {
            (await (await ctx.ApiAsync()).GetAsync("/api/whatsapp/templates", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Build();
}

public static class ChannelsSuite
{
    public static TestSuite Build() => new SuiteBuilder("channels", "Channels and webhooks", "Channel list and the public webhooks' handling of bad or unknown calls.")
        .Test("list", "Channel integrations list loads", async ctx =>
        {
            (await (await ctx.ApiAsync()).GetAsync("/api/channel-integrations", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Test("meta-verify-wrong", "WhatsApp webhook handshake refuses a wrong token", async ctx =>
        {
            (await ctx.Anonymous.GetAsync($"/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong-{ctx.Tag}&hub.challenge=123", ct: ctx.Ct))
                .ExpectStatus(403);
        }, Needs.None)
        .Test("meta-verify", "WhatsApp webhook handshake echoes the challenge", async ctx =>
        {
            if (string.IsNullOrWhiteSpace(ctx.Env.MetaWebhookVerifyToken)) throw new TestSkipped("No Meta webhook verify token configured for this environment.");
            var challenge = "auto" + ctx.Tag;
            var response = (await ctx.Anonymous.GetAsync(
                $"/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={Uri.EscapeDataString(ctx.Env.MetaWebhookVerifyToken)}&hub.challenge={challenge}", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(challenge, response.Body, "Echoed challenge");
        }, Needs.None)
        .Test("telegram-unknown", "Telegram webhook for an unknown connection returns 404", async ctx =>
        {
            (await ctx.Anonymous.PostRawAsync($"/api/integrations/telegram/webhook/{Guid.NewGuid()}", "{\"update_id\":1}", "application/json", ct: ctx.Ct))
                .ExpectStatus(404);
        }, Needs.None)
        .Test("tiktok-reachable", "TikTok webhook endpoint is reachable", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/integrations/tiktok/webhook", ct: ctx.Ct)).ExpectOk();
        }, Needs.None)
        .Build();
}

public static class CalendarSuite
{
    public static TestSuite Build() => new SuiteBuilder("calendar", "Calendar integrations", "Google / Outlook calendar connection status (no account is connected).")
        .Test("list", "Calendar integrations list loads", async ctx =>
        {
            (await (await ctx.ApiAsync()).GetAsync("/api/calendar-integrations", ct: ctx.Ct)).ExpectOk().Array();
        })
        .Test("connect-redirect", "Connect Google Calendar starts the sign-in redirect", async ctx =>
        {
            var response = (await (await ctx.ApiAsync()).GetAsync("/calendar-oauth/google/connect", ct: ctx.Ct)).ExpectStatus(302);
            ctx.Log($"Redirects to {response.Location?.Split('?')[0]}");
        })
        .Test("sync-callback-key", "Calendar sync callback refuses a missing ingest key", async ctx =>
        {
            (await ctx.Anonymous.PostAsync("/api/calendar-integrations/sync-callback", new { }, ct: ctx.Ct)).ExpectStatus(401, 500);
        }, Needs.None)
        .Build();
}

public static class BillingSuite
{
    public static TestSuite Build() => new SuiteBuilder("billing", "Billing (clinic)", "What a clinic sees on its billing page.")
        .Test("summary", "Billing summary loads", async ctx =>
        {
            var summary = (await (await ctx.ApiAsync()).GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk();
            ctx.Log($"billingEnabled={summary.Json?["billingEnabled"]}, plan={summary.Json?["planCode"]}, status={summary.Json?["subscriptionStatus"]}");
        })
        .Test("history", "Usage and transactions load", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/api/billing/usage", ct: ctx.Ct)).ExpectOk().RequireJson();
            (await api.GetAsync("/api/billing/transactions", ct: ctx.Ct)).ExpectOk().RequireJson();
        })
        .Build();
}
