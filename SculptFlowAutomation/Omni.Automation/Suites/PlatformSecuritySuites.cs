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
/// <summary>Signed-out access to every client API route and page, wrong ingest / admin keys, security headers, and how
/// errors look (no server error pages, JSON on /api).</summary>
[Suite(4)]
public static class ApiSecuritySuite
{
    private static readonly string G = Guid.NewGuid().ToString();

    /// <summary>Every dashboard-facing route of the main app's Controllers/Client (and the conversations controller),
    /// with a sample id. All of them need a signed-in user.</summary>
    private static readonly (string Method, string Path)[] ClientRoutes =
    {
        ("GET", "/api/billing/summary"), ("GET", "/api/billing/usage"), ("GET", "/api/billing/transactions"),
        ("GET", "/api/bookings/available"), ("POST", "/api/bookings"), ("GET", $"/api/bookings/{G}"), ("PATCH", $"/api/bookings/{G}/status"),
        ("GET", "/api/bookings/calendar"), ("GET", "/api/bookings"),
        ("POST", $"/api/bots/{G}/test/messages"), ("GET", $"/api/bots/{G}/test/{G}"), ("DELETE", $"/api/bots/{G}/test/{G}"),
        ("GET", "/api/calendar-integrations"), ("POST", "/api/calendar-integrations/google/refresh-calendars"),
        ("POST", "/api/calendar-integrations/google/select-calendar"), ("POST", "/api/calendar-integrations/google/sync-enabled"),
        ("POST", "/api/calendar-integrations/google/disconnect"),
        ("GET", "/api/campaigns/audience-preview"), ("GET", "/api/campaigns/audience-preview/contacts"), ("GET", "/api/campaigns"),
        ("GET", $"/api/campaigns/{G}"), ("POST", "/api/campaigns"), ("POST", $"/api/campaigns/{G}/schedule"), ("POST", $"/api/campaigns/{G}/send"),
        ("POST", $"/api/campaigns/{G}/process-batch"), ("POST", $"/api/campaigns/{G}/stop"), ("POST", $"/api/campaigns/{G}/cancel"),
        ("GET", "/api/channel-integrations"), ("PUT", "/api/channel-integrations"), ("POST", "/api/channel-integrations/telegram/connect"),
        ("POST", "/api/channel-integrations/telegram/refresh"), ("POST", "/api/channel-integrations/whatsapp/disconnect"),
        ("POST", "/api/channel-integrations/whatsapp/connect"), ("POST", "/api/channel-integrations/facebook/connect"),
        ("GET", "/api/contacts"), ("GET", $"/api/contacts/{G}"), ("GET", $"/api/contacts/{G}/export"), ("DELETE", $"/api/contacts/{G}"),
        ("PATCH", $"/api/contacts/{G}"), ("POST", $"/api/contacts/{G}/stage"),
        ("POST", "/api/conversations"), ("GET", "/api/conversations"), ("GET", $"/api/conversations/{G}"), ("GET", $"/api/conversations/{G}/messages"),
        ("POST", $"/api/conversations/{G}/messages/send"), ("POST", $"/api/conversations/{G}/messages/send-template"),
        ("POST", $"/api/conversations/{G}/read"), ("POST", $"/api/conversations/{G}/take-over"), ("POST", $"/api/conversations/{G}/return-to-ai"),
        ("POST", $"/api/conversations/{G}/close"), ("POST", $"/api/conversations/{G}/reopen"),
        ("GET", "/api/dashboard/summary"), ("GET", "/api/dashboard/attention"), ("GET", "/api/dashboard/contacts"), ("GET", "/api/dashboard/bookings"),
        ("GET", "/api/dashboard/services"),
        ("GET", "/api/knowledge"), ("GET", "/api/knowledge/settings"), ("PUT", "/api/knowledge/settings"), ("GET", $"/api/knowledge/{G}"),
        ("POST", "/api/knowledge"), ("POST", "/api/knowledge/upload"), ("PUT", $"/api/knowledge/{G}"), ("POST", $"/api/knowledge/{G}/active"),
        ("DELETE", $"/api/knowledge/{G}"),
        ("GET", "/api/members"), ("GET", "/api/members/invites"), ("POST", "/api/members/invites"), ("POST", $"/api/members/invites/{G}/resend"),
        ("DELETE", $"/api/members/invites/{G}"), ("POST", $"/api/members/{G}/deactivate"), ("POST", $"/api/members/{G}/reactivate"),
        ("GET", $"/api/messages/{G}/media"),
        ("GET", "/api/notifications"), ("GET", "/api/notifications/unread-count"), ("POST", $"/api/notifications/{G}/read"), ("POST", "/api/notifications/read-all"),
        ("POST", "/api/orders"), ("PATCH", $"/api/orders/{G}"), ("GET", "/api/orders"),
        ("GET", "/api/services"), ("GET", $"/api/services/{G}"), ("POST", "/api/services"), ("PUT", $"/api/services/{G}"), ("POST", $"/api/services/{G}/active"),
        ("GET", "/api/knowledge/websites"), ("POST", "/api/knowledge/websites"), ("GET", $"/api/knowledge/websites/{G}"),
        ("GET", $"/api/knowledge/websites/{G}/pages"), ("POST", $"/api/knowledge/websites/{G}/rescrape"), ("POST", $"/api/knowledge/websites/{G}/active"),
        ("DELETE", $"/api/knowledge/websites/{G}"),
        ("GET", "/api/whatsapp/health"), ("GET", "/api/whatsapp/health/events"),
        ("GET", "/api/whatsapp/templates"), ("GET", $"/api/whatsapp/templates/{G}"), ("POST", "/api/whatsapp/templates"), ("POST", $"/api/whatsapp/templates/{G}/sync"),
        ("POST", "/hubs/inbox/negotiate?negotiateVersion=1"),
    };

