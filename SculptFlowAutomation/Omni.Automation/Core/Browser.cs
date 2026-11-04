using Microsoft.Playwright;

namespace Omni.Automation.Core;

/// <summary>A browser page signed in as a test client, with the console and page errors it produced.</summary>
public sealed class BrowserPage : IAsyncDisposable
{
    private readonly IBrowserContext _context;

    internal BrowserPage(IBrowserContext context, IPage page)
    {
        _context = context;
        Page = page;
        page.Console += (_, m) => { if (m.Type == "error") Errors.Add($"console: {m.Text}"); };
        page.PageError += (_, e) => Errors.Add($"page error: {e}");
    }

    public IPage Page { get; }

    /// <summary>Console errors and uncaught page errors since the page opened.</summary>
    public List<string> Errors { get; } = new();

    public ValueTask DisposeAsync() => _context.DisposeAsync();
}

/// <summary>
/// Playwright for the UI suites (Needs.Browser). Chromium is launched on first use. Install it once with
/// <c>pwsh bin/Debug/net10.0/playwright.ps1 install chromium</c> after a build (see README).
/// </summary>
public sealed class Browser : IAsyncDisposable
{
    private readonly TestContext _ctx;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _shot;

    public Browser(TestContext ctx) => _ctx = ctx;

    /// <summary>Opens a page for a test client, signed in, in <paramref name="language"/> (en/ar/fr), on desktop
    /// (1440×900) or a phone (iPhone 13). The language is saved as the user's own, as the language picker does.</summary>
    public async Task<BrowserPage> OpenAsync(TestClient client, string language = "en", bool mobile = false)
    {
        await SetLanguageAsync(client, language);
        var browser = await LaunchAsync();
        var options = mobile
            ? new BrowserNewContextOptions(_playwright!.Devices["iPhone 13"]) { Locale = language }
            : new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, Locale = language };
        var context = await browser.NewContextAsync(options);
        var page = new BrowserPage(context, await context.NewPageAsync());
        if (_ctx.AccountLimitBypass is { } bypass)
        {
            // Only the sign-in form post carries the admin key (past the per-IP account limits); every other request is as a visitor's.
            await page.Page.RouteAsync(u => new Uri(u).AbsolutePath.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase), async route =>
            {
                if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
                var headers = new Dictionary<string, string>(route.Request.Headers);
                foreach (var (name, value) in bypass) headers[name] = value;
                await route.ContinueAsync(new RouteContinueOptions { Headers = headers });
            });
        }

        await page.Page.GotoAsync(Url("/Account/Login"));
        await page.Page.FillAsync("input[name=Email]", client.Email);
        await page.Page.FillAsync("input[name=Password]", client.Password);
        await Task.WhenAll(page.Page.WaitForURLAsync(u => !u.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase), new() { Timeout = 30000 }),
            // The sign-in form, not the language menu's form that comes first on the page.
            page.Page.ClickAsync("form.au-auth__form button[type=submit]"));
        return page;
    }

    /// <summary>Opens a signed-out page (sign-in, sign-up) with the language cookie set.</summary>
    public async Task<BrowserPage> OpenAnonymousAsync(string language = "en", bool mobile = false)
    {
        var browser = await LaunchAsync();
        var options = mobile
            ? new BrowserNewContextOptions(_playwright!.Devices["iPhone 13"]) { Locale = language }
            : new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, Locale = language };
        var context = await browser.NewContextAsync(options);
        var uri = new Uri(_ctx.Env.BaseUrl);
        await context.AddCookiesAsync([new Cookie { Name = "omni.lang", Value = language, Domain = uri.Host, Path = "/" }]);
        return new BrowserPage(context, await context.NewPageAsync());
    }

    /// <summary>Saves a full-page screenshot into the run folder and attaches it to the current test.</summary>
    public async Task<string> ScreenshotAsync(BrowserPage page, string name, string? caption = null)
    {
        Directory.CreateDirectory(_ctx.RunFolder);
        var file = $"{_ctx.Tag}-{Interlocked.Increment(ref _shot):D3}-{Safe(name)}.png";
        await page.Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(_ctx.RunFolder, file), FullPage = true });
        _ctx.Attach(file, caption ?? name);
        return file;
    }

    public string Url(string path) => _ctx.Env.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>Saves the user's own UI language through the app's language form (POST /language).</summary>
    public async Task SetLanguageAsync(TestClient client, string language)
    {
        var response = await client.Http.SubmitFormAsync("/dashboard", new Dictionary<string, string>
        {
            ["language"] = language,
            ["returnUrl"] = "/dashboard",
        }, ct: _ctx.Ct, postTo: "/language");
        if (response.Status is not (302 or 200)) throw new TestFailure($"Setting the UI language to {language} failed (status {response.Status}).");
    }

    private async Task<IBrowser> LaunchAsync()
    {
        if (_browser is not null) return _browser;
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return _browser;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new TestSkipped("Playwright's Chromium isn't installed. Run: pwsh bin/Debug/net10.0/playwright.ps1 install chromium");
        }
    }

    private static string Safe(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
