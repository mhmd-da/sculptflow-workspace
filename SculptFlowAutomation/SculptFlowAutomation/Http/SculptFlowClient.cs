using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SculptFlowAutomation.Http;

/// <summary>
/// HTTP client for one identity against the main app: anonymous, a signed-in clinic user (cookie jar), or a
/// key-holding caller (ingest / platform-admin header). Redirects are never followed, so tests can assert on them.
/// Every request is written to the current test's log as "METHOD path -> status (ms)"; header values (keys,
/// cookies) are never logged.
/// </summary>
public sealed partial class SculptFlowClient : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public SculptFlowClient(string baseUrl, Action<string> log, IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SculptFlowAutomation/1.0");
        if (headers is not null)
        {
            foreach (var (name, value) in headers) _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
        _log = log;
    }

    public Task<ApiResponse> GetAsync(string path, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, null, headers, ct);

    public Task<ApiResponse> DeleteAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, path, null, null, ct);

    public Task<ApiResponse> PostAsync(string path, object? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, JsonContent(body), headers, ct);

    public Task<ApiResponse> PutAsync(string path, object? body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, path, JsonContent(body), null, ct);

    public Task<ApiResponse> PatchAsync(string path, object? body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, path, JsonContent(body), null, ct);

    /// <summary>Posts raw text (e.g. a webhook body that must be sent byte for byte).</summary>
    public Task<ApiResponse> PostRawAsync(string path, string body, string contentType, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, new StringContent(body, Encoding.UTF8, contentType), headers, ct);

    public Task<ApiResponse> PostMultipartAsync(string path, MultipartFormDataContent content, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, content, null, ct);

    /// <summary>
    /// Submits a Razor Page form the way a browser does: GETs the page for its antiforgery token (and cookie), then
    /// POSTs the fields (plus the token) to the page, optionally to a named handler (?handler=X). <paramref name="postTo"/>
    /// posts to a different page instead, for forms that live in the shared layout (e.g. sign-out).
    /// </summary>
    public async Task<ApiResponse> SubmitFormAsync(string pagePath, IEnumerable<KeyValuePair<string, string>> fields,
        string? handler = null, CancellationToken ct = default, string? postTo = null)
    {
        var page = await GetAsync(pagePath, ct: ct);
        if (page.Status != 200)
        {
            throw new Core.TestFailure($"Couldn't open {pagePath} to submit its form (status {page.Status}).");
        }
        var token = AntiforgeryToken(page.Body)
            ?? throw new Core.TestFailure($"{pagePath} has no antiforgery token in its form.");

        var all = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", token) };
        var target = postTo ?? pagePath;
        if (handler is not null) target = $"{target}{(target.Contains('?') ? '&' : '?')}handler={Uri.EscapeDataString(handler)}";
        return await SendAsync(HttpMethod.Post, target, new FormUrlEncodedContent(all), null, ct);
    }

    public static string? AntiforgeryToken(string html)
    {
        var match = TokenRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private async Task<ApiResponse> SendAsync(HttpMethod method, string path, HttpContent? content,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 0.9));
        if (headers is not null)
        {
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        }

        var display = "/" + path.TrimStart('/');
        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _log($"{method} {display} -> connection failed: {ex.Message}");
            throw new Core.TestFailure($"{method} {display} couldn't reach the server: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log($"{method} {display} -> timed out after {sw.ElapsedMilliseconds} ms");
            throw new Core.TestFailure($"{method} {display} timed out after {sw.ElapsedMilliseconds} ms.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            var location = response.Headers.Location?.ToString();
            _log($"{method} {display} -> {(int)response.StatusCode}{(location is null ? "" : $" ({location})")} in {sw.ElapsedMilliseconds} ms");
            return new ApiResponse(method.Method, display, (int)response.StatusCode, body, location,
                response.Content.Headers.ContentType?.MediaType, sw.ElapsedMilliseconds);
        }
    }

    private static StringContent? JsonContent(object? body) =>
        body is null ? null : new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}