    /// <summary>Every page that needs a signed-in user (and the two OAuth starts).</summary>
    internal static readonly string[] SignedInPages =
    {
        "/dashboard", "/dashboard/contacts", "/dashboard/bookings", "/inbox", "/reports", "/Members", "/Services", "/Bots", "/Campaigns",
        "/Campaigns/Create", "/KnowledgeBase", "/KnowledgeBase/Settings", "/WhatsApp/Templates", "/WhatsApp/Health",
        "/settings/business-info", "/settings/business-info?tab=availability", "/settings/billing", "/settings/integrations",
        "/settings/inbox", "/settings/auto-respond", "/settings/reminders", "/settings/calendar-integrations",
        "/plan/not-included/campaigns", "/channel/not-connected/whatsapp",
        $"/dashboard/contacts/{G}", $"/dashboard/bookings/{G}", $"/Campaigns/{G}", $"/KnowledgeBase/Websites/{G}", $"/Bots/Edit/{G}",
        "/calendar-oauth/google/connect", "/tiktok-oauth/connect",
    };

    public static TestSuite Build() => new SuiteBuilder("api-security", "API and page security", "Signed-out access, wrong keys, security headers and error shapes.")
        .Test("signed-out-api", "Every client API route refuses a signed-out caller (401)", async ctx =>
        {
            var problems = new List<string>();
            foreach (var (method, path) in ClientRoutes)
            {
                var response = await ctx.Anonymous.SendJsonAsync(new HttpMethod(method), path, method is "GET" or "DELETE" ? null : new { }, ct: ctx.Ct);
                if (response.Status != 401) problems.Add($"{method} {path} -> {response.Status}");
            }
            Expect.True(problems.Count == 0, $"{problems.Count} of {ClientRoutes.Length} routes didn't answer 401 signed out: {string.Join("; ", problems)}");
        }, needs: Needs.None)
        .Test("signed-out-pages", "Every dashboard page sends a signed-out visitor to sign-in", async ctx =>
        {
            var problems = new List<string>();
            foreach (var path in SignedInPages)
            {
                var response = await ctx.Anonymous.GetAsync(path, ct: ctx.Ct);
                if (response.Status != 302 || response.Location?.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) != true)
                    problems.Add($"{path} -> {response.Status} {response.Location}");
            }
            Expect.True(problems.Count == 0, "Pages that didn't redirect to sign-in: " + string.Join("; ", problems));
        }, needs: Needs.None)
        .Test("ingest-key", "Ingest-key endpoints refuse a missing or wrong key", async ctx =>
        {
            var problems = new List<string>();
            foreach (var key in new string?[] { null, "wrong-key", "" })
            {
                var http = key is null ? ctx.NewHttp() : ctx.NewHttp(new Dictionary<string, string> { ["X-Ingest-Key"] = key });
                var who = key is null ? "no key" : $"key '{key}'";
                foreach (var (label, response) in new[]
                         {
                             ("GET /api/ai/test/codes", await http.GetAsync("/api/ai/test/codes", ct: ctx.Ct)),
                             ("POST /api/contacts", await http.PostAsync("/api/contacts", new { clientId = G, fullName = "Nobody" }, ct: ctx.Ct)),
                         })
                {
                    if (response.Status != 401) problems.Add($"{label} with {who} -> {response.Status}");
                }
                // AI tool routes check the calling bot before the key and answer the tool protocol's "not allowed"
                // (200, success false, nothing looked up); either way nothing may come back.
                foreach (var (label, response) in new[]
                         {
                             ("GET /api/ai/client-info", await http.GetAsync($"/api/ai/client-info?clientId={G}", ct: ctx.Ct)),
                             ("GET /api/ai/services", await http.GetAsync($"/api/ai/services?clientId={G}", ct: ctx.Ct)),
                             ("GET /api/ai/contacts/{id}", await http.GetAsync($"/api/ai/contacts/{G}?clientId={G}", ct: ctx.Ct)),
                         })
                {
                    var refused = response.Status == 401 || (response.Status == 200 && response.Json?["success"]?.ToString() == "false");
                    if (!refused) problems.Add($"{label} with {who} -> {response.Status}{response.Snippet(120)}");
                }
            }
            Expect.True(problems.Count == 0, string.Join("; ", problems));
        }, needs: Needs.None)
        .Test("admin-key", "Platform-admin endpoints refuse a missing or wrong key (401)", async ctx =>
        {
            var problems = new List<string>();
            foreach (var key in new string?[] { null, "wrong-key" })
            {
                var http = key is null ? ctx.NewHttp() : ctx.NewHttp(new Dictionary<string, string> { ["X-Platform-Admin-Key"] = key });
                foreach (var path in new[] { "/api/platform-admin/automation/clients", "/api/platform-admin/clients", "/api/platform-admin/settings", "/api/platform-admin/members" })
                {
                    var response = await http.GetAsync(path, ct: ctx.Ct);
                    if (response.Status != 401) problems.Add($"{path} with {(key is null ? "no key" : "a wrong key")} -> {response.Status}");
                }
                var delete = await http.DeleteAsync($"/api/platform-admin/automation/clients/{G}", ctx.Ct);
                if (delete.Status != 401) problems.Add($"DELETE automation client with {(key is null ? "no key" : "a wrong key")} -> {delete.Status}");
            }
            Expect.True(problems.Count == 0, string.Join("; ", problems));
        }, needs: Needs.None)
        .Test("signed-in-admin-api", "A signed-in client owner can't use the platform-admin API", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var response = await api.GetAsync("/api/platform-admin/clients", ct: ctx.Ct);
            Expect.Equal(401, response.Status, "Platform-admin API with only a client session");
        })
        .Test("security-headers", "Pages and APIs can't be framed and send nosniff and a Referrer-Policy", async ctx =>
        {
            var client = await ctx.ClientAsync();
            using var raw = new RawSession(ctx);
            await raw.SignInAsync(client.Email, client.Password);
            var problems = new List<string>();
            foreach (var path in new[] { "/dashboard", "/Members", "/api/members" })
            {
                using var response = await raw.SendAsync(HttpMethod.Get, path);
                var frame = RawSession.Header(response, "X-Frame-Options");
                var csp = RawSession.Header(response, "Content-Security-Policy");
                if (frame is null && csp?.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase) != true) problems.Add($"{path}: no X-Frame-Options / frame-ancestors");
                if (!string.Equals(RawSession.Header(response, "X-Content-Type-Options"), "nosniff", StringComparison.OrdinalIgnoreCase)) problems.Add($"{path}: no X-Content-Type-Options: nosniff");
                if (RawSession.Header(response, "Referrer-Policy") is null) problems.Add($"{path}: no Referrer-Policy");
            }
            Expect.True(problems.Count == 0, "Missing security headers (clickjacking / MIME sniffing): " + string.Join("; ", problems));
        }, finding: "t1-accounts-03")
        .Test("no-swagger", "Swagger isn't exposed", async ctx =>
        {
            foreach (var path in new[] { "/swagger", "/swagger/index.html", "/swagger/v1/swagger.json" })
            {
                var response = await ctx.Anonymous.GetAsync(path, ct: ctx.Ct);
                Expect.True(response.Status is 404 or 302 or 401, $"{path} answered {response.Status}.");
            }
        }, needs: Needs.None)
        .Test("malformed-json", "Malformed JSON gets a 400, not a server error", async ctx =>
        {
            var api = await ctx.ApiAsync();
            var response = await api.PostRawAsync("/api/services", "{\"name\": ", "application/json", ct: ctx.Ct);
            Expect.Equal(400, response.Status, "POST /api/services with broken JSON");
        })
        .Test("not-found-page", "An unknown page and a malformed id answer 404, not a server error", async ctx =>
        {
            var api = await ctx.ApiAsync();
            foreach (var path in new[] { "/no-such-page-" + ctx.Tag, "/dashboard/contacts/not-a-guid", "/Campaigns/not-a-guid" })
            {
                var response = await api.GetAsync(path, ct: ctx.Ct);
                Expect.Equal(404, response.Status, $"GET {path}");
                PlatformHelpers.ExpectNoServerError(response, "Not-found page");
            }
        })
        .Test("api-500-json", "An unexpected API error answers JSON {\"error\"}, not the HTML error page", async ctx =>
        {
            // The one known way to cause an unmapped error: an order with a booking id that doesn't exist (t1-accounts-01).
            var main = await ctx.ClientAsync();
            var contact = await PlatformHelpers.IngestContactAsync(ctx, main, $"Auto error {ctx.Tag}");
            var response = await main.Http.PostAsync("/api/orders", new { contactId = contact, serviceId = main.BaseServiceId, bookingId = Guid.NewGuid(), quotedAmount = 5 }, ct: ctx.Ct);
            if (response.IsSuccess)
            {
                await ctx.Db.ExecuteForClientAsync(main.ClientId, "delete from crm.orders where client_id = @clientId and id = @id", new { id = response.Id() }, ctx.Ct);
                throw new TestFailure("An order with an unknown booking id was created, so no error could be checked.");
            }
            if (response.Status < 500)
            {
                // t1-accounts-01 is fixed on the target: the request is refused cleanly, so this no longer reaches an unmapped error.
                throw new TestSkipped($"An unknown booking id now answers {response.Status}; no other known request causes an unmapped server error.");
            }
            Expect.True(response.Json?["error"] is not null, $"{response.Method} {response.Path} answered {response.Status} with {response.ContentType ?? "no content type"} instead of JSON {{\"error\"}} (the dashboard can't show it).");
        }, needs: Needs.Session | Needs.IngestKey | Needs.Database, finding: "t1-accounts-02")
        .Build();
}

