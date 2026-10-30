using System.Text.Json.Nodes;
using SculptFlowAutomation.Core;

namespace SculptFlowAutomation.Http;

/// <summary>One HTTP exchange, with the assertion helpers tests chain off it.</summary>
public sealed class ApiResponse
{
    private JsonNode? _json;
    private bool _parsed;

    public ApiResponse(string method, string path, int status, string body, string? location, string? contentType, long elapsedMs)
    {
        Method = method;
        Path = path;
        Status = status;
        Body = body;
        Location = location;
        ContentType = contentType;
        ElapsedMs = elapsedMs;
    }

    public string Method { get; }
    public string Path { get; }
    public int Status { get; }
    public string Body { get; }
    public string? Location { get; }
    public string? ContentType { get; }
    public long ElapsedMs { get; }

    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>The body parsed as JSON (null when it isn't JSON).</summary>
    public JsonNode? Json
    {
        get
        {
            if (!_parsed)
            {
                _parsed = true;
                try { _json = string.IsNullOrWhiteSpace(Body) ? null : JsonNode.Parse(Body); }
                catch (System.Text.Json.JsonException) { _json = null; }
            }
            return _json;
        }
    }

    /// <summary>Fails unless the status is one of <paramref name="expected"/>.</summary>
    public ApiResponse ExpectStatus(params int[] expected)
    {
        if (!expected.Contains(Status))
        {
            throw new TestFailure(
                $"{Method} {Path} returned {Status}, expected {string.Join(" or ", expected)}.{Snippet()}");
        }
        return this;
    }

    public ApiResponse ExpectOk() => ExpectStatus(200);

    /// <summary>Fails when the status is a 2xx (used for "this must be refused" checks).</summary>
    public ApiResponse ExpectRefused()
    {
        if (IsSuccess)
        {
            throw new TestFailure($"{Method} {Path} returned {Status} but should have been refused.{Snippet()}");
        }
        return this;
    }

    /// <summary>The JSON body, failing when there is none.</summary>
    public JsonNode RequireJson()
    {
        return Json ?? throw new TestFailure($"{Method} {Path} did not return JSON.{Snippet()}");
    }

    /// <summary>A property of the JSON object body, failing when it's missing or null.</summary>
    public JsonNode Prop(string name)
    {
        var node = RequireJson()[name];
        return node ?? throw new TestFailure($"{Method} {Path}: response has no \"{name}\".{Snippet()}");
    }

    public string Str(string name) => Prop(name).ToString();

    public Guid Id(string name = "id")
    {
        var raw = Prop(name).ToString();
        return Guid.TryParse(raw, out var id) ? id : throw new TestFailure($"{Method} {Path}: \"{name}\" is not an id ({raw}).");
    }

    /// <summary>The body as a JSON array, or the named array property of an object body (e.g. "items").</summary>
    public JsonArray Array(string? property = null)
    {
        var node = property is null ? RequireJson() : Prop(property);
        return node as JsonArray ?? throw new TestFailure($"{Method} {Path}: expected a JSON array{(property is null ? "" : $" in \"{property}\"")}.");
    }

    public ApiResponse ExpectBodyContains(string text)
    {
        if (!Body.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            throw new TestFailure($"{Method} {Path}: response doesn't contain \"{text}\".{Snippet()}");
        }
        return this;
    }

    public string Snippet(int max = 600)
    {
        if (string.IsNullOrWhiteSpace(Body)) return string.Empty;
        var text = Body.Length > max ? Body[..max] + "…" : Body;
        return $" Body: {text}";
    }
}
