using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;
/// <summary>Every Razor page renders (no server error) for a general and a clinic client; plan-gated pages send a trial
/// client to /plan/not-included/...; detail pages with unknown ids answer not-found; signed-out pages render.</summary>
[Suite(6)]
public static class PageRenderSuite
{
    /// <summary>Pages a plan can leave out, and the feature the not-included page names.</summary>
    private static readonly Dictionary<string, string> Gated = new()
    {
        ["/Services"] = "bookings", ["/Bots"] = "ai_assistant", ["/Campaigns"] = "campaigns", ["/Campaigns/Create"] = "campaigns",
        ["/KnowledgeBase"] = "ai_assistant", ["/KnowledgeBase/Settings"] = "ai_assistant", ["/settings/inbox"] = "ai_assistant",
    };

    private static readonly string[] Pages =
    {
        "/dashboard", "/dashboard/contacts", "/dashboard/bookings", "/inbox", "/reports", "/Members", "/Services", "/Bots", "/Campaigns",
        "/Campaigns/Create", "/KnowledgeBase", "/KnowledgeBase/Settings", "/WhatsApp/Templates", "/WhatsApp/Health",
        "/settings/business-info", "/settings/business-info?tab=availability", "/settings/billing", "/settings/integrations",
        "/settings/inbox", "/settings/auto-respond", "/settings/reminders", "/settings/calendar-integrations",
        "/plan/not-included/campaigns", "/plan/not-included/bookings", "/plan/not-included/ai_assistant",
        "/channel/not-connected/whatsapp", "/channel/not-connected/telegram",
    };