/// <summary>Tenant isolation: client A (the run's main client, then a member and a full-plan client) uses client B's ids
/// on every resource type and must neither read nor change anything; B's data is checked afterwards. B's records are
/// made through the API where it allows it and in the database otherwise (no plan or channel needed).</summary>
[Suite(5)]
public static class TenantIsolationSuite
{
    public static TestSuite Build() => new SuiteBuilder("tenant-isolation", "Tenant isolation", "One client can't read or change another client's records (QA's IDOR scripts).")
        .Test("contacts", "Another client's contact can't be read, exported, edited, staged or deleted", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, a, b,
                ("GET", $"/api/contacts/{b.Contact}", null),
                ("GET", $"/api/contacts/{b.Contact}/export", null),
                ("PATCH", $"/api/contacts/{b.Contact}", new { notes = "A was here" }),
                ("POST", $"/api/contacts/{b.Contact}/stage", new { stageKey = "contacted" }),
                ("DELETE", $"/api/contacts/{b.Contact}", new { confirmation = b.ContactName }));
            var list = (await a.GetAsync($"/api/contacts?serviceId={b.Client.BaseServiceId}", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(0, list.Array("items").Count, "Contacts listed when filtering by another client's service");
            var search = (await a.GetAsync($"/api/contacts?search={Uri.EscapeDataString(b.Marker)}", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(0, search.Array("items").Count, "Contacts found when searching another client's contact name");
            var own = (await b.Client.Http.GetAsync($"/api/contacts/{b.Contact}", ct: ctx.Ct)).ExpectOk();
            Expect.True(own.Json?["notes"]?.ToString() != "A was here", "Client A changed client B's contact.");
            Expect.EqualText("new", own.Json?["stageKey"], "B's contact stage after A's attempts");
        }, needs: IsolationHelpers.Needs)
        .Test("conversations", "Another client's conversation can't be read, written to or moved", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, a, b,
                ("GET", $"/api/conversations/{b.Conversation}", null),
                ("POST", $"/api/conversations/{b.Conversation}/messages", new { direction = "outbound", senderType = "member", channel = "website", messageType = "text", content = "A injected" }),
                ("POST", $"/api/conversations/{b.Conversation}/messages/send", new { content = "A send" }),
                ("POST", $"/api/conversations/{b.Conversation}/messages/send-template", new { templateId = b.Template, variables = Array.Empty<string>() }),
                ("POST", $"/api/conversations/{b.Conversation}/read", null),
                ("POST", $"/api/conversations/{b.Conversation}/take-over", null),
                ("POST", $"/api/conversations/{b.Conversation}/return-to-ai", null),
                ("POST", $"/api/conversations/{b.Conversation}/close", null),
                ("POST", $"/api/conversations/{b.Conversation}/reopen", null),
                ("POST", "/api/conversations", new { contactId = b.Contact, channel = "website" }),
                ("GET", $"/api/messages/{b.Message}/media", null));
            var own = (await b.Client.Http.GetAsync($"/api/conversations/{b.Conversation}", ct: ctx.Ct)).ExpectOk();
            var messages = own.Json?["messages"] as JsonArray ?? new JsonArray();
            Expect.True(!messages.Any(m => m?["content"]?.ToString()?.Contains("A injected") == true), "Client A added a message to client B's conversation.");
            Expect.EqualText("active", own.Json?["conversation"]?["status"], "B's conversation status after A's attempts");
        }, needs: IsolationHelpers.Needs)
        .Test("conversation-messages", "Another client's conversation messages answer 404, not an empty 200", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            var response = await a.GetAsync($"/api/conversations/{b.Conversation}/messages", ct: ctx.Ct);
            Expect.True(!response.Body.Contains(b.Marker, StringComparison.Ordinal), "Client A read client B's messages.");
            Expect.Equal(404, response.Status, "GET another client's conversation messages");
        }, needs: IsolationHelpers.Needs, finding: "t1-accounts-17")
        .Test("services-orders", "Another client's services and orders can't be read or changed, nor used in a new order", async ctx =>
        {
            var main = await ctx.ClientAsync();
            var (a, b) = (main.Http, await IsolationHelpers.ForeignAsync(ctx));
            var ownContact = await PlatformHelpers.IngestContactAsync(ctx, main, $"Auto A order {ctx.Tag}");
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, a, b,
                ("GET", $"/api/services/{b.Client.BaseServiceId}", null),
                ("PUT", $"/api/services/{b.Client.BaseServiceId}", new { name = "Hijacked", bookingDuration = 30 }),
                ("POST", $"/api/services/{b.Client.BaseServiceId}/active", new { isActive = false }),
                ("PATCH", $"/api/orders/{b.Order}", new { status = "confirmed", notes = "A" }),
                ("POST", "/api/orders", new { contactId = b.Contact, serviceId = b.Client.BaseServiceId }),
                ("POST", "/api/orders", new { contactId = ownContact, serviceId = b.Client.BaseServiceId }),
                ("POST", "/api/orders", new { contactId = b.Contact, serviceId = main.BaseServiceId }));
            var service = (await b.Client.Http.GetAsync($"/api/services/{b.Client.BaseServiceId}", ct: ctx.Ct)).ExpectOk();
            Expect.True(service.Json?["name"]?.ToString() != "Hijacked", "Client A renamed client B's service.");
        }, needs: IsolationHelpers.Needs)
        .Test("order-foreign-booking", "An order can't point at another client's booking", async ctx =>
        {
            var main = await ctx.ClientAsync();
            var b = await IsolationHelpers.ForeignAsync(ctx);
            var contact = await PlatformHelpers.IngestContactAsync(ctx, main, $"Auto A booking {ctx.Tag}");
            var response = await main.Http.PostAsync("/api/orders", new { contactId = contact, serviceId = main.BaseServiceId, bookingId = b.Booking, quotedAmount = 5 }, ct: ctx.Ct);
            if (response.IsSuccess)
            {
                await ctx.Db.ExecuteForClientAsync(main.ClientId, "delete from crm.orders where client_id = @clientId and id = @id", new { id = response.Id() }, ctx.Ct);
                throw new TestFailure($"POST /api/orders accepted another client's bookingId ({response.Status}): the order was linked to client B's booking.");
            }
            Expect.Equal(400, response.Status, "Order with another client's booking id");
        }, needs: IsolationHelpers.Needs, finding: "t1-accounts-01")
        .Test("order-unknown-booking", "An order with an unknown booking id gets 400, not a server error", async ctx =>
        {
            var main = await ctx.ClientAsync();
            var contact = await PlatformHelpers.IngestContactAsync(ctx, main, $"Auto A unknown {ctx.Tag}");
            var response = await main.Http.PostAsync("/api/orders", new { contactId = contact, serviceId = main.BaseServiceId, bookingId = Guid.NewGuid(), quotedAmount = 5 }, ct: ctx.Ct);
            if (response.IsSuccess) await ctx.Db.ExecuteForClientAsync(main.ClientId, "delete from crm.orders where client_id = @clientId and id = @id", new { id = response.Id() }, ctx.Ct);
            Expect.Equal(400, response.Status, "Order with a booking id that doesn't exist (a 500 here lets anyone probe which booking ids exist)");
        }, needs: Needs.Session | Needs.IngestKey | Needs.Database, finding: "t1-accounts-01")
        .Test("campaigns-templates", "Another client's campaigns and WhatsApp templates can't be read, sent or reused", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            var when = DateTimeOffset.UtcNow.AddDays(3);
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, a, b,
                ("GET", $"/api/campaigns/{b.Campaign}", null),
                ("POST", $"/api/campaigns/{b.Campaign}/schedule", new { scheduledAt = when }),
                ("POST", $"/api/campaigns/{b.Campaign}/send", null),
                ("POST", $"/api/campaigns/{b.Campaign}/process-batch", null),
                ("POST", $"/api/campaigns/{b.Campaign}/stop", null),
                ("POST", $"/api/campaigns/{b.Campaign}/cancel", null),
                ("POST", "/api/campaigns", new { name = "A steal", whatsAppTemplateId = b.Template, contactIds = new[] { b.Contact } }),
                ("GET", $"/api/whatsapp/templates/{b.Template}", null),
                ("POST", $"/api/whatsapp/templates/{b.Template}/sync", null));
            var status = await ctx.Db.ScalarAsync<string>("select status from marketing.campaigns where id = @id", new { id = b.Campaign }, ctx.Ct);
            Expect.Equal("draft", status, "B's campaign status after A's attempts");
        }, needs: IsolationHelpers.Needs)
        .Test("knowledge", "Another client's knowledge documents and websites can't be read, changed or deleted", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, a, b,
                ("GET", $"/api/knowledge/{b.Knowledge}", null),
                ("PUT", $"/api/knowledge/{b.Knowledge}", new { title = "x", category = "general", content = "x" }),
                ("POST", $"/api/knowledge/{b.Knowledge}/active", new { isActive = false }),
                ("DELETE", $"/api/knowledge/{b.Knowledge}", null),
                ("GET", $"/api/knowledge/websites/{b.Website}", null),
                ("GET", $"/api/knowledge/websites/{b.Website}/pages", null),
                ("POST", $"/api/knowledge/websites/{b.Website}/rescrape", null),
                ("POST", $"/api/knowledge/websites/{b.Website}/active", new { isActive = false }),
                ("DELETE", $"/api/knowledge/websites/{b.Website}", null));
            var rows = await ctx.Db.QueryAsync("""
                select (select is_active from knowledge.knowledge_documents where id = @doc) as doc_active,
                       (select is_active from knowledge.knowledge_website_sources where id = @web) as web_active
                """, new { doc = b.Knowledge, web = b.Website }, ctx.Ct);
            Expect.True(rows[0]["doc_active"] is true && rows[0]["web_active"] is true, "B's knowledge document or website was changed or deleted by A.");
        }, needs: IsolationHelpers.Needs)
        .Test("notifications", "Marking another client's notification read answers 404 and changes nothing", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            var response = await a.PostAsync($"/api/notifications/{b.Notification}/read", ct: ctx.Ct);
            var read = await ctx.Db.ScalarAsync<bool>("select is_read from activity.notifications where id = @id", new { id = b.Notification }, ctx.Ct);
            Expect.True(!read, "Client A marked client B's notification as read.");
            Expect.Equal(404, response.Status, "POST /api/notifications/{another client's id}/read");
        }, needs: IsolationHelpers.Needs, finding: "t1-accounts-17")
        .Test("members-invites", "Another client's members and invites can't be deactivated, resent or cancelled", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, a, b,
                ("POST", $"/api/members/{b.OwnerUserId}/deactivate", null),
                ("POST", $"/api/members/{b.OwnerUserId}/reactivate", null),
                ("POST", $"/api/members/invites/{b.Invite}/resend", null),
                ("DELETE", $"/api/members/invites/{b.Invite}", null));
            var revoked = await ctx.Db.ScalarAsync<DateTime?>("select revoked_at from identity.account_tokens where id = @id", new { id = b.Invite }, ctx.Ct);
            Expect.True(revoked is null, "Client A cancelled client B's invite.");
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, b.Client.Http), "Client B's owner session after A's attempts");
        }, needs: IsolationHelpers.Needs)
        .Test("bot-test", "Another client's bot test session can't be read or reset", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            var get = await a.GetAsync($"/api/bots/{b.Bot}/test/{b.BotSession}", ct: ctx.Ct);
            Expect.True(!get.IsSuccess, $"Client A read client B's bot test session ({get.Status}).");
            var reset = await a.DeleteAsync($"/api/bots/{b.Bot}/test/{b.BotSession}", ctx.Ct);
            var exists = await ctx.Db.ScalarAsync<long>("select count(*) from crm.bot_test_sessions where id = @id", new { id = b.BotSession }, ctx.Ct);
            Expect.Equal(1L, exists, "B's bot test session rows after A's reset");
            Expect.Equal(404, reset.Status, "DELETE /api/bots/{B bot}/test/{B session}");
        }, needs: IsolationHelpers.Needs, finding: "t1-accounts-17")
        .Test("pages", "Another client's detail pages answer not-found (or the plan page) and show none of its data", async ctx =>
        {
            var (a, b) = (await ctx.ApiAsync(), await IsolationHelpers.ForeignAsync(ctx));
            var problems = new List<string>();
            foreach (var path in new[]
                     {
                         $"/dashboard/contacts/{b.Contact}", $"/dashboard/bookings/{b.Booking}", $"/Campaigns/{b.Campaign}",
                         $"/KnowledgeBase/Websites/{b.Website}", $"/Bots/Edit/{b.Bot}", $"/inbox?conversationId={b.Conversation}",
                         $"/dashboard/contacts?serviceId={b.Client.BaseServiceId}",
                     })
            {
                var page = await a.GetAsync(path, ct: ctx.Ct);
                if (page.Body.Contains(b.Marker, StringComparison.Ordinal)) problems.Add($"{path} shows B's data");
                if (page.Status >= 500) problems.Add($"{path} -> {page.Status}");
                if (page.Status == 200 && (path.Contains("/dashboard/contacts/") || path.Contains("/Campaigns/"))) problems.Add($"{path} -> 200");
            }
            Expect.True(problems.Count == 0, string.Join("; ", problems));
        }, needs: IsolationHelpers.Needs)
        .Test("hub", "SignalR: a client receives none of another client's live events, even after joining its conversation", async ctx =>
        {
            var main = await ctx.ClientAsync();
            var b = await IsolationHelpers.ForeignAsync(ctx);
            using var sessionA = new RawSession(ctx);
            await sessionA.SignInAsync(main.Email, main.Password);
            using var sessionB = new RawSession(ctx);
            await sessionB.SignInAsync(b.Client.Email, b.Client.Password);
            await using var hubA = await HubProbe.ConnectAsync(ctx, sessionA);
            await using var hubB = await HubProbe.ConnectAsync(ctx, sessionB);
            await hubA.InvokeAsync("JoinConversation", b.Conversation);

            (await b.Client.Http.PostAsync($"/api/conversations/{b.Conversation}/close", ct: ctx.Ct)).ExpectStatus(200, 204);
            (await b.Client.Http.PostAsync($"/api/conversations/{b.Conversation}/reopen", ct: ctx.Ct)).ExpectStatus(200, 204);
            var conversation = b.Conversation.ToString();
            Expect.True(await hubB.WaitForAsync(e => e.Contains(conversation, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(15)),
                "Control failed: client B's own connection got no event for its conversation, so the check means nothing.");
            await Task.Delay(1500, ctx.Ct);
            var leaked = hubA.Events.Where(e => e.Contains(conversation, StringComparison.OrdinalIgnoreCase)).ToList();
            Expect.True(leaked.Count == 0, $"Client A received client B's events: {string.Join(" | ", leaked)}");
        }, needs: IsolationHelpers.Needs)
        .Test("bookings", "Another client's bookings can't be read, moved or booked into (full plan)", async ctx =>
        {
            var team = await PlatformHelpers.TeamAsync(ctx);
            var b = await IsolationHelpers.ForeignAsync(ctx);
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, team.Http, b,
                ("GET", $"/api/bookings/{b.Booking}", null),
                ("PATCH", $"/api/bookings/{b.Booking}/status", new { status = "canceled" }),
                ("POST", "/api/bookings", new { contactId = b.Contact, serviceId = b.Client.BaseServiceId, bookingType = "consultation", scheduledStart = DateTimeOffset.UtcNow.AddDays(4) }),
                ("GET", $"/api/bookings/available?serviceId={b.Client.BaseServiceId}", null));
            var list = (await team.Http.GetAsync("/api/bookings", ct: ctx.Ct)).ExpectOk();
            Expect.True(!list.Body.Contains(b.Booking.ToString(), StringComparison.OrdinalIgnoreCase), "Client B's booking is in client A's booking list.");
            var status = await ctx.Db.ScalarAsync<string>("select status from scheduling.bookings where id = @id", new { id = b.Booking }, ctx.Ct);
            Expect.Equal("booked", status, "B's booking status after A's attempts");
        }, needs: IsolationHelpers.Needs | Needs.AdminKey, risk: Risk.Billing, description: "Client A is the private full-plan client, so the bookings routes reach the id check instead of the plan check.")
        .Test("member", "A member of another client is refused the same way as its owner", async ctx =>
        {
            var member = await AccountsHelpers.SharedMemberAsync(ctx);
            var b = await IsolationHelpers.ForeignAsync(ctx);
            await IsolationHelpers.ExpectAllRefusedAsync(ctx, member.Http, b,
                ("GET", $"/api/contacts/{b.Contact}", null),
                ("PATCH", $"/api/contacts/{b.Contact}", new { notes = "member was here" }),
                ("GET", $"/api/conversations/{b.Conversation}", null),
                ("POST", $"/api/conversations/{b.Conversation}/close", null),
                ("GET", $"/api/bookings/{b.Booking}", null),
                ("PATCH", $"/api/orders/{b.Order}", new { status = "confirmed" }),
                ("GET", $"/api/campaigns/{b.Campaign}", null),
                ("GET", $"/api/knowledge/{b.Knowledge}", null),
                ("POST", $"/api/members/{b.OwnerUserId}/deactivate", null),
                ("DELETE", $"/api/members/invites/{b.Invite}", null));
        }, needs: IsolationHelpers.Needs | Needs.AdminKey, risk: Risk.Billing)
        .Build();
}

