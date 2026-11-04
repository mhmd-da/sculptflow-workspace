using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Omni.Automation.Core;

namespace Omni.Automation.Suites;

/// <summary>Targeted UI and wording checks from the QA findings: user content direction, neutral branding, the error page's
/// reference, translated billing reasons, and mobile layout of long text.</summary>
[Suite(93)]
public static class UiDetailsSuite
{
    public static TestSuite Build() => new SuiteBuilder("ui-details", "UI: details from QA findings",
            "Single checks behind specific QA findings (bidi of the client name, neutral icons and logo, error reference, translations, mobile layout).")
        .Test("client-name-dir", "In Arabic the client name is isolated with dir=auto (sidebar and page intros)", async ctx =>
        {
            var client = await UiHelpers.GeneralAsync(ctx);
            var problems = new List<string>();
            await using var page = await UiHelpers.OpenSignedInAsync(ctx, client, "ar");
            foreach (var path in new[] { "/dashboard", "/Members" })
            {
                await page.Page.GotoAsync(ctx.Browser.Url(path), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
                var missing = await page.Page.EvaluateAsync<string[]>("""
                    name => {
                      const out = [];
                      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                      while (walker.nextNode()) {
                        const node = walker.currentNode;
                        if (!node.textContent.includes(name)) continue;
                        const el = node.parentElement;
                        if (!el || el.closest('script,style,[hidden],input,textarea,title')) continue;
                        if (el.closest('bdi,[dir="auto"],[dir="ltr"]')) continue;
                        out.push((el.className || el.tagName.toLowerCase()).toString().split(' ')[0]);
                      }
                      return [...new Set(out)];
                    }
                    """, client.Name);
                problems.AddRange(missing.Select(m => $"{path}: .{m}"));
            }
            if (problems.Count > 0) await ctx.Browser.ScreenshotAsync(page, "client-name-ar");
            Expect.True(problems.Count == 0,
                $"The client name \"{client.Name}\" is shown without dir=auto/<bdi>, so Arabic pages reorder its brackets: {string.Join("; ", problems)}.");
        }, needs: Needs.Session | Needs.Browser | Needs.AdminKey, risk: Risk.Billing, finding: "t4-ai-14,t1-accounts-14")
        .Test("neutral-icon", "A general business doesn't get the medical icon for Services", async ctx =>
        {
            var client = await UiHelpers.GeneralAsync(ctx);
            var places = new List<string>();
            foreach (var path in new[] { "/dashboard", "/Services" })
            {
                var html = (await client.Http.GetAsync(path, ct: ctx.Ct)).ExpectOk().Body;
                if (html.Contains("medical_services", StringComparison.Ordinal)) places.Add(path);
            }
            Expect.True(places.Count == 0,
                $"A general business sees the 'medical_services' icon (sidebar Services and the Services empty state) on {string.Join(" and ", places)}.");
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Billing, finding: "t1-accounts-13,t5-ops-21")
        .Test("brand-mark", "The brand mark is Omni's, not the old SculptFlow 'S'", async ctx =>
        {
            var svg = (await ctx.Anonymous.GetAsync("/logo.svg", ct: ctx.Ct)).ExpectOk().Body;
            Expect.True(!svg.Contains("Signature-style S", StringComparison.OrdinalIgnoreCase) && !svg.Contains("M43 19.5C40.5 15.2", StringComparison.Ordinal),
                "/logo.svg (the mark on every page, sign-in included) is still the old SculptFlow 'S' stroke.");
        }, needs: Needs.None, finding: "t1-accounts-13")
        .Test("error-reference-get", "The error page shows a reference to quote", async ctx =>
        {
            var page = await ctx.Anonymous.GetAsync("/Error", ct: ctx.Ct);
            Expect.True(page.Body.Contains("au-mono", StringComparison.Ordinal), $"GET /Error answered {page.Status} without a reference code.");
        }, needs: Needs.None)
        .Test("error-reference-post", "The error page shows a reference after a failed form post too", async ctx =>
        {
            // UseExceptionHandler re-executes /Error with the failed request's method; a POST must show the reference as well.
            var page = await ctx.Anonymous.PostRawAsync("/Error", "x=1", "application/x-www-form-urlencoded", ct: ctx.Ct);
            Expect.True(page.Body.Contains("au-mono", StringComparison.Ordinal),
                $"The error page rendered for a POST (status {page.Status}) says to quote a reference but shows none: Error.cshtml.cs sets RequestId only in OnGet.");
        }, needs: Needs.None, finding: "t4-ai-17")
        .Test("billing-reasons", "Billing transactions show translated reasons in Arabic and French", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("ui-billing");
            await ctx.Browser.SetLanguageAsync(client, "en");
            var english = (await client.Http.GetAsync("/settings/billing", ct: ctx.Ct)).ExpectOk().Body;
            string[] reasons = ["Plan at signup", "Trial ended", "Subscription renewal", "Period ended unpaid", "Extra user"];
            var shown = reasons.Where(r => english.Contains(r, StringComparison.Ordinal)).ToList();
            if (shown.Count == 0) throw new TestSkipped("A new client's billing page shows no system-written transaction reason to check.");
            var problems = new List<string>();
            foreach (var language in new[] { "ar", "fr" })
            {
                await ctx.Browser.SetLanguageAsync(client, language);
                var body = (await client.Http.GetAsync("/settings/billing", ct: ctx.Ct)).ExpectOk().Body;
                problems.AddRange(shown.Where(r => body.Contains(r, StringComparison.Ordinal)).Select(r => $"{language}: \"{r}\""));
            }
            Expect.True(problems.Count == 0, $"Billing transactions keep English reasons on translated pages: {string.Join(", ", problems)}.");
        }, finding: "t2-billing-16")
        .Test("fr-availability-mobile", "French availability settings don't scroll sideways on a phone", async ctx =>
        {
            var client = await UiHelpers.GeneralAsync(ctx);
            await using var page = await UiHelpers.OpenSignedInAsync(ctx, client, "fr", mobile: true);
            await page.Page.GotoAsync(ctx.Browser.Url("/settings/business-info?tab=availability"), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
            var overflow = await page.Page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
            if (overflow > 1) await ctx.Browser.ScreenshotAsync(page, "fr-availability-mobile");
            Expect.True(overflow <= 1, $"In French on an iPhone 13 the availability tab is {overflow}px wider than the screen (a long button doesn't wrap).");
        }, needs: Needs.Session | Needs.Browser | Needs.AdminKey, risk: Risk.Billing, finding: "t1-accounts-15")
        .Test("long-name-mobile", "A long service name without spaces wraps or ends in an ellipsis on a phone", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("ui-longname");
            await ctx.SetPlanAsync(client, "inbox-bookings"); // the Services page needs bookings
            var name = "Auto" + new string('y', 190);
            (await client.Http.PostAsync("/api/services", new { name, code = $"auto-{ctx.Tag}-long", description = "Omni.Automation", bookingDuration = 30 }, ct: ctx.Ct)).ExpectStatus(201);
            await using var page = await UiHelpers.OpenSignedInAsync(ctx, client, "en", mobile: true);
            await page.Page.GotoAsync(ctx.Browser.Url("/Services"), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
            var result = await page.Page.EvaluateAsync<string?>("""
                name => {
                  const els = [...document.querySelectorAll('main *')].filter(e => e.childElementCount === 0 && e.textContent.trim() === name);
                  if (!els.length) return 'not shown';
                  for (const el of els) {
                    const r = el.getBoundingClientRect();
                    const s = getComputedStyle(el);
                    if (r.right > window.innerWidth + 1 || r.left < -1) return `ends at ${Math.round(r.right)}px on a ${window.innerWidth}px screen`;
                    if (el.scrollWidth > el.clientWidth + 1 && s.textOverflow !== 'ellipsis') return `is clipped (${el.scrollWidth}px of text in ${el.clientWidth}px, no ellipsis)`;
                  }
                  return null;
                }
                """, name);
            if (result is not null) await ctx.Browser.ScreenshotAsync(page, "long-service-name-mobile");
            Expect.True(result is null, $"On an iPhone 13 the Services list's long name {result}.");
        }, needs: Needs.Session | Needs.Browser | Needs.AdminKey, risk: Risk.Billing, finding: "t5-ops-24")
        .Build();
}
