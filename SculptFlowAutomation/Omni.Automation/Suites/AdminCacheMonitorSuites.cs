using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Cache admin: list, read, remove by key and prefix, token masking, clear-all (Risk.Shared).</summary>
[Suite(87)]
public static class AdminCacheSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-cache", "Admin: cache",
            "The cache API behind the portal's Cache page. Only the run's own keys are removed, except the Risk.Shared clear-all.")
        .Test("list-get", "A client's cached row is listed and readable by key", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var key = "core:client:" + AdminHelpers.N(client.ClientId);
            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            var keys = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/cache/keys?prefix={Uri.EscapeDataString(key)}", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(keys.Any(k => k?["key"]?.ToString() == key), $"{key} isn't listed after the client's dashboard loaded.");
            var entry = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/cache/key?key={Uri.EscapeDataString(key)}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText(key, entry.Prop("key"), "Cache entry key");
            Expect.True(entry.Prop("value").ToJsonString().Contains(client.Name.Replace("[", "").Split(' ')[^1]), "The cached client row doesn't contain the client's name.");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/cache/key?key=auto:nothing:{ctx.Tag}", ct: ctx.Ct)).Status, "Reading a key that isn't cached");
            Expect.Equal(400, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/cache/key", ct: ctx.Ct)).Status, "Reading without a key");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("remove", "Removing one key and a prefix drops them; a missing prefix is refused", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var key = "core:client:" + AdminHelpers.N(client.ClientId);
            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(204, (await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/cache/key?key={Uri.EscapeDataString(key)}", ctx.Ct)).Status, "Removing the client's key");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/cache/key?key={Uri.EscapeDataString(key)}", ct: ctx.Ct)).Status, "Reading a removed key");
            Expect.Equal(404, (await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/cache/key?key={Uri.EscapeDataString(key)}", ctx.Ct)).Status, "Removing it twice");

            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            var removed = (await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/cache/keys?prefix={Uri.EscapeDataString(key)}", ctx.Ct)).ExpectOk();
            Expect.True(removed.Prop("removed").GetValue<int>() >= 1, "Removing by the client's prefix removed nothing.");
            Expect.Equal(400, (await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/cache/keys?prefix=", ctx.Ct)).Status, "Removing with an empty prefix");
            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("no-tokens", "Cached channel connections don't show access tokens or webhook secrets", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-cache-tg");
            var channel = await AdminHelpers.FakeTelegramAsync(ctx, client);
            // The admin's own inbox-settings read loads the client's connection list into the cache.
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/inbox-settings", ct: ctx.Ct)).ExpectOk();
            var key = "channels:integrations:" + AdminHelpers.N(client.ClientId);
            var entry = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/cache/key?key={Uri.EscapeDataString(key)}", ct: ctx.Ct)).ExpectOk();
            var value = entry.Prop("value").ToJsonString();
            Expect.True(value.Contains(channel.IntegrationId.ToString(), StringComparison.OrdinalIgnoreCase), $"{key} doesn't hold the fake connection.");
            var leaks = new List<string>();
            if (value.Contains("AutoFakeToken", StringComparison.Ordinal)) leaks.Add("the bot access token");
            if (value.Contains(channel.WebhookSecret!, StringComparison.Ordinal)) leaks.Add("the webhook secret token");
            Expect.True(leaks.Count == 0, $"The cache API shows {string.Join(" and ", leaks)} of a channel connection in clear text ({key}).");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database, finding: "t6-admin-03")
        .Test("clear-all", "Clearing the whole cache works and the app keeps serving", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var cleared = (await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/cache", ctx.Ct)).ExpectOk();
            cleared.Prop("removed");
            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/settings", ct: ctx.Ct)).ExpectOk();
        }, needs: Needs.Session | Needs.AdminKey, risk: Risk.Shared)
        .Build();
}

/// <summary>Monitoring and tools: logs, message traces, API timings, channels list, knowledge benchmark (read-only), and the
/// automation cleanup endpoint itself.</summary>
[Suite(88)]
public static class AdminMonitorSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-monitor", "Admin: logs, traces, timings, channels, benchmark, automation",
            "Read-only monitoring APIs and the automation cleanup endpoint's safety rule.")
        .Test("logs", "Error log summary, groups, list and one entry answer; odd filters don't crash", async ctx =>
        {
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/logs/summary", ct: ctx.Ct)).ExpectOk().Prop("hours");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/logs/groups?take=5", ct: ctx.Ct)).ExpectOk();
            var list = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/logs?pageSize=1", ct: ctx.Ct)).ExpectOk();
            if (list.Array("items").FirstOrDefault()?["id"] is { } id)
            {
                (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/logs/{id}", ct: ctx.Ct)).ExpectOk();
            }
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/logs/999999999999", ct: ctx.Ct)).Status, "Unknown log entry");
            foreach (var q in new[] { "groups?hours=-1&level=Bogus&sort=zzz&search=%3Cscript%3E", "groups?hours=99999", "?fingerprint=zzz&page=-1", "?traceId=nope&pageSize=100000", "?search=" + new string('x', 2000) })
            {
                var r = await ctx.Admin.GetAsync($"{AdminHelpers.Api}/logs{(q.StartsWith('?') ? "" : "/")}{q}", ct: ctx.Ct);
                Expect.True(r.Status is 200 or 400, $"GET logs/{q[..Math.Min(40, q.Length)]} answered {r.Status}.{r.Snippet(200)}");
            }
        }, needs: Needs.AdminKey)
        .Test("traces", "Message traces: summary, list, and 404 for unknown traces and messages", async ctx =>
        {
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/message-traces/summary?hours=24", ct: ctx.Ct)).ExpectOk().Prop("traces");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/message-traces?hours=0&failedOnly=true&pageSize=5", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/message-traces/by-message/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Trace of an unknown message");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/message-traces/not-a-trace", ct: ctx.Ct)).Status, "Unknown trace id");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/message-traces/resolve?q=nothing-{ctx.Tag}", ct: ctx.Ct)).Status, "Resolving text that matches nothing");
            var big = await ctx.Admin.GetAsync($"{AdminHelpers.Api}/message-traces/resolve?q={new string('x', 3000)}", ct: ctx.Ct);
            Expect.True(big.Status is 400 or 404 or 414, $"Resolving a 3000-character query answered {big.Status}.");
        }, needs: Needs.AdminKey)
        .Test("api-timings", "API timings answer for every sort; an unknown sort is 400", async ctx =>
        {
            foreach (var sort in new[] { "slowest", "p95", "max", "calls", "errors", "slow", "time" })
            {
                (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/api-timings?sort={sort}&take=5", ct: ctx.Ct)).ExpectOk();
            }
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/api-timings?hours=-5&take=100000", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(400, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/api-timings?sort=bogus", ct: ctx.Ct)).Status, "Unknown sort");
        }, needs: Needs.AdminKey)
        .Test("channels", "Channels list and details show a connection without its token", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-channels");
            var channel = await AdminHelpers.FakeTelegramAsync(ctx, client);
            var list = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array();
            Expect.ContainsId(list, channel.IntegrationId, "Channels of the client");
            AdminHelpers.NoSecretProperties(list, "The channels list");
            var detail = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels/{channel.IntegrationId}", ct: ctx.Ct)).ExpectOk();
            AdminHelpers.NoSecretProperties(detail.Json, "The channel details");
            Expect.True(!detail.Body.Contains("AutoFakeToken") && !detail.Body.Contains(channel.WebhookSecret!), "The channel details show the token or webhook secret.");
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels/{channel.IntegrationId}/health-events", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels?channel=bogus&problemsOnly=true", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels/calendars?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array();
            (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels/tiktok?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array();
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/channels/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Unknown channel");
        }, needs: Needs.Session | Needs.AdminKey | Needs.Database)
        .Test("benchmark-read", "Knowledge benchmark read-only views answer for a client and 404 for unknown ones", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var path = $"{AdminHelpers.Api}/knowledge-benchmark/clients/{client.ClientId}";
            (await ctx.Admin.GetAsync(path, ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{path}/cases?view=all", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{path}/generations", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{path}/runs", ct: ctx.Ct)).ExpectOk();
            (await ctx.Admin.GetAsync($"{path}/source-documents", ct: ctx.Ct)).ExpectOk().Array();
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{path}/cases/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Unknown case");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{path}/runs/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Unknown run");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/knowledge-benchmark/clients/{Guid.NewGuid()}", ct: ctx.Ct)).Status, "Unknown client");
            var badRun = await ctx.Admin.PostAsync($"{path}/runs", new { scope = "bogus" }, ct: ctx.Ct);
            Expect.Equal(400, badRun.Status, "Starting a run with scope bogus");
            var noGeneration = await ctx.Admin.PostAsync($"{path}/runs", new { scope = "generation" }, ct: ctx.Ct);
            Expect.Equal(400, noGeneration.Status, "Starting a generation-scoped run without generationId");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("automation-list", "The automation endpoint lists this run's test clients; an unknown id is 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            Expect.ContainsId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/automation/clients", ct: ctx.Ct)).ExpectOk().Array(), client.ClientId, "Automation clients");
            Expect.Equal(404, (await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/automation/clients/{Guid.NewGuid()}", ctx.Ct)).Status, "Deleting an unknown client");
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("automation-refuses", "The automation endpoint refuses (409) a client that isn't an automation client", async ctx =>
        {
            // A real client must never be put at risk, so the "not automation" client is one of ours whose name lacks the
            // [Automation] prefix; it is renamed and deleted at the end.
            var http = ctx.NewHttp();
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            var email = $"auto-{ctx.Tag}-unprefixed-{suffix}@{TestContext.EmailDomain}";
            var password = AdminHelpers.NewPassword();
            var name = $"Automation unprefixed {ctx.Tag}";
            var register = await http.SubmitFormAsync("/Account/Register", new Dictionary<string, string>
            {
                ["FullName"] = "Automation Unprefixed", ["ClientName"] = name, ["Email"] = email, ["BusinessTypeCode"] = "general",
                ["Password"] = password, ["ConfirmPassword"] = password,
            }, ct: ctx.Ct);
            Expect.Equal(302, register.Status, "Registering the unprefixed client");
            var clientId = (await http.PostAsync("/api/services", new { name = "Consultation", code = $"auto-{ctx.Tag}-unpref", description = "Omni.Automation", bookingDuration = 30 }, ct: ctx.Ct))
                .ExpectStatus(201).Id("clientId");
            var deleted = false;
            try
            {
                var refused = await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/automation/clients/{clientId}", ctx.Ct);
                deleted = refused.Status == 204;
                Expect.Equal(409, refused.Status, "Deleting a client without the [Automation] name prefix");
                Expect.True((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{clientId}", ct: ctx.Ct)).Status == 200, "The refused client is gone.");
                Expect.LacksId((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/automation/clients", ct: ctx.Ct)).ExpectOk().Array(), clientId, "Automation clients");
            }
            finally
            {
                if (!deleted)
                {
                    var body = new Dictionary<string, object?>
                    {
                        ["name"] = $"{TestContext.ClientNamePrefix} unprefixed {ctx.Tag}", ["phone"] = null, ["email"] = email, ["website"] = null, ["countryCode"] = null,
                        ["timezone"] = "UTC", ["address"] = null, ["operatingHours"] = null, ["bookingInfo"] = null,
                    };
                    var renamed = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{clientId}", body, CancellationToken.None);
                    var gone = renamed.IsSuccess ? await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/automation/clients/{clientId}", CancellationToken.None) : renamed;
                    if (gone.Status != 204) ctx.Run.Note($"Left client \"{name}\" ({clientId}) behind: rename/delete answered {gone.Status}. Delete it with \"Delete leftover test clients\" after renaming it to start with [Automation].");
                }
            }
        }, needs: Needs.AdminKey)
        .Build();
}