    public static TestSuite Build() => new SuiteBuilder("page-render", "Page rendering", "Every page renders for both business types, plan gates redirect, unknown ids are not-found.")
        .Test("general", "Every dashboard page renders for a general business on the trial plan", async ctx =>
            await RenderAllAsync(ctx, await ctx.ClientAsync(), fullPlan: false))
        .Test("clinic", "Every dashboard page renders for a clinic on the trial plan", async ctx =>
            await RenderAllAsync(ctx, await ctx.ClinicClientAsync(), fullPlan: false))
        .Test("plan-gates", "Pages the trial plan leaves out redirect to /plan/not-included/{feature}, for GET and POST", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var problems = new List<string>();
            foreach (var (path, feature) in Gated)
            {
                var get = await api.GetAsync(path, ct: ctx.Ct);
                if (get.Status != 302 || get.Location != $"/plan/not-included/{feature}") problems.Add($"GET {path} -> {get.Status} {get.Location}");
            }
            // A POST to a gated page is redirected too (the filter guards every handler). The token comes from an open page.
            var post = await api.SubmitFormAsync("/settings/business-info", new Dictionary<string, string> { ["Name"] = "x" }, postTo: "/KnowledgeBase/Settings", ct: ctx.Ct);
            if (post.Status != 302 || post.Location != "/plan/not-included/ai_assistant") problems.Add($"POST /KnowledgeBase/Settings -> {post.Status} {post.Location}");
            foreach (var detail in new[] { $"/Campaigns/{Guid.NewGuid()}", $"/Bots/Edit/{Guid.NewGuid()}", $"/KnowledgeBase/Websites/{Guid.NewGuid()}" })
            {
                var get = await api.GetAsync(detail, ct: ctx.Ct);
                if (get.Status != 302 || get.Location?.StartsWith("/plan/not-included/", StringComparison.Ordinal) != true) problems.Add($"GET {detail} -> {get.Status} {get.Location}");
            }
            Expect.True(problems.Count == 0, "Plan gates that didn't redirect as expected: " + string.Join("; ", problems));
        })
        .Test("not-in-plan-page", "The not-included page renders for every feature, and an unknown feature isn't a server error", async ctx =>
        {
            var api = await ctx.ApiAsync();
            foreach (var feature in new[] { "campaigns", "bookings", "ai_assistant", "bogus", "<b>omni-raw</b>" })
            {
                var page = await api.GetAsync("/plan/not-included/" + Uri.EscapeDataString(feature), ct: ctx.Ct);
                PlatformHelpers.ExpectNoServerError(page, $"Not-included page for '{feature}'");
                Expect.True(page.Status is 200 or 404, $"/plan/not-included/{feature} answered {page.Status}.");
                Expect.True(!page.Body.Contains("<b>omni-raw</b>", StringComparison.Ordinal), "The feature name is reflected raw.");
            }
            foreach (var channel in new[] { "whatsapp", "telegram", "bogus" })
            {
                var page = await api.GetAsync("/channel/not-connected/" + channel, ct: ctx.Ct);
                PlatformHelpers.ExpectNoServerError(page, $"Channel-not-connected page for '{channel}'");
            }
        })
        .Test("unknown-ids", "Detail pages with unknown ids answer not-found (or the plan page), never a server error", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var problems = new List<string>();
            foreach (var path in DetailPaths())
            {
                var page = await api.GetAsync(path, ct: ctx.Ct);
                var ok = page.Status == 404
                         || (page.Status == 302 && page.Location?.StartsWith("/plan/not-included/", StringComparison.Ordinal) == true)
                         || (page.Status == 200 && path.StartsWith("/dashboard/bookings/", StringComparison.Ordinal)); // trial: the "bookings not included" panel
                if (!ok || page.Status >= 500) problems.Add($"{path} -> {page.Status} {page.Location}");
            }
            Expect.True(problems.Count == 0, string.Join("; ", problems));
        })
        .Test("signed-out", "Sign-in, sign-up, forgot, reset, invite and error pages render signed out", async ctx =>
        {
            foreach (var path in new[] { "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/Account/ResetPassword?token=bad", "/Account/AcceptInvite?token=bad" })
            {
                var page = (await ctx.Anonymous.GetAsync(path, ct: ctx.Ct)).ExpectOk();
                PlatformHelpers.ExpectNoServerError(page, path);
            }
            var missing = await ctx.Anonymous.GetAsync("/no-such-page-" + ctx.Tag, ct: ctx.Ct);
            Expect.True(missing.Status is 404 or 302, $"An unknown page signed out answered {missing.Status}.");
            var root = await ctx.Anonymous.GetAsync("/", ct: ctx.Ct);
            Expect.True(root.Status is 200 or 302, $"/ answered {root.Status}.");
        }, needs: Needs.None)
        .Test("full-plan", "With every module included, every page and unknown-id detail page renders for both business types", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            await RenderAllAsync(ctx, team, fullPlan: true);
            await ExpectDetailNotFoundAsync(ctx, team.Http);
            // The same client as a clinic (business type changed by the admin API, then back).
            try
            {
                await BusinessTypeHelpers.ChangeAsync(ctx, team, "clinic");
                await RenderAllAsync(ctx, team, fullPlan: true);
                await ExpectDetailNotFoundAsync(ctx, team.Http);
            }
            finally
            {
                await BusinessTypeHelpers.ChangeAsync(ctx, team, "general");
            }
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing)
        .Test("general-wording", "A general business's sidebar doesn't use the clinic 'medical_services' icon", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var page = (await team.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains("href=\"/Services\"", StringComparison.OrdinalIgnoreCase), "The Services link isn't in the sidebar (the check needs it).");
            Expect.True(!page.Body.Contains("medical_services", StringComparison.Ordinal), "The sidebar shows the 'medical_services' (clinic) icon for Services on a general business.");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t1-accounts-13")
        .Build();

    private static IEnumerable<string> DetailPaths() => new[]
    {
        $"/dashboard/contacts/{Guid.NewGuid()}", $"/dashboard/bookings/{Guid.NewGuid()}", $"/Campaigns/{Guid.NewGuid()}",
        $"/KnowledgeBase/Websites/{Guid.NewGuid()}", $"/Bots/Edit/{Guid.NewGuid()}",
    };

    private static async Task ExpectDetailNotFoundAsync(TestContext ctx, OmniClient http)
    {
        var problems = new List<string>();
        foreach (var path in DetailPaths())
        {
            var page = await http.GetAsync(path, ct: ctx.Ct);
            if (page.Status != 404) problems.Add($"{path} -> {page.Status} {page.Location}");
        }
        Expect.True(problems.Count == 0, "Unknown ids on a full plan didn't answer 404: " + string.Join("; ", problems));
    }

    /// <summary>Opens every page: 200, or a redirect to the plan / channel-not-connected page; never a server error.
    /// On a full plan, plan redirects are failures.</summary>
    private static async Task RenderAllAsync(TestContext ctx, TestClient client, bool fullPlan)
    {
        var problems = new List<string>();
        foreach (var path in Pages)
        {
            var page = await client.Http.GetAsync(path, ct: ctx.Ct);
            var planRedirect = page.Status == 302 && page.Location?.StartsWith("/plan/not-included/", StringComparison.Ordinal) == true;
            var channelRedirect = page.Status == 302 && page.Location?.StartsWith("/channel/not-connected/", StringComparison.Ordinal) == true;
            if (page.Status >= 500 || page.Body.Contains("Something went wrong", StringComparison.OrdinalIgnoreCase)) problems.Add($"{path} -> {page.Status} (server error)");
            else if (page.Status == 200 || channelRedirect || (planRedirect && !fullPlan)) continue;
            else problems.Add($"{path} -> {page.Status} {page.Location}");
        }
        Expect.True(problems.Count == 0, $"{client.Label} ({client.BusinessType}): {string.Join("; ", problems)}");
    }
}

/// <summary>Business-type changes through the platform-admin API.</summary>
internal static class BusinessTypeHelpers
{
    public static async Task ChangeAsync(TestContext ctx, TestClient client, string code)
    {
        (await ctx.Admin.PutAsync($"/api/platform-admin/clients/{client.ClientId}/business-type", new { businessTypeCode = code }, ctx.Ct)).ExpectOk();
        ctx.Log($"{client.Name} is now business type {code}.");
    }
}
