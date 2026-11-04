using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Omni.Automation.Core;

namespace Omni.Automation.Suites;

/// <summary>
/// The page checks the UI suites share: open a page in a language and viewport, then look for console/page errors,
/// the wrong text direction, horizontal scrolling, raw strings-file keys, clinic words on a general business and
/// leftover English sentences in Arabic/French. Screenshots are taken only of pages with a problem.
/// </summary>
internal static partial class UiHelpers
{
    public static readonly string[] SignedOutPages = ["/Account/Login", "/Account/Register", "/Account/ForgotPassword"];

    /// <summary>Known, finding-tagged problems that the sweep notes instead of failing on (their own tests track them):
    /// (page, language, mobile, check) → finding id.</summary>
    private static readonly Dictionary<(string Page, string Lang, bool Mobile, string Check), string> Known = new()
    {
        [("/settings/business-info?tab=availability", "fr", true, "overflow")] = "t1-accounts-15",
    };

    /// <summary>Page groups the sweep tests are split into (each test must finish within the runner's 120 s).</summary>
    public static readonly (string Id, string Name, string[] Pages)[] PageGroups =
    [
        ("daily", "dashboard, inbox, contacts, bookings", ["/dashboard", "/inbox", "/dashboard/contacts", "/dashboard/contacts/{contact}", "/dashboard/bookings"]),
        ("tools", "services, campaigns, bots, knowledge, reports", ["/Services", "/Campaigns", "/Bots", "/KnowledgeBase", "/reports"]),
        ("account", "members, business info, billing", ["/Members", "/settings/business-info", "/settings/business-info?tab=availability", "/settings/billing", "/settings/integrations"]),
        ("settings", "inbox, auto-reply, reminders, calendars, templates", ["/settings/inbox", "/settings/auto-respond", "/settings/reminders", "/settings/calendar-integrations", "/WhatsApp/Templates"]),
    ];

    private sealed class Session
    {
        public required Task<BrowserPage> Desktop;
        public required Task<BrowserPage> Mobile;
        public required Task<Guid> Contact;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TestContext, Dictionary<string, Session>> Sessions = new();

    /// <summary>One signed-in desktop page, one phone page and one contact per client, kept for the run (the browser
    /// closes with it), so each sweep test spends its time on pages rather than sign-ins.</summary>
    private static Session SessionFor(TestContext ctx, TestClient client)
    {
        var byClient = Sessions.GetOrCreateValue(ctx);
        lock (byClient)
        {
            if (!byClient.TryGetValue(client.Label, out var session))
            {
                session = new Session
                {
                    Desktop = OpenSignedInAsync(ctx, client, "en"),
                    Mobile = OpenSignedInAsync(ctx, client, "en", mobile: true),
                    Contact = AdminHelpers.NewContactAsync(ctx, client, "ui"),
                };
                byClient[client.Label] = session;
            }
            return session;
        }
    }

    /// <summary>Switches the client's own language, then opens each page on desktop and on an iPhone 13 and returns the
    /// problems ("page (viewport) check: detail"). Pages with a problem are screenshotted.</summary>
    public static async Task<List<string>> SweepAsync(TestContext ctx, TestClient client, string language, IEnumerable<string> pages, bool general)
    {
        var session = SessionFor(ctx, client);
        var contactId = await session.Contact;
        await ctx.Browser.SetLanguageAsync(client, language);
        var problems = new List<string>();
        foreach (var mobile in new[] { false, true })
        {
            var page = await (mobile ? session.Mobile : session.Desktop);
            foreach (var raw in pages)
            {
                var path = raw.Replace("{contact}", contactId.ToString());
                var label = raw.Replace("{contact}", "{id}");
                var found = await CheckPageAsync(ctx, page, path, language, general, client);
                var reported = 0;
                foreach (var problem in found)
                {
                    var check = problem.Split(':')[0];
                    if (Known.TryGetValue((raw, language, mobile, check), out var finding))
                    {
                        ctx.Log($"Known issue {finding} on {label} ({Viewport(mobile)}): {problem}");
                        continue;
                    }
                    problems.Add($"{label} ({Viewport(mobile)}) {problem}");
                    reported++;
                }
                if (reported > 0) await ctx.Browser.ScreenshotAsync(page, $"{client.Label}-{language}-{Viewport(mobile)}-{label}", $"{label} {language} {Viewport(mobile)}");
            }
        }
        return problems;
    }

