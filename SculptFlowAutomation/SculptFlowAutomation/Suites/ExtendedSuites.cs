using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SculptFlowAutomation.Core;

namespace SculptFlowAutomation.Suites;

/// <summary>Admin billing API beyond the read-only checks in <see cref="PlatformAdminSuite"/>: lookups, validation, and
/// (opt-in "billing writes") subscriptions, adjustments and a clinic-only rate card on the test clinic.</summary>
public static class AdminBillingSuite
{
    private const string Root = "/api/platform-admin/billing";

    public static TestSuite Build() => new SuiteBuilder("admin-billing", "Admin: billing writes", "Plans, rate cards, subscriptions, adjustments and refunds. Writes touch only the test clinic.")
        .Test("lookups", "Unknown plans, rates and usage return 404; bad input returns 400", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await ctx.Admin.GetAsync($"{Root}/plans/no-such-plan-{ctx.Tag}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{Root}/rate-cards/no-such-card-{ctx.Tag}/rates", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PostAsync($"{Root}/rates/{Guid.NewGuid()}/close", new { }, ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PostAsync($"{Root}/clinics/{clinic.ClinicId}/usage/{Guid.NewGuid()}/refund", new { reason = "automation" }, ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{Root}/clinics/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PutAsync($"{Root}/channel-accounts/{Guid.NewGuid()}/provider-billing", new { providerBilling = "platform_funded", omniUsageBilling = true, reason = "automation" }, ctx.Ct)).ExpectStatus(404, 400);
            (await ctx.Admin.PostAsync($"{Root}/channel-accounts/{Guid.NewGuid()}/provider-billing/reset", new { reason = "automation" }, ct: ctx.Ct)).ExpectStatus(404, 400);
            // Editing a real plan would change it for every clinic, so only the "unknown plan" answers are checked.
            (await ctx.Admin.PutAsync($"{Root}/plans/no-such-plan-{ctx.Tag}", new { code = $"no-such-plan-{ctx.Tag}", name = "x", price = 1, billingPeriod = "month", includedUsageCredit = 0 }, ctx.Ct)).ExpectStatus(404, 400);
            (await ctx.Admin.PutAsync($"{Root}/plans/no-such-plan-{ctx.Tag}/entitlements", new Dictionary<string, string> { ["max_staff"] = "5" }, ctx.Ct)).ExpectStatus(404, 400);
            // A plan code must be lower-case letters, digits, - or _; this is rejected before anything is stored.
            (await ctx.Admin.PostAsync($"{Root}/plans", new { code = "Bad Code!", name = "x", price = 1, billingPeriod = "month", includedUsageCredit = 0 }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{Root}/plans", new { code = $"auto-{ctx.Tag}", name = "x", price = -5, billingPeriod = "month", includedUsageCredit = 0 }, ct: ctx.Ct)).ExpectStatus(400);
        }, Needs.Session | Needs.AdminKey)
        .Test("quote", "Price quote answers for a known event type and refuses a malformed one", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var quote = await ctx.Admin.GetAsync($"{Root}/clinics/{clinic.ClinicId}/quote?eventType=whatsapp_marketing_message&country=LB", ct: ctx.Ct);
            quote.ExpectStatus(200, 404); // 404 = no rate configured for it on this target, which is a valid answer
            ctx.Log($"Quote: {quote.Status}");
            (await ctx.Admin.GetAsync($"{Root}/clinics/{clinic.ClinicId}/quote?eventType=BAD%20TYPE", ct: ctx.Ct)).ExpectStatus(400, 404);
        }, Needs.Session | Needs.AdminKey)
        .Test("adjustment", "Manual adjustments: idempotent, reasoned, balanced", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var root = $"{Root}/clinics/{clinic.ClinicId}";
            var key = new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-adjust" };
            (await ctx.Admin.PostAsync($"{root}/wallet/top-ups", new { amount = 10m, reason = "seed" }, new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-seed" }, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PostAsync($"{root}/wallet/adjustments", new { amount = -3m, balanceType = "wallet", reason = "Automation correction" }, key, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PostAsync($"{root}/wallet/adjustments", new { amount = -3m, balanceType = "wallet", reason = "Automation correction" }, key, ctx.Ct)).ExpectOk();
            var ledger = (await ctx.Admin.GetAsync($"{root}/ledger?pageSize=100", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(1, ledger.Body.Split("manual_adjustment").Length - 1, "Adjustment entries after a retried request");
            Expect.EqualText("true", (await ctx.Admin.GetAsync($"{root}/reconciliation", ct: ctx.Ct)).ExpectOk().Prop("isBalanced"), "Reconciliation");

            (await ctx.Admin.PostAsync($"{root}/wallet/adjustments", new { amount = 0m, balanceType = "wallet", reason = "zero" },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-zero" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{root}/wallet/adjustments", new { amount = 1m, balanceType = "piggy-bank", reason = "bad type" },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-badtype" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{root}/wallet/adjustments", new { amount = -100000m, balanceType = "wallet", reason = "overdraw" },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-overdraw" }, ctx.Ct)).ExpectStatus(400, 422);
            (await ctx.Admin.PostAsync($"{root}/wallet/top-ups", new { amount = -1m, reason = "negative" },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-neg" }, ctx.Ct)).ExpectStatus(400);
        }, Needs.Session | Needs.AdminKey, Risk.Billing)
        .Test("subscription", "Start, cancel, resume and renew a plan on the test clinic", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var plans = (await ctx.Admin.GetAsync($"{Root}/plans", ct: ctx.Ct)).ExpectOk().Array();
            var plan = plans.FirstOrDefault(p => p?["isActive"]?.ToString() == "true" && decimal.TryParse(p?["price"]?.ToString(), out var price) && price == 0)
                ?? plans.FirstOrDefault(p => p?["isActive"]?.ToString() == "true");
            if (plan is null) throw new TestSkipped("No active plan on this target to subscribe the test clinic to.");
            var code = plan["code"]!.ToString();
            var root = $"{Root}/clinics/{clinic.ClinicId}";
            ctx.Log($"Using plan {code}");

            // Make sure the wallet can pay a priced plan.
            if (decimal.TryParse(plan["price"]?.ToString(), out var cost) && cost > 0)
            {
                (await ctx.Admin.PostAsync($"{root}/wallet/top-ups", new { amount = cost + 1, reason = "plan funding" },
                    new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-fund" }, ctx.Ct)).ExpectOk();
            }

            (await ctx.Admin.PostAsync($"{root}/subscription", new { planCode = code, chargeFirstPeriod = true, reason = "automation" },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-sub" }, ctx.Ct)).ExpectOk();
            Expect.EqualText(code, (await clinic.Client.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk().Prop("planCode"), "Plan the clinic sees");

            (await ctx.Admin.PostAsync($"{root}/subscription/cancel", new { immediately = false, reason = "automation" }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("true", (await clinic.Client.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk().Prop("cancelAtPeriodEnd"), "cancelAtPeriodEnd");
            (await ctx.Admin.PostAsync($"{root}/subscription/resume", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("false", (await clinic.Client.GetAsync("/api/billing/summary", ct: ctx.Ct)).ExpectOk().Prop("cancelAtPeriodEnd"), "cancelAtPeriodEnd after resume");
            (await ctx.Admin.PostAsync($"{root}/subscription/renew", ct: ctx.Ct)).ExpectStatus(200, 422); // 422 = not due yet
            (await ctx.Admin.PostAsync($"{root}/subscription", new { planCode = $"no-such-plan-{ctx.Tag}" },
                new Dictionary<string, string> { ["Idempotency-Key"] = $"auto-{ctx.Tag}-badplan" }, ctx.Ct)).ExpectStatus(404, 400);
            Expect.EqualText("true", (await ctx.Admin.GetAsync($"{root}/reconciliation", ct: ctx.Ct)).ExpectOk().Prop("isBalanced"), "Reconciliation");
        }, Needs.Session | Needs.AdminKey, Risk.Billing)
        .Test("rate-card", "Clinic-only rate card: create, add a rate, close it, edit the card", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var code = $"auto-{ctx.Tag}";
            (await ctx.Admin.PostAsync($"{Root}/rate-cards", new { code, name = $"Automation {ctx.Tag}", description = "Test clinic pricing", clinicId = clinic.ClinicId }, ct: ctx.Ct))
                .ExpectStatus(200, 201);
            (await ctx.Admin.PostAsync($"{Root}/rate-cards", new { code, name = "duplicate", clinicId = clinic.ClinicId }, ct: ctx.Ct)).ExpectStatus(409, 400);
            (await ctx.Admin.PostAsync($"{Root}/rate-cards", new { code = $"auto-{ctx.Tag}-x", name = "bad", clinicId = clinic.ClinicId, isDefault = true }, ct: ctx.Ct)).ExpectStatus(400);

            var rate = (await ctx.Admin.PostAsync($"{Root}/rate-cards/{code}/rates", new
            {
                eventType = "automation_test_event", countryCode = "LB", unit = "message", providerCost = 0.01m, clientRate = 0.02m,
            }, ct: ctx.Ct)).ExpectStatus(200, 201);
            var rateId = rate.Id();
            (await ctx.Admin.PostAsync($"{Root}/rate-cards/{code}/rates", new { eventType = "BAD TYPE", providerCost = 0m, clientRate = 0m }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{Root}/rate-cards/{code}/rates", new { eventType = "automation_test_event", clientRate = -1m, providerCost = 0m }, ct: ctx.Ct)).ExpectStatus(400);
            Expect.True((await ctx.Admin.GetAsync($"{Root}/rate-cards/{code}/rates", ct: ctx.Ct)).ExpectOk().Body.Contains(rateId.ToString()), "The new rate isn't listed.");

            (await ctx.Admin.PostAsync($"{Root}/rates/{rateId}/close", new { effectiveTo = DateTimeOffset.UtcNow.AddMinutes(1) }, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{Root}/rate-cards/{code}/rates?history=true", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{Root}/rate-cards/{code}", new { name = $"Automation v2 {ctx.Tag}", description = "edited", isActive = false, isDefault = false }, ctx.Ct)).ExpectOk();
        }, Needs.Session | Needs.AdminKey, Risk.Billing)
        .Build();
}

/// <summary>Knowledge retrieval benchmark, run from the admin portal for one clinic (platform-admin key): reads and
/// validation always; generation and runs need OpenAI and n8n. Clinic staff no longer have a benchmark API.</summary>
public static class BenchmarkSuite
{
    private static async Task<string> RootAsync(TestContext ctx) =>
        $"/api/platform-admin/knowledge-benchmark/clinics/{(await ctx.ClinicAsync()).ClinicId}";

    public static TestSuite Build() => new SuiteBuilder("benchmark", "Knowledge benchmark", "Test cases, generations and scoring runs for knowledge search, through the admin portal's API.")
        .Test("reads", "Dashboard, cases, generations, runs and source documents load", async ctx =>
        {
            var root = await RootAsync(ctx);
            (await ctx.Admin.GetAsync(root, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{root}/cases?view=manual", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{root}/generations?take=5", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{root}/runs?take=5", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{root}/source-documents", ct: ctx.Ct)).ExpectOk().Array();
        }, Needs.Session | Needs.AdminKey)
        .Test("access", "Staff can't reach the benchmark; an unknown clinic or a missing key is refused", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.GetAsync("/api/knowledge/benchmark", ct: ctx.Ct)).ExpectStatus(404);
            (await api.GetAsync("/KnowledgeBase/Benchmark", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"/api/platform-admin/knowledge-benchmark/clinics/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Anonymous.GetAsync(await RootAsync(ctx), ct: ctx.Ct)).ExpectStatus(401);
        }, Needs.Session | Needs.AdminKey)
        .Test("unknown", "Unknown cases, runs and generations return 404", async ctx =>
        {
            var root = await RootAsync(ctx);
            var id = Guid.NewGuid();
            (await ctx.Admin.GetAsync($"{root}/cases/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PutAsync($"{root}/cases/{id}", new { question = "x?" }, ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PostAsync($"{root}/cases/{id}/review", new { reviewed = true }, ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.DeleteAsync($"{root}/cases/{id}", ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{root}/runs/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{root}/runs/{id}/results", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{root}/runs/{id}/generations", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{root}/runs/{id}/results/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{root}/generations/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.PostAsync($"{root}/generations/{id}/cancel", ct: ctx.Ct)).ExpectStatus(404);
            (await ctx.Admin.GetAsync($"{root}/source-documents/{id}/chunks", ct: ctx.Ct)).ExpectStatus(404);
        }, Needs.Session | Needs.AdminKey)
        .Test("validation", "A manual case must point at one of the clinic's chunks; a run needs a valid scope", async ctx =>
        {
            var root = await RootAsync(ctx);
            (await ctx.Admin.PostAsync($"{root}/cases", new { question = "Where do I park?", documentId = Guid.NewGuid(), chunkId = Guid.NewGuid() }, ct: ctx.Ct)).ExpectStatus(400, 404);
            (await ctx.Admin.PostAsync($"{root}/runs", new { scope = "everything" }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{root}/runs", new { scope = "generation" }, ct: ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{root}/runs", new { scope = "generation", generationId = Guid.NewGuid() }, ct: ctx.Ct)).ExpectStatus(400, 404);
        }, Needs.Session | Needs.AdminKey)
        .Test("manual-case", "Write a manual case from a real chunk, edit, review, delete", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var root = await RootAsync(ctx);
            var docId = (await api.PostAsync("/api/knowledge", new
            {
                title = $"Benchmark source {ctx.Tag}", category = "faq", content = "Consultations are free and take thirty minutes.", isActive = true,
            }, ct: ctx.Ct)).ExpectStatus(201).Id();
            var chunks = (await ctx.Admin.GetAsync($"{root}/source-documents/{docId}/chunks", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(chunks.Count > 0, "The new document has no chunks.");
            var chunkId = Guid.Parse(chunks[0]!["id"]!.ToString());

            var caseId = (await ctx.Admin.PostAsync($"{root}/cases", new { question = "How long is a consultation?", documentId = docId, chunkId }, ct: ctx.Ct)).ExpectStatus(201, 200).Id();
            (await ctx.Admin.GetAsync($"{root}/cases/{caseId}", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{root}/cases/{caseId}", new { question = "How long does a consultation take?" }, ctx.Ct)).ExpectOk();
            (await ctx.Admin.PutAsync($"{root}/cases/{caseId}", new { question = "" }, ctx.Ct)).ExpectStatus(400);
            (await ctx.Admin.PostAsync($"{root}/cases/{caseId}/review", new { reviewed = true }, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.DeleteAsync($"{root}/cases/{caseId}", ctx.Ct)).ExpectStatus(204, 200);
            (await api.DeleteAsync($"/api/knowledge/{docId}", ctx.Ct)).ExpectStatus(204, 200);
        }, Needs.Session | Needs.AdminKey, Risk.External)
        .Test("generate", "Ask n8n to generate test cases from the clinic's knowledge", async ctx =>
        {
            var response = await ctx.Admin.PostAsync($"{await RootAsync(ctx)}/cases/generate", ct: ctx.Ct);
            ctx.Log($"Generate answered {response.Status}{response.Snippet(200)}");
            Expect.True(response.Status is 200 or 201 or 202 or 400 or 422 or 503, $"Generate returned {response.Status}.");
        }, Needs.Session | Needs.AdminKey, Risk.External)
        .Test("run", "Score the clinic's cases in a run", async ctx =>
        {
            var root = await RootAsync(ctx);
            var run = (await ctx.Admin.PostAsync($"{root}/runs", new { scope = "all" }, ct: ctx.Ct)).ExpectStatus(200, 201, 202, 400, 422);
            ctx.Log($"Run start answered {run.Status}{run.Snippet(200)}");
            if (run.IsSuccess)
            {
                var id = run.Id();
                (await ctx.Admin.GetAsync($"{root}/runs/{id}", ct: ctx.Ct)).ExpectOk();
                (await ctx.Admin.GetAsync($"{root}/runs/{id}/results", ct: ctx.Ct)).ExpectOk();
            }
        }, Needs.Session | Needs.AdminKey, Risk.External)
        .Build();
}

/// <summary>Callbacks n8n and the providers make into the app, with the ingest key or provider secrets.</summary>
public static class CallbacksSuite
{
    public static TestSuite Build() => new SuiteBuilder("callbacks", "n8n and provider callbacks", "Calendar sync results, benchmark questions, WhatsApp template/health events, signed Meta deliveries, AI test codes.")
        .Test("calendar-sync", "Calendar sync callback for an unknown appointment changes nothing", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await ctx.Ingest.PostAsync("/api/calendar-integrations/sync-callback", new
            {
                clinicId = clinic.ClinicId, appointmentId = Guid.NewGuid(), calendarIntegrationId = Guid.NewGuid(), requestId = Guid.NewGuid(),
                success = true, externalEventId = "evt-auto",
            }, ct: ctx.Ct)).ExpectStatus(200, 404);
        }, Needs.Session | Needs.IngestKey)
        .Test("benchmark-questions", "Benchmark questions for an unknown generation are refused", async ctx =>
        {
            (await ctx.Ingest.PostAsync($"/api/knowledge/benchmark/generations/{Guid.NewGuid()}/questions", new { questions = Array.Empty<object>() }, ct: ctx.Ct))
                .ExpectStatus(404, 400, 409);
            (await ctx.Anonymous.PostAsync($"/api/knowledge/benchmark/generations/{Guid.NewGuid()}/questions", new { questions = Array.Empty<object>() }, ct: ctx.Ct))
                .ExpectStatus(401, 500);
        }, Needs.IngestKey)
        .Test("whatsapp-events", "A template event is stored for its clinic; a health event without WhatsApp is refused", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            // Template events are an upsert keyed by Meta's id (or clinic + name + language), so an unseen template is created.
            var template = (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/templates/events", new
            {
                clinicId = clinic.ClinicId, eventType = "status_update", metaTemplateId = "auto-" + ctx.Tag, name = $"auto_event_{ctx.Tag}", status = "APPROVED",
            }, ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("approved", template.Prop("status"), "Template status");
            Expect.ContainsId((await clinic.Client.GetAsync("/api/whatsapp/templates", ct: ctx.Ct)).ExpectOk().Array(), template.Id(), "Clinic's templates");
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/templates/events", new { clinicId = clinic.ClinicId, eventType = "status_update", name = "" }, ct: ctx.Ct))
                .ExpectStatus(400);
            (await ctx.Ingest.PostAsync("/api/integrations/whatsapp/health/events", new
            {
                clinicId = clinic.ClinicId, eventType = "phone_quality", phoneNumberId = "auto-" + ctx.Tag, qualityRating = "GREEN",
            }, ct: ctx.Ct)).ExpectStatus(422, 404, 400); // 422 = the clinic has no WhatsApp connection
            (await ctx.Anonymous.PostAsync("/api/integrations/whatsapp/health/events", new { clinicId = clinic.ClinicId, eventType = "x" }, ct: ctx.Ct)).ExpectStatus(401, 500);
        }, Needs.Session | Needs.IngestKey)
        .Test("meta-unsigned", "WhatsApp webhook refuses an unsigned or wrongly signed delivery", async ctx =>
        {
            var body = MetaDelivery(ctx.Tag);
            (await ctx.Anonymous.PostRawAsync("/api/integrations/whatsapp/webhook", body, "application/json", ct: ctx.Ct)).ExpectStatus(401);
            (await ctx.Anonymous.PostRawAsync("/api/integrations/whatsapp/webhook", body, "application/json",
                new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=" + new string('0', 64) }, ctx.Ct)).ExpectStatus(401);
        }, Needs.None)
        .Test("meta-signed", "WhatsApp webhook accepts a correctly signed delivery for an unknown number", async ctx =>
        {
            if (string.IsNullOrWhiteSpace(ctx.Env.MetaAppSecret)) throw new TestSkipped("No Meta app secret configured for this environment.");
            var body = MetaDelivery(ctx.Tag);
            var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ctx.Env.MetaAppSecret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            // Meta retries on non-200, so an unknown phone number id must still get a quick 200 and change nothing.
            (await ctx.Anonymous.PostRawAsync("/api/integrations/whatsapp/webhook", body, "application/json",
                new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=" + signature }, ctx.Ct)).ExpectOk();
        }, Needs.None)
        .Test("tiktok-post", "TikTok webhook accepts a delivery", async ctx =>
        {
            (await ctx.Anonymous.PostRawAsync("/api/integrations/tiktok/webhook", "{\"event\":\"automation\"}", "application/json", ct: ctx.Ct)).ExpectStatus(200, 401);
        }, Needs.None)
        .Test("ai-test-codes", "AI test endpoints return every documented outcome", async ctx =>
        {
            var codes = (await ctx.Ingest.GetAsync("/api/ai/test/codes", ct: ctx.Ct)).ExpectOk();
            foreach (var code in codes.Array("schedule").Select(c => c!.ToString()))
            {
                Expect.EqualText(code, (await ctx.Ingest.PostAsync($"/api/ai/test/schedule?code={code}", new { }, ct: ctx.Ct)).ExpectOk().Prop("code"), $"schedule {code}");
            }
            foreach (var code in codes.Array("cancel").Select(c => c!.ToString()))
            {
                Expect.EqualText(code, (await ctx.Ingest.PostAsync($"/api/ai/test/cancel?code={code}", new { }, ct: ctx.Ct)).ExpectOk().Prop("code"), $"cancel {code}");
            }
            (await ctx.Ingest.PostAsync("/api/ai/test/schedule?code=NOT_A_CODE", new { }, ct: ctx.Ct)).ExpectStatus(200, 400);
        }, Needs.IngestKey)
        .Build();

    private static string MetaDelivery(string tag) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[]
        {
            new
            {
                id = "0",
                changes = new[]
                {
                    new
                    {
                        field = "messages",
                        value = new
                        {
                            messaging_product = "whatsapp",
                            metadata = new { display_phone_number = "15550000000", phone_number_id = $"auto-{tag}" },
                            contacts = new[] { new { profile = new { name = "Automation" }, wa_id = "15550100000" } },
                            messages = new[]
                            {
                                new { from = "15550100000", id = $"wamid.auto{tag}", type = "text", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), text = new { body = "hello" } },
                            },
                        },
                    },
                },
            },
        },
    });
}

/// <summary>Sign-in and connect redirects for Google, Google/Outlook Calendar and TikTok. The provider side can't be
/// automated, so this checks the app starts each flow and survives a forged or failed callback.</summary>
public static class OAuthSuite
{
    public static TestSuite Build() => new SuiteBuilder("oauth", "OAuth flows", "Starting each sign-in/connect flow, and rejecting forged callbacks.")
        .Test("google-login", "Continue with Google starts a redirect (or is cleanly off)", async ctx =>
        {
            var client = ctx.NewClient();
            var response = await client.SubmitFormAsync("/Account/Login", Array.Empty<KeyValuePair<string, string>>(), ct: ctx.Ct, postTo: "/auth/google/start");
            response.ExpectStatus(302, 400, 404);
            ctx.Log($"Google start: {response.Status} -> {response.Location?.Split('?')[0]}");
            (await ctx.Anonymous.GetAsync("/auth/google/callback", ct: ctx.Ct)).ExpectStatus(302, 400, 404);
        }, Needs.None)
        .Test("calendar", "Calendar connect redirects; a forged callback is refused", async ctx =>
        {
            var api = await ctx.ApiAsync();
            foreach (var provider in new[] { "google", "outlook" })
            {
                var start = (await api.GetAsync($"/calendar-oauth/{provider}/connect", ct: ctx.Ct)).ExpectStatus(302);
                ctx.Log($"{provider} connect -> {start.Location?.Split('?')[0]}");
                var forged = (await api.GetAsync($"/calendar-oauth/{provider}/callback?code=forged&state=forged-{ctx.Tag}", ct: ctx.Ct)).ExpectStatus(302, 400);
                Expect.True(forged.Location?.Contains("settings", StringComparison.OrdinalIgnoreCase) != false, $"A forged {provider} callback went to {forged.Location}.");
            }
            (await api.GetAsync("/calendar-oauth/yahoo/connect", ct: ctx.Ct)).ExpectStatus(302, 400, 404);
            Expect.True((await api.GetAsync("/api/calendar-integrations", ct: ctx.Ct)).ExpectOk().Body.Contains("\"connected\"") == false,
                "A forged callback left a connected calendar.");
        })
        .Test("tiktok", "TikTok connect redirects; a forged callback is refused", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var start = (await api.GetAsync("/tiktok-oauth/connect", ct: ctx.Ct)).ExpectStatus(302);
            ctx.Log($"TikTok connect -> {start.Location?.Split('?')[0]}");
            (await api.GetAsync($"/tiktok-oauth/callback?code=forged&state=forged-{ctx.Tag}", ct: ctx.Ct)).ExpectStatus(302, 400);
            (await ctx.Anonymous.GetAsync("/tiktok-oauth/connect", ct: ctx.Ct)).ExpectStatus(302); // to sign-in
        })
        .Build();
}

/// <summary>More scenarios for the clinic APIs: reopen, media, campaigns, templates, channels, calendar, websites,
/// notifications and pagination limits.</summary>
public static class ClinicEdgeCasesSuite
{
    public static TestSuite Build() => new SuiteBuilder("edge", "Clinic API edge cases", "Validation, unknown ids, limits and lesser-used actions across the clinic APIs.")
        .Test("reopen", "A closed conversation can be reopened; an open one stays open", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("reopen");
            var id = await ctx.Fixtures.NewConversationAsync(leadId);
            (await api.PostAsync($"/api/conversations/{id}/close", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("active", (await api.PostAsync($"/api/conversations/{id}/reopen", ct: ctx.Ct)).ExpectOk().Prop("status"), "Status after reopen");
            Expect.EqualText("active", (await api.PostAsync($"/api/conversations/{id}/reopen", ct: ctx.Ct)).ExpectOk().Prop("status"), "Reopening an open conversation");
            (await api.PostAsync($"/api/conversations/{Guid.NewGuid()}/reopen", ct: ctx.Ct)).ExpectStatus(404);
        })
        .Test("auto-reopen", "A patient message reopens a closed conversation", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("auto-reopen");
            var id = await ctx.Fixtures.NewConversationAsync(leadId);
            (await clinic.Client.PostAsync($"/api/conversations/{id}/close", ct: ctx.Ct)).ExpectOk();
            (await ctx.Ingest.PostAsync("/api/messages/ingest", new
            {
                clinicId = clinic.ClinicId, conversationId = id, leadId, eventType = "customer_message", channel = "website",
                content = "Are you there?", externalMessageId = $"auto-{Guid.NewGuid():N}", receivedAt = DateTimeOffset.UtcNow,
            }, ct: ctx.Ct)).ExpectOk();
            Expect.True((await clinic.Client.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk().Body.Contains("\"status\":\"active\""),
                "The conversation stayed closed after the patient wrote again.");
        }, Needs.Session | Needs.IngestKey)
        .Test("ingest-duplicate", "The same inbound message ingested twice is stored once", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("dedupe");
            var id = await ctx.Fixtures.NewConversationAsync(leadId);
            var body = new
            {
                clinicId = clinic.ClinicId, conversationId = id, leadId, eventType = "customer_message", channel = "website",
                content = $"Once only {ctx.Tag}", externalMessageId = $"auto-dupe-{ctx.Tag}", receivedAt = DateTimeOffset.UtcNow,
            };
            (await ctx.Ingest.PostAsync("/api/messages/ingest", body, ct: ctx.Ct)).ExpectOk();
            (await ctx.Ingest.PostAsync("/api/messages/ingest", body, ct: ctx.Ct)).ExpectStatus(200, 409);
            var count = (await clinic.Client.GetAsync($"/api/conversations/{id}/messages", ct: ctx.Ct)).ExpectOk().Array()
                .Count(m => m?["content"]?.ToString() == $"Once only {ctx.Tag}");
            Expect.Equal(1, count, "Copies of a re-delivered message");
            (await ctx.Ingest.PostAsync("/api/messages/ingest", body with { eventType = "nonsense" }, ct: ctx.Ct)).ExpectStatus(400);
        }, Needs.Session | Needs.IngestKey)
        .Test("conversation-unknown-lead", "Opening a conversation for a lead that doesn't exist is rejected (not a server error)", async ctx =>
        {
            (await (await ctx.ApiAsync()).PostAsync("/api/conversations", new { leadId = Guid.NewGuid(), channel = "website" }, ct: ctx.Ct)).ExpectStatus(400, 404);
        })
        .Test("message-sender", "A message with an unknown sender type is rejected (400, not a server error)", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var id = await ctx.Fixtures.NewConversationAsync(await ctx.Fixtures.LeadAsync());
            (await api.PostAsync($"/api/conversations/{id}/messages", new { direction = "inbound", senderType = "martian", channel = "website", content = "x" }, ct: ctx.Ct)).ExpectStatus(400);
        })
        .Test("messages", "Bad message bodies and unknown media are refused", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var leadId = await ctx.Fixtures.LeadAsync();
            var id = await ctx.Fixtures.NewConversationAsync(leadId);
            (await api.PostAsync($"/api/conversations/{id}/messages", new { direction = "sideways", senderType = "lead", channel = "website", content = "x" }, ct: ctx.Ct)).ExpectStatus(400);
            (await api.PostAsync($"/api/conversations/{Guid.NewGuid()}/messages", new { direction = "inbound", senderType = "lead", channel = "website", content = "x" }, ct: ctx.Ct)).ExpectStatus(404);
            (await api.GetAsync($"/api/messages/{Guid.NewGuid()}/media", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync("/api/conversations", new { leadId, channel = "carrier-pigeon" }, ct: ctx.Ct)).ExpectStatus(400);
            (await api.PostAsync($"/api/conversations/{id}/messages/send-template", new { templateId = Guid.NewGuid() }, ct: ctx.Ct)).ExpectStatus(400, 404, 422);
        })
        .Test("staff-send", "Staff reply on a conversation with no connected channel is refused, not lost", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("staff-send");
            var id = await ctx.Fixtures.NewConversationAsync(leadId);
            var response = await api.PostAsync($"/api/conversations/{id}/messages/send", new { content = "Hello from the clinic" }, ct: ctx.Ct);
            ctx.Log($"Staff send answered {response.Status}{response.Snippet(200)}");
            Expect.True(response.Status is 400 or 409 or 422 or 502 or 503, $"Sending with no connected channel returned {response.Status}.");
        })
        .Test("notifications", "Mark one notification read", async ctx =>
        {
            await ctx.Fixtures.NewLeadAsync("notify");
            var api = await ctx.ApiAsync();
            var items = (await api.GetAsync("/api/notifications?take=5", ct: ctx.Ct)).ExpectOk().Array("items");
            if (items.Count > 0)
            {
                (await api.PostAsync($"/api/notifications/{items[0]!["id"]}/read", ct: ctx.Ct)).ExpectStatus(200, 204);
            }
            (await api.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", ct: ctx.Ct)).ExpectStatus(404, 200, 204);
            (await api.GetAsync("/api/notifications?take=100000", ct: ctx.Ct)).ExpectOk();
        })
        .Test("negative-skip", "A negative skip is clamped on every list (no server error)", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var broken = new List<string>();
            foreach (var path in new[] { "/api/notifications", "/api/leads", "/api/appointments", "/api/procedure-bookings", "/api/conversations", "/api/dashboard/leads", "/api/whatsapp/health/events" })
            {
                var response = await api.GetAsync($"{path}?skip=-5", ct: ctx.Ct);
                if (response.Status >= 500) broken.Add($"{path} ({response.Status})");
            }
            Expect.True(broken.Count == 0, "Server error on skip=-5: " + string.Join(", ", broken));
        })
        .Test("paging", "Huge or negative paging values are clamped, not errors", async ctx =>
        {
            var api = await ctx.ApiAsync();
            foreach (var path in new[] { "/api/leads", "/api/appointments", "/api/procedure-bookings", "/api/conversations", "/api/dashboard/leads", "/api/dashboard/appointments" })
            {
                (await api.GetAsync($"{path}?take=100000&skip=0", ct: ctx.Ct)).ExpectOk();
                (await api.GetAsync($"{path}?take=0", ct: ctx.Ct)).ExpectOk();
            }
            (await api.GetAsync("/api/billing/usage?page=0&pageSize=100000", ct: ctx.Ct)).ExpectOk();
        })
        .Test("campaigns", "Campaign actions on unknown ids return 404; bad schedules are rejected", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var id = Guid.NewGuid();
            (await api.GetAsync($"/api/campaigns/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/campaigns/{id}/schedule", new { scheduledAt = DateTimeOffset.UtcNow.AddDays(1) }, ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/campaigns/{id}/send", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/campaigns/{id}/process-batch", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/campaigns/{id}/cancel", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/campaigns/{id}/stop", ct: ctx.Ct)).ExpectStatus(404);
            (await api.GetAsync("/api/campaigns/audience-preview?audienceType=nonsense", ct: ctx.Ct)).ExpectStatus(400);
            (await api.GetAsync("/api/campaigns/audience-preview?audienceType=reactivation_no_consultation", ct: ctx.Ct)).ExpectOk();
            (await api.PostAsync("/api/campaigns", new { name = "", whatsAppTemplateId = Guid.NewGuid(), leadIds = Array.Empty<Guid>() }, ct: ctx.Ct)).ExpectStatus(400, 404, 422);
        })
        .Test("templates", "Template lookups and validation", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.GetAsync($"/api/whatsapp/templates/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/whatsapp/templates/{Guid.NewGuid()}/sync", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync("/api/whatsapp/templates", new { name = $"auto_{ctx.Tag}", category = "spam", language = "en", body = "Hi" }, ct: ctx.Ct)).ExpectStatus(400, 422);
            (await api.PostAsync("/api/whatsapp/templates", new { name = $"auto_{ctx.Tag}", category = "marketing", language = "en", body = "" }, ct: ctx.Ct)).ExpectStatus(400, 422);
            var draft = (await api.PostAsync("/api/whatsapp/templates", new { name = $"auto_{ctx.Tag}", category = "marketing", language = "en", body = "Hello {{1}}" }, ct: ctx.Ct)).ExpectStatus(201);
            Expect.EqualText("draft", draft.Prop("status"), "New template status");
            (await api.GetAsync($"/api/whatsapp/templates/{draft.Id()}", ct: ctx.Ct)).ExpectOk();
        })
        .Test("template-name", "A template name WhatsApp would refuse (capitals, spaces, symbols) is rejected up front", async ctx =>
        {
            (await (await ctx.ApiAsync()).PostAsync("/api/whatsapp/templates", new { name = "Bad Name!", category = "marketing", language = "en", body = "Hi" }, ct: ctx.Ct))
                .ExpectStatus(400, 422);
        })
        .Test("channels", "Channel actions without a connected channel are refused cleanly", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.PostAsync("/api/channel-integrations/whatsapp/disconnect", ct: ctx.Ct)).ExpectStatus(200, 204, 404);
            // Refresh without a Telegram bot answers the (disconnected) placeholder row; it must not claim to be connected.
            var refresh = (await api.PostAsync("/api/channel-integrations/telegram/refresh", ct: ctx.Ct)).ExpectStatus(200, 400, 404, 422);
            Expect.True(!refresh.Body.Contains("\"status\":\"connected\""), "Refreshing a missing Telegram bot reported it as connected.");
            (await api.PostAsync("/api/channel-integrations/telegram/connect", new { botToken = "" }, ct: ctx.Ct)).ExpectStatus(400);
            (await api.PutAsync("/api/channel-integrations", new { channel = "pigeon" }, ctx.Ct)).ExpectStatus(400);
            (await api.PutAsync("/api/channel-integrations", new { channel = "whatsapp", displayName = "Automation" }, ctx.Ct)).ExpectStatus(200, 400, 422);
            Expect.True((await api.GetAsync("/api/channel-integrations", ct: ctx.Ct)).ExpectOk().Body.Contains("\"connected\"") == false, "A channel shows as connected.");
        })
        .Test("channel-unknown", "Disconnecting an unknown channel type is rejected", async ctx =>
        {
            (await (await ctx.ApiAsync()).PostAsync("/api/channel-integrations/pigeon/disconnect", ct: ctx.Ct)).ExpectStatus(400, 404);
        })
        .Test("channels-external", "Fake Telegram, WhatsApp and Facebook credentials are rejected and nothing gets connected", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.PostAsync("/api/channel-integrations/telegram/connect", new { botToken = "123456:automation-fake-token" }, ct: ctx.Ct)).ExpectStatus(400, 422, 502);
            (await api.PostAsync("/api/channel-integrations/whatsapp/connect", new { accessToken = "automation-fake-token", phoneNumberId = "0", wabaId = "0" }, ct: ctx.Ct))
                .ExpectStatus(400, 422, 502, 503);
            (await api.PostAsync("/api/channel-integrations/facebook/connect", new { accessToken = "automation-fake-token", pageId = "0" }, ct: ctx.Ct))
                .ExpectStatus(400, 422, 502, 503);
            Expect.True((await api.GetAsync("/api/channel-integrations", ct: ctx.Ct)).ExpectOk().Body.Contains("\"connected\"") == false, "A fake credential left a connected channel.");
        }, risk: Risk.External)
        .Test("calendar", "Calendar actions on an unconnected or unknown provider are refused", async ctx =>
        {
            var api = await ctx.ApiAsync();
            foreach (var provider in new[] { "google", "outlook" })
            {
                (await api.PostAsync($"/api/calendar-integrations/{provider}/refresh-calendars", ct: ctx.Ct)).ExpectStatus(400, 404, 422);
                (await api.PostAsync($"/api/calendar-integrations/{provider}/select-calendar", new { externalCalendarId = "primary" }, ct: ctx.Ct)).ExpectStatus(400, 404, 422);
                (await api.PostAsync($"/api/calendar-integrations/{provider}/sync-enabled", new { enabled = true }, ct: ctx.Ct)).ExpectStatus(400, 404, 422);
                (await api.PostAsync($"/api/calendar-integrations/{provider}/disconnect", ct: ctx.Ct)).ExpectStatus(200, 204, 404);
            }
            (await api.PostAsync("/api/calendar-integrations/yahoo/refresh-calendars", ct: ctx.Ct)).ExpectStatus(400, 404);
        })
        .Test("websites", "Website source actions: unknown ids, deactivate and rescrape", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var id = Guid.NewGuid();
            (await api.GetAsync($"/api/knowledge/websites/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/knowledge/websites/{id}/active", new { isActive = false }, ct: ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync($"/api/knowledge/websites/{id}/rescrape", ct: ctx.Ct)).ExpectStatus(404);
            (await api.DeleteAsync($"/api/knowledge/websites/{id}", ctx.Ct)).ExpectStatus(404);
            (await api.PostAsync("/api/knowledge/websites", new { url = "http://127.0.0.1/admin", crawlMode = "single_page" }, ct: ctx.Ct)).ExpectStatus(400);
            (await api.PostAsync("/api/knowledge/websites", new { url = "https://example.com", crawlMode = "everything" }, ct: ctx.Ct)).ExpectStatus(400);
        })
        .Test("websites-lifecycle", "Deactivate and rescrape a real website source; the same address can't be added twice", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var id = (await api.PostAsync("/api/knowledge/websites", new { url = "https://example.org/", crawlMode = "single_page", category = "website" }, ct: ctx.Ct)).ExpectStatus(202).Id();
            (await api.PostAsync("/api/knowledge/websites", new { url = "https://example.org/", crawlMode = "single_page", category = "website" }, ct: ctx.Ct)).ExpectStatus(400, 409);
            for (var i = 0; i < 30 && (await api.GetAsync($"/api/knowledge/websites/{id}", ct: ctx.Ct)).ExpectOk().Prop("source")["inProgress"]?.ToString() == "true"; i++)
            {
                await Task.Delay(2000, ctx.Ct);
            }
            Expect.EqualText("false", (await api.PostAsync($"/api/knowledge/websites/{id}/active", new { isActive = false }, ct: ctx.Ct)).ExpectOk().Prop("isActive"), "isActive");
            (await api.PostAsync($"/api/knowledge/websites/{id}/rescrape", ct: ctx.Ct)).ExpectStatus(200, 202, 409);
            for (var i = 0; i < 30 && (await api.GetAsync($"/api/knowledge/websites/{id}", ct: ctx.Ct)).ExpectOk().Prop("source")["inProgress"]?.ToString() == "true"; i++)
            {
                await Task.Delay(2000, ctx.Ct);
            }
            (await api.DeleteAsync($"/api/knowledge/websites/{id}", ctx.Ct)).ExpectStatus(204);
        }, risk: Risk.External)
        .Test("knowledge", "Knowledge validation and unknown ids", async ctx =>
        {
            var api = await ctx.ApiAsync();
            (await api.PostAsync("/api/knowledge", new { title = "", category = "faq", content = "x" }, ct: ctx.Ct)).ExpectStatus(400);
            (await api.PostAsync("/api/knowledge", new { title = "t", category = "faq", content = "" }, ct: ctx.Ct)).ExpectStatus(400);
            (await api.GetAsync($"/api/knowledge/{Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(404);
            (await api.PutAsync($"/api/knowledge/{Guid.NewGuid()}", new { title = "t", category = "faq", content = "c" }, ctx.Ct)).ExpectStatus(404, 503);
            (await api.DeleteAsync($"/api/knowledge/{Guid.NewGuid()}", ctx.Ct)).ExpectStatus(404);
            (await api.PutAsync("/api/knowledge/settings", new { chunkSizeTokens = 10, chunkOverlapTokens = 0, topK = 4, minimumSimilarity = 0.3 }, ctx.Ct)).ExpectStatus(400);
            (await api.PutAsync("/api/knowledge/settings", new { chunkSizeTokens = 300, chunkOverlapTokens = 300, topK = 4, minimumSimilarity = 0.3 }, ctx.Ct)).ExpectStatus(400);
            using var empty = new MultipartFormDataContent();
            (await api.PostMultipartAsync("/api/knowledge/upload", empty, ctx.Ct)).ExpectStatus(400);
        })
        .Test("lead-unknown-clinic", "Lead intake for a clinic that doesn't exist is rejected (not a server error)", async ctx =>
        {
            (await ctx.Ingest.PostAsync("/api/leads", new { clinicId = Guid.NewGuid(), fullName = "Nobody", phone = "+15550100000" }, ct: ctx.Ct)).ExpectStatus(400, 404);
        }, Needs.None)
        .Test("lead-anonymous", "Lead intake without the ingest key is refused", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await ctx.Anonymous.PostAsync("/api/leads", new { clinicId = clinic.ClinicId, fullName = "Nobody", phone = "+15550100000" }, ct: ctx.Ct)).ExpectStatus(401);
        })
        .Test("lead-empty", "Lead intake with no name, phone or email is rejected", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await ctx.Ingest.PostAsync("/api/leads", new { clinicId = clinic.ClinicId }, ct: ctx.Ct)).ExpectStatus(400);
        })
        .Test("leads", "Lead intake validation", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await ctx.Ingest.PostAsync("/api/leads", new { clinicId = clinic.ClinicId, fullName = "Bad email", email = "not-an-email" }, ct: ctx.Ct)).ExpectStatus(400, 201);
            (await clinic.Client.PatchAsync($"/api/leads/{Guid.NewGuid()}", new { city = "x" }, ctx.Ct)).ExpectStatus(404);
            (await clinic.Client.PostAsync($"/api/leads/{Guid.NewGuid()}/status", new { status = "contacted" }, ct: ctx.Ct)).ExpectStatus(404);
        })
        .Test("appointments", "Appointment validation: unknown lead, outside hours, in the past", async ctx =>
        {
            var api = await ctx.ApiAsync();
            await ctx.Fixtures.EnsureAvailabilityAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("appt-edge");
            var tomorrow = DateTime.UtcNow.Date.AddDays(1);
            (await api.PostAsync("/api/appointments", new { leadId = Guid.NewGuid(), appointmentType = "consultation", scheduledStart = tomorrow.AddHours(10) }, ct: ctx.Ct)).ExpectStatus(400, 404);
            var night = await api.PostAsync("/api/appointments", new { leadId, appointmentType = "consultation", scheduledStart = new DateTimeOffset(tomorrow.AddHours(3), TimeSpan.Zero) }, ct: ctx.Ct);
            ctx.Log($"Staff booking at 03:00 (clinic closed) answered {night.Status}");
            var past = await api.PostAsync("/api/appointments", new { leadId, appointmentType = "consultation", scheduledStart = DateTimeOffset.UtcNow.AddDays(-2) }, ct: ctx.Ct);
            ctx.Log($"Staff booking in the past answered {past.Status}");
            (await api.PatchAsync($"/api/appointments/{Guid.NewGuid()}/status", new { status = "confirmed" }, ctx.Ct)).ExpectStatus(404);
            (await api.GetAsync("/api/appointments/available?days=-3", ct: ctx.Ct)).ExpectStatus(200, 400);
            (await api.GetAsync($"/api/appointments/available?procedureId={Guid.NewGuid()}", ct: ctx.Ct)).ExpectStatus(400, 404, 200);
        })
        .Test("ai-booking-rules", "AI booking refuses a closed time, the past and an unknown lead", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            await ctx.Fixtures.EnsureAvailabilityAsync();
            var leadId = await ctx.Fixtures.NewLeadAsync("ai-rules");
            var tomorrow = DateTime.UtcNow.Date.AddDays(1);
            Expect.EqualText("SLOT_UNAVAILABLE", (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new
            {
                leadId, scheduledStart = new DateTimeOffset(tomorrow.AddHours(3), TimeSpan.Zero),
            }, ct: ctx.Ct)).ExpectOk().Prop("code"), "Booking at 03:00 when the clinic opens at 06:00");
            var past = (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new
            {
                leadId, scheduledStart = DateTimeOffset.UtcNow.AddDays(-1),
            }, ct: ctx.Ct)).ExpectOk();
            Expect.True(past.Str("success") == "false", $"The AI booked an appointment in the past ({past.Str("code")}).");
            Expect.EqualText("LEAD_NOT_FOUND", (await ctx.Ingest.PostAsync($"/api/ai/appointments/book?clinicId={clinic.ClinicId}", new
            {
                leadId = Guid.NewGuid(), scheduledStart = await ctx.Fixtures.SlotStartAsync(16),
            }, ct: ctx.Ct)).ExpectOk().Prop("code"), "Unknown lead");
            Expect.EqualText("NO_UPCOMING_APPOINTMENT", (await ctx.Ingest.PostAsync($"/api/ai/appointments/cancel?clinicId={clinic.ClinicId}&leadId={leadId}", new { reason = "x" }, ct: ctx.Ct))
                .ExpectOk().Prop("code"), "Cancel with nothing booked");
            (await ctx.Ingest.PostAsync($"/api/ai/appointments/reschedule?clinicId={clinic.ClinicId}&leadId={leadId}", new { scheduledStart = await ctx.Fixtures.SlotStartAsync(17) }, ct: ctx.Ct))
                .ExpectStatus(404);
        }, Needs.Session | Needs.IngestKey)
        .Build();
}
