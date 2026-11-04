using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Omni.Automation.Core;

namespace Omni.Automation.Suites;

/// <summary>Signed-out pages (sign-in, sign-up, forgot password) in Arabic and French.</summary>
[Suite(90)]
public static class UiSignedOutSuite
{
    public static TestSuite Build()
    {
        var builder = new SuiteBuilder("ui-signed-out", "UI: signed-out pages",
            "Sign-in, sign-up and forgot-password pages in Arabic (RTL) and French, on desktop and iPhone 13.");
        foreach (var language in new[] { "ar", "fr" })
        {
            builder.Test($"pages-{language}", $"Sign-in, sign-up and forgot password are clean in {language} (desktop and mobile)", async ctx =>
            {
                var problems = new List<string>();
                foreach (var mobile in new[] { false, true })
                {
                    await using var page = await ctx.Browser.OpenAnonymousAsync(language, mobile);
                    foreach (var path in UiHelpers.SignedOutPages)
                    {
                        var found = await UiHelpers.CheckPageAsync(ctx, page, path, language, general: false, client: null);
                        problems.AddRange(found.Select(p => $"{path} ({UiHelpers.Viewport(mobile)}) {p}"));
                        if (found.Count > 0) await ctx.Browser.ScreenshotAsync(page, $"signed-out-{language}-{UiHelpers.Viewport(mobile)}-{path}");
                    }
                }
                UiHelpers.NoProblems(problems, $"Signed-out pages in {language}");
            }, needs: Needs.Browser);
        }
        return builder.Build();
    }
}

/// <summary>Builds one sweep test per language and page group (desktop and iPhone 13 in each), so every test stays well
/// inside the runner's 120 s and the report names the page group that broke.</summary>
internal static class UiSweepTests
{
    public static void Add(SuiteBuilder builder, string kind, Func<TestContext, Task<TestClient>> client, bool general)
    {
        foreach (var language in new[] { "en", "ar", "fr" })
        foreach (var (group, groupName, pages) in UiHelpers.PageGroups)
        {
            builder.Test($"{language}-{group}", $"{kind} business pages in {language}: {groupName} (desktop and mobile)", async ctx =>
            {
                var found = await UiHelpers.SweepAsync(ctx, await client(ctx), language, pages, general);
                UiHelpers.NoProblems(found, $"{kind} business, {language}, {groupName}");
            }, needs: Needs.Session | Needs.Browser | Needs.IngestKey | Needs.AdminKey, risk: Risk.Billing);
        }
    }
}

/// <summary>Every main page of a general business in en/ar/fr on desktop and iPhone 13.</summary>
[Suite(91)]
public static class UiGeneralSuite
{
    public static TestSuite Build()
    {
        var builder = new SuiteBuilder("ui-general", "UI: general business pages",
            "The main client pages of a general business (plan ai-pro) in each language, desktop and iPhone 13: no errors, correct direction, " +
            "no sideways scrolling, no raw keys or English leftovers, no clinic words.");
        UiSweepTests.Add(builder, "General", UiHelpers.GeneralAsync, general: true);
        return builder.Build();
    }
}

/// <summary>Every main page of a clinic business in en/ar/fr on desktop and iPhone 13 (clinic words are expected here).</summary>
[Suite(92)]
public static class UiClinicSuite
{
    public static TestSuite Build()
    {
        var builder = new SuiteBuilder("ui-clinic", "UI: clinic business pages",
            "The main client pages of a clinic business (plan ai-pro) in each language, desktop and iPhone 13: no errors, correct direction, " +
            "no sideways scrolling, no raw keys or English leftovers.");
        UiSweepTests.Add(builder, "Clinic", UiHelpers.ClinicAsync, general: false);
        return builder.Build();
    }
}
