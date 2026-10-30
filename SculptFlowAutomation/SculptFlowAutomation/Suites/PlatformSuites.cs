using SculptFlowAutomation.Core;

namespace SculptFlowAutomation.Suites;

public static class HealthSuite
{
    public static TestSuite Build() => new SuiteBuilder("health", "Health", "Is the app up and serving pages? No data is created.")
        .Test("root", "Home redirects to the dashboard", async ctx =>
        {
            var response = (await ctx.Anonymous.GetAsync("/", ct: ctx.Ct)).ExpectStatus(302);
            Expect.True(response.Location?.Contains("dashboard", StringComparison.OrdinalIgnoreCase) == true,
                $"\"/\" should redirect to /dashboard, went to {response.Location}.");
        }, Needs.None)
        .Test("login-page", "Sign-in page loads", async ctx =>
        {
            var response = (await ctx.Anonymous.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk().ExpectBodyContains("<form");
            Expect.True(Http.SculptFlowClient.AntiforgeryToken(response.Body) is not null, "Sign-in form has no antiforgery token.");
        }, Needs.None)
        .Test("register-page", "Sign-up page loads", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/Account/Register", ct: ctx.Ct)).ExpectOk().ExpectBodyContains("ClinicName");
        }, Needs.None)
        .Test("response-time", "Sign-in page answers within 3 seconds", async ctx =>
        {
            var response = (await ctx.Anonymous.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk();
            Expect.True(response.ElapsedMs < 3000, $"Took {response.ElapsedMs} ms (limit 3000 ms). A cold start on Render can cause this; run again to confirm.");
        }, Needs.None)
        .Test("dashboard-requires-login", "Dashboard sends signed-out visitors to sign-in", async ctx =>
        {
            var response = (await ctx.Anonymous.GetAsync("/dashboard", ct: ctx.Ct)).ExpectStatus(302);
            Expect.True(response.Location?.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) == true,
                $"Expected a redirect to /Account/Login, got {response.Location}.");
        }, Needs.None)
        .Build();
}

public static class AuthSuite
{
    public static TestSuite Build() => new SuiteBuilder("auth", "Sign-up and sign-in", "Registers the run's test clinic, then signs in and out with it.")
        .Test("register", "Sign up a new clinic", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            Expect.True(clinic.ClinicId != Guid.Empty, "Registration finished without a clinic id.");
            (await clinic.Client.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
        })
        .Test("login", "Sign in with email and password", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var client = ctx.NewClient();
            var response = await client.SubmitFormAsync("/Account/Login", new Dictionary<string, string>
            {
                ["Email"] = clinic.Email,
                ["Password"] = clinic.Password,
            }, ct: ctx.Ct);
            response.ExpectStatus(302);
            (await client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectOk();
        })
        .Test("wrong-password", "Wrong password is rejected", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var client = ctx.NewClient();
            var response = await client.SubmitFormAsync("/Account/Login", new Dictionary<string, string>
            {
                ["Email"] = clinic.Email,
                ["Password"] = clinic.Password + "-wrong",
            }, ct: ctx.Ct);
            response.ExpectOk(); // the page redisplays with an error instead of redirecting
            Expect.True(TestContext.PageError(response.Body) is not null, "No error message was shown for a wrong password.");
            (await client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectRefused();
        })
        .Test("duplicate-email", "Signing up twice with one email is refused", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var client = ctx.NewClient();
            var response = await client.SubmitFormAsync("/Account/Register", new Dictionary<string, string>
            {
                ["FullName"] = "Automation duplicate",
                ["ClinicName"] = $"{TestContext.ClinicNamePrefix} duplicate {ctx.Tag}",
                ["Email"] = clinic.Email,
                ["Password"] = clinic.Password,
                ["ConfirmPassword"] = clinic.Password,
            }, ct: ctx.Ct);
            Expect.True(response.Status != 302, "A second clinic was registered with an email that is already in use.");
        })
        .Test("logout", "Sign out ends the session", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            var client = ctx.NewClient();
            (await client.SubmitFormAsync("/Account/Login", new Dictionary<string, string>
            {
                ["Email"] = clinic.Email,
                ["Password"] = clinic.Password,
            }, ct: ctx.Ct)).ExpectStatus(302);

            // The sign-out form lives in the layout of every signed-in page.
            (await client.SubmitFormAsync("/dashboard", Array.Empty<KeyValuePair<string, string>>(), ct: ctx.Ct, postTo: "/Account/Logout"))
                .ExpectStatus(302);
            (await client.GetAsync("/api/staff", ct: ctx.Ct)).ExpectRefused();
        })
        .Build();
}

