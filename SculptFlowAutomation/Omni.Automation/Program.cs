using Omni.Automation;
using Omni.Automation.Core;

// Two ways to use it:
//   dotnet run                      -> local web page (http://localhost:5290) to pick tests and watch them run
//   dotnet run -- run [options]     -> command line, for scripts / CI (see Cli.Usage)
if (args.Length > 0 && Cli.Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase))
{
    return await Cli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5290");

var options = builder.Configuration.GetSection("Automation").Get<AutomationOptions>() ?? new AutomationOptions();
var catalog = new TestCatalog();
var store = new RunStore(Path.Combine(builder.Environment.ContentRootPath, options.RunsFolder));
var runner = new TestRunner(catalog, store.FilesFolder);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/catalog", () => catalog.Suites.Select(s => new
{
    s.Id,
    s.Name,
    s.Description,
    Tests = s.Tests.Select(t => new
    {
        t.Id,
        t.Name,
        t.Description,
        t.Finding,
        Risks = Flags(t.Risk),
        Needs = Flags(t.Needs),
    }),
}));

app.MapGet("/api/environments", () => new
{
    Default = options.DefaultEnvironment,
    Environments = options.Environments.Select(e => new
    {
        Name = e.Key,
        e.Value.BaseUrl,
        e.Value.HasIngestKey,
        e.Value.HasAdminKey,
        e.Value.HasMetaSecret,
        e.Value.HasDatabase,
        HasMetaToken = !string.IsNullOrWhiteSpace(e.Value.MetaWebhookVerifyToken),
        IsLocal = Uri.TryCreate(e.Value.BaseUrl, UriKind.Absolute, out var u) && u.IsLoopback,
    }),
});

app.MapPost("/api/runs", (RunRequest request) =>
{
    if (!options.Environments.TryGetValue(request.Environment, out var env) || string.IsNullOrWhiteSpace(env.BaseUrl))
    {
        return Results.BadRequest(new { error = $"Unknown environment \"{request.Environment}\"." });
    }
    if (request.TestIds.Count == 0) return Results.BadRequest(new { error = "Pick at least one test." });
    if (store.AnyRunning) return Results.Conflict(new { error = "A run is already in progress." });

    var run = runner.Prepare(request, env);
    store.Add(run);
    _ = Task.Run(async () =>
    {
        try { await runner.RunAsync(run, env); }
        catch (Exception ex) { run.Note($"Run crashed: {ex.Message}"); lock (run.Sync) { run.Phase = "crashed"; run.FinishedAt ??= DateTimeOffset.UtcNow; } }
        finally { store.Save(run.Snapshot()); }
    });
    return Results.Ok(new { run.Id });
});

app.MapGet("/api/runs", () => store.History());
app.MapGet("/api/runs/{id}", (string id) => store.Load(id) is { } run ? Results.Ok(run) : Results.NotFound());
app.MapPost("/api/runs/{id}/stop", (string id) =>
{
    if (store.Get(id) is not { } run) return Results.NotFound();
    run.Cancellation.Cancel();
    return Results.Ok();
});
app.MapGet("/runs/{id}/report", (string id) =>
    store.ReportPath(id) is { } path ? Results.File(path, "text/html") : Results.NotFound());
// Screenshots linked from a report ("files/x.png" relative to /runs/{id}/report).
app.MapGet("/runs/{id}/files/{file}", (string id, string file) =>
    store.FilePath(file) is { } path ? Results.File(path, "image/png") : Results.NotFound());

// Deletes automation clients left behind by crashed or stopped runs.
app.MapPost("/api/cleanup/{environment}", async (string environment) =>
{
    if (!options.Environments.TryGetValue(environment, out var env)) return Results.BadRequest(new { error = "Unknown environment." });
    if (!env.HasAdminKey) return Results.BadRequest(new { error = "No platform-admin key configured for this environment." });
    return Results.Ok(new { messages = await Cleanup.SweepAsync(env) });
});

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Omni automation tester: open {app.Urls.FirstOrDefault()} in your browser."));

app.Run();
return 0;

static IEnumerable<string> Flags<T>(T value) where T : struct, Enum =>
    Enum.GetValues<T>().Where(f => Convert.ToInt32(f) != 0 && value.HasFlag(f)).Select(f => f.ToString().ToLowerInvariant());