    public static string Viewport(bool mobile) => mobile ? "iPhone 13" : "desktop";

    /// <summary>A browser page signed in as the client in a language (core Browser.OpenAsync).</summary>
    public static Task<BrowserPage> OpenSignedInAsync(TestContext ctx, TestClient client, string language, bool mobile = false) =>
        ctx.Browser.OpenAsync(client, language, mobile);

    /// <summary>Opens one page and runs every check on it. Each problem starts with its check name ("overflow: ...").</summary>
    public static async Task<List<string>> CheckPageAsync(TestContext ctx, BrowserPage page, string path, string language, bool general, TestClient? client)
    {
        var problems = new List<string>();
        var errorsBefore = page.Errors.Count;
        IResponse? response;
        try
        {
            response = await page.Page.GotoAsync(ctx.Browser.Url(path), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            // A slow third-party script can hold the load event; the DOM is what matters.
            response = null;
            await page.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
        await page.Page.WaitForTimeoutAsync(500);
        if (response is { Status: >= 400 } r) problems.Add($"status: {r.Status}");

        var info = await page.Page.EvaluateAsync<JsonElement>(PageInfoScript);
        var dir = info.GetProperty("dir").GetString() ?? "";
        var lang = info.GetProperty("lang").GetString() ?? "";
        var overflow = info.GetProperty("overflow").GetInt32();
        var text = info.GetProperty("text").GetString() ?? "";

        var expectedDir = language == "ar" ? "rtl" : "ltr";
        if (!string.Equals(dir, expectedDir, StringComparison.OrdinalIgnoreCase)) problems.Add($"dir: html dir is \"{dir}\", expected {expectedDir}");
        if (!lang.StartsWith(language, StringComparison.OrdinalIgnoreCase)) problems.Add($"lang: html lang is \"{lang}\", expected {language}");
        if (overflow > 1) problems.Add($"overflow: the page scrolls sideways by {overflow}px");

        var keys = RawKeyRegex().Matches(text).Select(m => m.Value).Distinct().Take(5).ToList();
        if (keys.Count > 0) problems.Add($"raw keys: {string.Join(", ", keys)}");

        if (general)
        {
            var words = ClinicWordRegex().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            if (words.Count > 0) problems.Add($"clinic words: {string.Join(", ", words)}");
        }

        if (language != "en")
        {
            var english = info.GetProperty("english").EnumerateArray().Select(e => e.GetString() ?? "")
                .Where(s => client is null || !s.Contains(ctx.Tag, StringComparison.Ordinal))
                .Take(4).ToList();
            if (english.Count > 0) problems.Add($"english: {string.Join(" | ", english.Select(s => $"\"{Short(s)}\""))}");
        }

        var errors = page.Errors.Skip(errorsBefore).Distinct().Take(3).ToList();
        if (errors.Count > 0) problems.Add($"errors: {string.Join(" | ", errors.Select(Short))}");
        return problems;
    }

    public static string Short(string s) => s.Length > 140 ? s[..140] + "…" : s;