public static class SecuritySuite
{
    private static readonly string[] ProtectedGets =
    {
        "/api/leads", "/api/procedures", "/api/appointments", "/api/conversations", "/api/notifications",
        "/api/dashboard/summary", "/api/knowledge", "/api/campaigns", "/api/staff", "/api/billing/summary",
        "/api/channel-integrations", "/api/calendar-integrations",
    };

    public static TestSuite Build() => new SuiteBuilder("security", "Security", "Signed-out callers, wrong keys and one clinic reaching into another are all refused.")
        .Test("signed-out", "Clinic APIs refuse signed-out callers", async ctx =>
        {
            var leaks = new List<string>();
            foreach (var path in ProtectedGets)
            {
                var response = await ctx.Anonymous.GetAsync(path, ct: ctx.Ct);
                if (response.IsSuccess) leaks.Add($"{path} ({response.Status})");
            }
            Expect.True(leaks.Count == 0, "Answered without sign-in: " + string.Join(", ", leaks));
        }, Needs.None)
        .Test("ingest-key", "AI/n8n endpoints refuse a missing or wrong ingest key", async ctx =>
        {
            var anyId = Guid.NewGuid();
            (await ctx.Anonymous.GetAsync($"/api/ai/clinic-info?clinicId={anyId}", ct: ctx.Ct)).ExpectStatus(401, 500);
            var wrong = ctx.NewClient(new Dictionary<string, string> { ["X-Ingest-Key"] = "wrong-" + ctx.Tag });
            (await wrong.GetAsync($"/api/ai/clinic-info?clinicId={anyId}", ct: ctx.Ct)).ExpectStatus(401, 500);
            (await wrong.PostAsync("/api/messages/ingest", new
            {
                clinicId = anyId, conversationId = anyId, leadId = anyId, eventType = "customer_message", channel = "website", content = "x",
            }, ct: ctx.Ct)).ExpectStatus(401, 500);
        }, Needs.None)
        .Test("admin-key", "Platform-admin API refuses a missing or wrong key", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/platform-admin/billing/plans", ct: ctx.Ct)).ExpectStatus(401, 404);
            var wrong = ctx.NewClient(new Dictionary<string, string> { ["X-Platform-Admin-Key"] = "wrong-" + ctx.Tag });
            (await wrong.GetAsync("/api/platform-admin/billing/plans", ct: ctx.Ct)).ExpectStatus(401, 404);
            (await wrong.DeleteAsync($"/api/platform-admin/automation/clinics/{Guid.NewGuid()}", ctx.Ct)).ExpectStatus(401, 404);
        }, Needs.None)
        .Test("ai-send-needs-key", "AI-origin message send refuses a missing ingest key", async ctx =>
        {
            (await ctx.Anonymous.PostAsync($"/api/conversations/{Guid.NewGuid()}/messages/send?clinicId={Guid.NewGuid()}",
                new { sender = "ai", content = "hello" }, ct: ctx.Ct)).ExpectStatus(401);
        }, Needs.None)
        .Test("tenant-isolation", "One clinic can't read or change another clinic's data", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            var owner = await ctx.ClinicAsync();
            var other = await ctx.RegisterClinicAsync("other");

            (await other.Client.GetAsync($"/api/leads/{leadId}", ct: ctx.Ct)).ExpectStatus(404);
            (await other.Client.PatchAsync($"/api/leads/{leadId}", new { notes = "cross-clinic write" }, ctx.Ct)).ExpectStatus(404);
            (await other.Client.GetAsync($"/api/procedures/{owner.BaseProcedureId}", ct: ctx.Ct)).ExpectStatus(404);
            var list = (await other.Client.GetAsync("/api/leads?take=200", ct: ctx.Ct)).ExpectOk();
            Expect.LacksId(list.Array("items"), leadId, "Other clinic's lead list");

            // The owner still sees its own lead untouched.
            var lead = (await owner.Client.GetAsync($"/api/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
            Expect.True(lead.Json?["notes"]?.ToString() != "cross-clinic write", "Another clinic changed this clinic's lead.");
        })
        .Build();
}

