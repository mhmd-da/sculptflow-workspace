using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Omni.Automation.Core;

/// <summary>Keeps runs in memory while the app is up and saves each finished run as JSON + an HTML report. Screenshots
/// go to runs/files/ (file names start with the run's tag), so the report's relative links work from disk and when
/// served at /runs/{id}/report.</summary>
public sealed class RunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ConcurrentDictionary<string, RunState> _runs = new();
    private readonly string _folder;

    public RunStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(FilesFolder);
    }

    public string Folder => _folder;

    /// <summary>Where screenshots and other attachments are written.</summary>
    public string FilesFolder => Path.Combine(_folder, "files");

    public void Add(RunState run) => _runs[run.Id] = run;

    public RunState? Get(string id) => _runs.GetValueOrDefault(id);

    public bool AnyRunning => _runs.Values.Any(r => r.FinishedAt is null);

    /// <summary>Saved runs, newest first (from disk, so they survive restarts).</summary>
    public IReadOnlyList<RunSnapshot> History(int take = 30)
    {
        var list = new List<RunSnapshot>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json").OrderByDescending(f => f).Take(take))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(file), JsonOptions);
                if (snapshot is not null) list.Add(snapshot with { Results = new(), RunLog = new() });
            }
            catch (JsonException) { /* a half-written, old-format or foreign file — ignore it */ }
        }
        return list;
    }

    public RunSnapshot? Load(string id)
    {
        if (_runs.TryGetValue(id, out var live)) return live.Snapshot();
        var file = Directory.EnumerateFiles(_folder, $"*-{id}.json").FirstOrDefault();
        return file is null ? null : JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(file), JsonOptions);
    }

    /// <summary>Writes runs/{time}-{env}-{id}.json and .html. Returns the HTML path.</summary>
    public string Save(RunSnapshot run)
    {
        var name = $"{run.StartedAt:yyyyMMdd-HHmmss}-{Safe(run.Environment)}-{run.Id}";
        File.WriteAllText(Path.Combine(_folder, name + ".json"), JsonSerializer.Serialize(run, JsonOptions));
        var html = Path.Combine(_folder, name + ".html");
        File.WriteAllText(html, RenderHtml(run));
        return html;
    }

    public string? ReportPath(string id) => Directory.EnumerateFiles(_folder, $"*-{id}.html").FirstOrDefault();

    /// <summary>An attachment file, refusing anything that isn't a plain file name in the files folder.</summary>
    public string? FilePath(string file) =>
        file.Contains('/') || file.Contains('\\') || file.Contains("..") || !File.Exists(Path.Combine(FilesFolder, file))
            ? null : Path.Combine(FilesFolder, file);

    private static string Safe(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A standalone report page (no external assets): summary, open QA findings, then every test.</summary>
    public static string RenderHtml(RunSnapshot run)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Omni test run {E(run.StartedAt.ToString("yyyy-MM-dd HH:mm"))}</title><style>");
        sb.Append("body{font:14px/1.5 system-ui,sans-serif;margin:0;padding:24px;background:#f7f7f8;color:#1d1d1f}");
        sb.Append("h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:24px 0 8px}.meta{color:#666;margin-bottom:16px}.pill{display:inline-block;padding:2px 10px;border-radius:99px;font-weight:600;margin-right:6px}");
        sb.Append(".passed{background:#e3f6e8;color:#17692f}.failed{background:#fde7e7;color:#a11b1b}.skipped{background:#eee;color:#555}.finding{background:#fff3d6;color:#7a5200;font-size:12px}");
        sb.Append("table{width:100%;border-collapse:collapse;background:#fff;border-radius:8px;overflow:hidden}td,th{padding:8px 10px;border-bottom:1px solid #eee;text-align:left;vertical-align:top}");
        sb.Append("th{background:#fafafa;font-weight:600}details pre{white-space:pre-wrap;font-size:12px;background:#f4f4f5;padding:8px;border-radius:6px;margin:6px 0 0}");
        sb.Append(".msg{color:#a11b1b}.skipmsg{color:#666}pre.log{white-space:pre-wrap;background:#fff;padding:12px;border-radius:8px}");
        sb.Append(".shots a{display:inline-block;margin:6px 6px 0 0}.shots img{max-width:220px;max-height:160px;border:1px solid #ddd;border-radius:4px}");
        sb.Append("</style></head><body>");
        sb.Append($"<h1>Omni test run</h1><div class=\"meta\">{E(run.Environment)} · {E(run.BaseUrl)} · started {E(run.StartedAt.ToString("u"))}");
        if (run.FinishedAt is { } f) sb.Append($" · took {(f - run.StartedAt).TotalSeconds:0.0} s");
        var allowed = new[] { run.AllowExternal ? "external" : null, run.AllowAi ? "AI" : null, run.AllowBilling ? "billing" : null, run.AllowShared ? "shared" : null, run.AllowMessaging ? "messaging" : null }.Where(a => a is not null);
        var allowedText = string.Join(", ", allowed);
        sb.Append($" · allowed: {E(allowedText.Length == 0 ? "nothing risky" : allowedText)}</div><p>");
        sb.Append($"<span class=\"pill passed\">{run.Passed} passed</span><span class=\"pill failed\">{run.Failed} failed</span><span class=\"pill skipped\">{run.Skipped} skipped</span></p>");

        var open = run.Results.Where(r => r.Finding is not null && r.Status == "failed").ToList();
        var fixedOnes = run.Results.Where(r => r.Finding is not null && r.Status == "passed").ToList();
        if (open.Count + fixedOnes.Count > 0)
        {
            sb.Append($"<h2>QA findings</h2><p>{open.Count} still reproduce, {fixedOnes.Count} now pass (fixed).</p>");
        }

        sb.Append("<table><thead><tr><th>Test</th><th>Result</th><th>Time</th></tr></thead><tbody>");
        foreach (var r in run.Results)
        {
            sb.Append($"<tr><td><strong>{E(r.Name)}</strong><br><small>{E(r.Id)}</small>");
            if (r.Finding is not null) sb.Append($" <span class=\"pill finding\">QA {E(r.Finding)}</span>");
            if (r.Message is not null) sb.Append($"<div class=\"{(r.Status == "failed" ? "msg" : "skipmsg")}\">{E(r.Message)}</div>");
            if (r.Attachments.Count > 0)
            {
                sb.Append("<div class=\"shots\">");
                foreach (var a in r.Attachments) sb.Append($"<a href=\"files/{E(a.File)}\" title=\"{E(a.Caption)}\"><img src=\"files/{E(a.File)}\" alt=\"{E(a.Caption)}\"></a>");
                sb.Append("</div>");
            }
            if (r.Log.Count > 0) sb.Append($"<details><summary>Log ({r.Log.Count})</summary><pre>{E(string.Join("\n", r.Log))}</pre></details>");
            sb.Append($"</td><td><span class=\"pill {E(r.Status)}\">{E(r.Status)}</span></td><td>{r.DurationMs} ms</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (run.TestClients.Count > 0) sb.Append($"<h2>Test clients</h2><pre class=\"log\">{E(string.Join("\n", run.TestClients))}</pre>");
        sb.Append($"<h2>Run log</h2><pre class=\"log\">{E(string.Join("\n", run.RunLog))}</pre></body></html>");
        return sb.ToString();
    }
}