/// <summary>Client B's records for the isolation tests (the run's "other" client), made once per run.</summary>
internal static class IsolationHelpers
{
    public const Needs Needs = Core.Needs.Session | Core.Needs.IngestKey | Core.Needs.Database;

    public sealed record Foreign(
        TestClient Client, string Marker, string ContactName, string OwnerUserId, Guid Contact, Guid Conversation, Guid Message, Guid Order,
        Guid Booking, Guid Template, Guid Campaign, Guid Knowledge, Guid Website, Guid Notification, Guid Bot, Guid BotSession, Guid Invite);

    public static Task<Foreign> ForeignAsync(TestContext ctx) => PlatformHelpers.Once(ctx, "isolation-b", async () =>
    {
        var b = await ctx.OtherClientAsync();
        var marker = $"IsoB{ctx.Tag}";
        var contactName = $"Auto {marker} Contact";
        var contact = await PlatformHelpers.IngestContactAsync(ctx, b, contactName);
        var conversation = await PlatformHelpers.ConversationAsync(ctx, b.Http, contact);
        var message = (await b.Http.PostAsync($"/api/conversations/{conversation}/messages", new
        {
            direction = "inbound", senderType = "contact", channel = "website", messageType = "text", content = $"{marker} secret message",
        }, ct: ctx.Ct)).ExpectStatus(200, 201).Id();
        var order = (await b.Http.PostAsync("/api/orders", new { contactId = contact, serviceId = b.BaseServiceId, quotedAmount = 100, currencyCode = "USD", notes = $"{marker} order" }, ct: ctx.Ct))
            .ExpectStatus(200, 201).Id();
        var owner = await PlatformHelpers.OwnerUserIdAsync(ctx, b);

        // The rest has no API on a trial plan (or would call Meta / crawl a site), so it goes straight into B's rows.
        var ids = new
        {
            booking = Guid.NewGuid(), template = Guid.NewGuid(), campaign = Guid.NewGuid(), knowledge = Guid.NewGuid(), website = Guid.NewGuid(),
            notification = Guid.NewGuid(), bot = Guid.NewGuid(), session = Guid.NewGuid(),
        };
        await ctx.Db.ExecuteForClientAsync(b.ClientId, """
            insert into scheduling.bookings (id, client_id, contact_id, service_id, booking_type, status, scheduled_start, scheduled_end, notes, created_at, updated_at)
            values (@booking, @clientId, @contact, @service, 'consultation', 'booked', now() + interval '3 days', now() + interval '3 days 30 minutes', @note, now(), now());
            insert into channels.whatsapp_templates (id, client_id, name, category, language, status, body)
            values (@template, @clientId, @templateName, 'MARKETING', 'en_US', 'approved', @note);
            insert into marketing.campaigns (id, client_id, name, whatsapp_template_id, status, channel, audience_type)
            values (@campaign, @clientId, @note, @template, 'draft', 'whatsapp', 'custom');
            insert into knowledge.knowledge_documents (id, client_id, title, content, is_active)
            values (@knowledge, @clientId, @note, @note, true);
            insert into knowledge.knowledge_website_sources (id, client_id, start_url, normalized_start_url, host, is_active, status)
            values (@website, @clientId, 'https://example.invalid/', 'https://example.invalid/', 'example.invalid', true, 'completed');
            insert into activity.notifications (id, client_id, type, title, text_args, contact_id, is_read, created_at)
            values (@notification, @clientId, 'NEW_CONTACT', 'notifications.newContact.title', array[@note], @contact, false, now());
            insert into crm.bots (id, client_id, name) values (@bot, @clientId, @note);
            insert into crm.bot_test_sessions (id, client_id, bot_id) values (@session, @clientId, @bot);
            """, new
        {
            ids.booking, ids.template, ids.campaign, ids.knowledge, ids.website, ids.notification, ids.bot, ids.session,
            contact, service = b.BaseServiceId, note = $"{marker} record", templateName = $"auto_{ctx.Tag}_b",
        }, ctx.Ct);
        var (invite, _) = await PlatformHelpers.InsertInviteAsync(ctx, b, SignupHelpers.NewEmail(ctx, "isob-invite"), $"{marker} invitee");
        return new Foreign(b, marker, contactName, owner, contact, conversation, message, order, ids.booking, ids.template, ids.campaign,
            ids.knowledge, ids.website, ids.notification, ids.bot, ids.session, invite);
    });

    /// <summary>Calls each route as <paramref name="caller"/>; every one must be refused (not 2xx, not 5xx) without
    /// showing B's data. Reports every problem at once.</summary>
    public static async Task ExpectAllRefusedAsync(TestContext ctx, OmniClient caller, Foreign b, params (string Method, string Path, object? Body)[] calls)
    {
        var problems = new List<string>();
        foreach (var (method, path, body) in calls)
        {
            var response = await caller.SendJsonAsync(new HttpMethod(method), path, body, ct: ctx.Ct);
            var shown = path.Replace(b.Client.ClientId.ToString(), "{B}");
            if (response.IsSuccess) problems.Add($"{method} {shown} -> {response.Status} (accepted)");
            else if (response.Status >= 500) problems.Add($"{method} {shown} -> {response.Status} (server error)");
            if (response.Body.Contains(b.Marker, StringComparison.Ordinal)) problems.Add($"{method} {shown} returned B's data");
        }
        Expect.True(problems.Count == 0, $"{problems.Count} of {calls.Length} cross-client calls weren't cleanly refused: {string.Join("; ", problems)}");
    }
}