/// <summary>Opens every signed-in page so a broken view (500) shows up even when its APIs are fine.</summary>
public static class PagesSuite
{
    private static readonly (string Id, string Name, string Path)[] Pages =
    {
        ("dashboard", "Dashboard", "/dashboard"),
        ("leads", "Leads", "/dashboard/leads"),
        ("appointments", "Appointments", "/dashboard/appointments"),
        ("inbox", "Inbox", "/inbox"),
        ("procedures", "Procedures", "/Procedures"),
        ("procedure-new", "New procedure form", "/Procedures/Edit"),
        ("knowledge", "Knowledge base", "/KnowledgeBase"),
        ("knowledge-new", "New knowledge entry form", "/KnowledgeBase/Edit"),
        ("knowledge-settings", "Knowledge settings", "/KnowledgeBase/Settings"),
        ("campaigns", "Campaigns", "/Campaigns"),
        ("campaign-new", "New campaign form", "/Campaigns/Create"),
        ("staff", "Staff", "/Staff"),
        ("whatsapp-templates", "WhatsApp templates", "/WhatsApp/Templates"),
        ("whatsapp-health", "WhatsApp health", "/WhatsApp/Health"),
        ("clinic-info", "Clinic info", "/settings/clinic-info"),
        ("availability", "Opening hours", "/settings/clinic-info?tab=availability"),
        ("integrations", "Integrations", "/settings/integrations"),
        ("calendar", "Calendar integrations", "/settings/calendar-integrations"),
        ("billing", "Billing", "/settings/billing"),
    };

    public static TestSuite Build()
    {
        var builder = new SuiteBuilder("pages", "Pages", "Every signed-in page of the clinic dashboard renders (200).");
        foreach (var (id, name, path) in Pages)
        {
            builder.Test(id, $"{name} page", async ctx =>
            {
                var api = await ctx.ApiAsync();
                var response = await api.GetAsync(path, ct: ctx.Ct);
                response.ExpectOk();
                Expect.True(response.ContentType?.Contains("html") == true, $"{path} didn't return HTML.");
            });
        }
        builder.Test("unknown-details", "Detail pages for ids that don't exist answer not-found, not an error", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var bad = new List<string>();
            foreach (var path in new[] { "/Campaigns/{0}", "/KnowledgeBase/Websites/{0}", "/KnowledgeBase/Benchmark/Generations/{0}", "/KnowledgeBase/Edit/{0}",
                         "/Procedures/Edit/{0}", "/dashboard/leads/{0}", "/dashboard/appointments/{0}" })
            {
                var response = await api.GetAsync(string.Format(path, Guid.NewGuid()), ct: ctx.Ct);
                if (response.Status >= 500) bad.Add($"{path} ({response.Status})");
            }
            Expect.True(bad.Count == 0, "Server error on: " + string.Join(", ", bad));
        });
        builder.Test("procedure-edit", "Procedure edit page for a real procedure", async ctx =>
        {
            var clinic = await ctx.ClinicAsync();
            (await clinic.Client.GetAsync($"/Procedures/Edit/{clinic.BaseProcedureId}", ct: ctx.Ct)).ExpectOk();
        });
        builder.Test("lead-detail", "Lead detail page", async ctx =>
        {
            var leadId = await ctx.Fixtures.LeadAsync();
            (await (await ctx.ApiAsync()).GetAsync($"/dashboard/leads/{leadId}", ct: ctx.Ct)).ExpectOk();
        });
        return builder.Build();
    }
}