    /// <summary>dir, lang, horizontal overflow, the visible text (icons and hidden parts left out) and English-looking
    /// sentences that aren't user content (dir=auto, bdi, inputs, code are skipped).</summary>
    private const string PageInfoScript = """
        () => {
          const d = document.documentElement;
          const skip = 'script,style,noscript,template,.au-icon,[aria-hidden="true"],[dir="auto"],bdi,code,pre,.au-mono,input,textarea,select,option,[hidden]';
          const parts = [];
          const english = [];
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          const lang = (d.lang || '').slice(0, 2);
          const enStop = /\b(the|your|you|and|with|this|that|to|for|is|are|not|has|have|will|can|of|was|be|it|from|yet)\b/gi;
          const frStop = /\b(le|la|les|des|du|de|et|vous|votre|vos|pour|avec|est|une|un|au|aux|pas|sur|dans|ce|cette|ou|à|ne|plus|il|elle)\b/i;
          while (walker.nextNode()) {
            const node = walker.currentNode;
            const el = node.parentElement;
            if (!el || el.closest(skip)) continue;
            const style = getComputedStyle(el);
            if (style.display === 'none' || style.visibility === 'hidden') continue;
            const t = node.textContent.replace(/\s+/g, ' ').trim();
            if (!t) continue;
            parts.push(t);
            if (lang === 'en' || /[@\/]|Automation|Omni|http/.test(t)) continue;
            const words = t.match(/[A-Za-z]{2,}/g) || [];
            if (words.length < 3) continue;
            if (/[؀-ۿ]/.test(t)) continue;
            const stops = (t.match(enStop) || []).length;
            if (stops < 2) continue;
            if (lang === 'fr' && frStop.test(t)) continue;
            english.push(t);
          }
          return { dir: d.dir || getComputedStyle(d).direction, lang: d.lang, overflow: d.scrollWidth - d.clientWidth, text: parts.join('\n'), english: [...new Set(english)].slice(0, 10) };
        }
        """;

    /// <summary>A strings-file key shown as text: one of the app's key namespaces followed by .something.</summary>
    [GeneratedRegex(@"\b(?:terms|signup|contacts|stages|clients|nav|members|businessTypes|common|layout|header|auth|messages|noClient|timeView|statusHelp|login|register|dashboard|reports|inbox|billing|integrations|calendars|days|businessInfo|contactDetail|values|bookings|bookingDetail|plan|services|campaigns|knowledge|knowledgeSettings|website|whatsAppHealth|templates|status|error|aurora|notifications|time|availability|channels|languages|orders|identity|oauth|forgotPassword|resetPassword|emails|acceptInvite|optOut|reminders|autoReply|bots|inboxSettings)\.[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)*\b")]
    private static partial Regex RawKeyRegex();

    /// <summary>Clinic vocabulary a general business must never see (en/fr/ar), and the old brand. French "rendez-vous" is left
    /// out: it is the everyday word for any booked slot and the shared French strings use it for every business.</summary>
    [GeneratedRegex(@"\b(?:clinics?|patients?|procedures?|appointments?|doctors?|sculptflow|cliniques?|médecins?|procédures?)\b|عيادة|عيادات|مريض|مرضى|طبيب|أطباء", RegexOptions.IgnoreCase)]
    private static partial Regex ClinicWordRegex();

    /// <summary>Fails with the collected problems, one per line, when there are any.</summary>
    public static void NoProblems(List<string> problems, string what)
    {
        if (problems.Count == 0) return;
        throw new TestFailure($"{what}: {problems.Count} problem(s):\n- " + string.Join("\n- ", problems.Take(25)) + (problems.Count > 25 ? $"\n… and {problems.Count - 25} more" : ""));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TestContext, Dictionary<string, Task<TestClient>>> Planned = new();

    /// <summary>The run's general client for the UI suites (its own, so other suites' data can't change what it shows), on
    /// plan ai-pro so every page renders instead of "not in your plan" (callers are Risk.Billing + AdminKey).</summary>
    public static Task<TestClient> GeneralAsync(TestContext ctx) => PlannedClientAsync(ctx, "ui-general", "general");

    /// <summary>The run's clinic client for the UI suites, on plan ai-pro.</summary>
    public static Task<TestClient> ClinicAsync(TestContext ctx) => PlannedClientAsync(ctx, "ui-clinic", "clinic");

    private static Task<TestClient> PlannedClientAsync(TestContext ctx, string label, string businessType)
    {
        var byLabel = Planned.GetOrCreateValue(ctx);
        lock (byLabel)
        {
            if (!byLabel.TryGetValue(label, out var task))
            {
                task = Create();
                byLabel[label] = task;
            }
            return task;
        }

        async Task<TestClient> Create()
        {
            var client = await ctx.NamedClientAsync(label, businessType);
            await ctx.SetPlanAsync(client, "ai-pro");
            return client;
        }
    }
}
