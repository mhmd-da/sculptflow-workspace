using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Shared helpers for the platform-admin suites: paths, throwaway admins and clients, small JSON lookups.</summary>
internal static class AdminHelpers
{
    public const string Api = "/api/platform-admin";

    /// <summary>An admin user the run made: id, email and its current password.</summary>
    public sealed record AutoAdmin(string Id, string Email, string Password);

    /// <summary>A password that passes the Identity rules (length, digit, upper case) and the admin minimum of 10.</summary>
    public static string NewPassword() => "Auto-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "a1";

    /// <summary>
    /// An active automation admin in a fixed slot (auto-admin-{slot}@omni-automation.test), created on first use and
    /// re-armed on later runs (new password, unlocked, active), so repeated runs don't pile up admin users. The caller
    /// deactivates it in <c>finally</c> with <see cref="DeactivateAdminAsync"/>.
    /// </summary>
    public static async Task<AutoAdmin> SlotAdminAsync(TestContext ctx, string slot)
    {
        var email = $"auto-admin-{slot}@{TestContext.EmailDomain}";
        var password = NewPassword();
        var created = await ctx.Admin.PostAsync($"{Api}/admins", new { email, fullName = $"{TestContext.ClientNamePrefix} Admin {slot}", password }, ct: ctx.Ct);
        if (created.Status == 201) return new AutoAdmin(created.Str("id"), email, password);

        created.ExpectStatus(400);
        var row = await FindAdminAsync(ctx, email)
            ?? throw new TestFailure($"Couldn't create admin {email} and it isn't in the admins list either.{created.Snippet(200)}");
        var id = row["id"]!.ToString();
        (await ctx.Admin.PutAsync($"{Api}/admins/{id}/password", new { password }, ctx.Ct)).ExpectOk();
        (await ctx.Admin.PutAsync($"{Api}/admins/{id}/active", new { active = true, actingAdminId = (string?)null }, ctx.Ct)).ExpectOk();
        return new AutoAdmin(id, email, password);
    }

    /// <summary>Deactivates an automation admin, never throwing (used in finally blocks).</summary>
    public static async Task DeactivateAdminAsync(TestContext ctx, string id)
    {
        try
        {
            var response = await ctx.Admin.PutAsync($"{Api}/admins/{id}/active", new { active = false, actingAdminId = (string?)null }, CancellationToken.None);
            if (!response.IsSuccess) ctx.Log($"Couldn't deactivate automation admin {id} (status {response.Status}).");
        }
        catch (Exception ex)
        {
            ctx.Log($"Couldn't deactivate automation admin {id}: {ex.Message}");
        }
    }

    public static async Task<JsonNode?> FindAdminAsync(TestContext ctx, string email)
    {
        var list = (await ctx.Admin.GetAsync($"{Api}/admins", ct: ctx.Ct)).ExpectOk().Array();
        return list.FirstOrDefault(a => string.Equals(a?["email"]?.ToString(), email, StringComparison.OrdinalIgnoreCase));
    }

    public static Task<ApiResponse> AdminLoginAsync(TestContext ctx, string email, string password) =>
        ctx.Admin.PostAsync($"{Api}/auth/login", new { email, password }, ct: ctx.Ct);

    /// <summary>The user id of a client's owner (from the members admin API).</summary>
    public static async Task<string> OwnerIdAsync(TestContext ctx, TestClient client)
    {
        var items = (await ctx.Admin.GetAsync($"{Api}/members?clientId={client.ClientId}", ct: ctx.Ct)).ExpectOk().Array("items");
        var owner = items.FirstOrDefault(m => string.Equals(m?["email"]?.ToString(), client.Email, StringComparison.OrdinalIgnoreCase))
            ?? throw new TestFailure($"The members list of {client.Name} doesn't contain its owner {client.Email}.");
        return owner["userId"]!.ToString();
    }

    public static async Task<JsonNode> ClientDetailAsync(TestContext ctx, Guid clientId) =>
        (await ctx.Admin.GetAsync($"{Api}/clients/{clientId}", ct: ctx.Ct)).ExpectOk().RequireJson();

    /// <summary>A valid full client edit body (PUT clients/{id}); callers override single fields.</summary>
    public static Dictionary<string, object?> ClientBody(TestClient client) => new()
    {
        ["name"] = client.Name,
        ["phone"] = "+15550100999",
        ["email"] = client.Email,
        ["website"] = "https://example.com",
        ["countryCode"] = "LB",
        ["timezone"] = "Asia/Beirut",
        ["address"] = "Automation street 1",
        ["operatingHours"] = "Mon-Fri 9-5",
        ["bookingInfo"] = null,
    };

    /// <summary>Puts a client's details back to a valid state (finally blocks).</summary>
    public static async Task RestoreClientAsync(TestContext ctx, TestClient client)
    {
        var response = await ctx.Admin.PutAsync($"{Api}/clients/{client.ClientId}", ClientBody(client), CancellationToken.None);
        if (!response.IsSuccess) ctx.Log($"Couldn't restore {client.Name} (status {response.Status}).");
    }

    /// <summary>True when an HTTP client is still signed in (the dashboard answers 200 instead of a redirect to sign-in).</summary>
    public static async Task<bool> SignedInAsync(TestContext ctx, OmniClient http) =>
        (await http.GetAsync("/dashboard", ct: ctx.Ct)).Status == 200;

    /// <summary>Signs in with a fresh client and tells whether it worked (a redirect away from the account pages, and the
    /// dashboard then loads).</summary>
    public static async Task<bool> CanSignInAsync(TestContext ctx, string email, string password)
    {
        var (http, response) = await ctx.SignInAsync(email, password);
        if (response.Status != 302 || (response.Location ?? "").Contains("/Account/", StringComparison.OrdinalIgnoreCase)) return false;
        return await SignedInAsync(ctx, http);
    }

    /// <summary>Waits up to ~20 s for a condition that a membership cache may delay.</summary>
    public static async Task<bool> WithinAsync(TestContext ctx, Func<Task<bool>> probe, int seconds = 20)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (true)
        {
            if (await probe()) return true;
            if (DateTimeOffset.UtcNow > deadline) return false;
            await Task.Delay(2000, ctx.Ct);
        }
    }

    /// <summary>Adds a member user to a client the way an accepted invite does: an invite token is inserted (email isn't
    /// configured on staging) and accepted through the AcceptInvite page. Needs the database.</summary>
    public static async Task<(string Email, string Password)> NewMemberAsync(TestContext ctx, TestClient client, string label)
    {
        var ownerId = await OwnerIdAsync(ctx, client);
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var email = $"auto-{ctx.Tag}-{label}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}@{TestContext.EmailDomain}";
        await ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into identity.account_tokens (id, purpose, user_id, client_id, email, full_name, token_hash, created_by_user_id, expires_at, created_at)
            values (gen_random_uuid(), 'invite', null, @clientId, @email, 'Automation Member', @hash, @owner, now() + interval '1 day', now())
            """, new { email, hash, owner = ownerId }, ctx.Ct);
        var password = NewPassword();
        var page = $"/Account/AcceptInvite?token={Uri.EscapeDataString(raw)}";
        var accepted = await ctx.NewHttp().SubmitFormAsync(page, new Dictionary<string, string>
        {
            ["Token"] = raw, ["FullName"] = "Automation Member", ["Password"] = password, ["ConfirmPassword"] = password,
        }, ct: ctx.Ct);
        if (accepted.Status != 302)
            throw new TestFailure($"Accepting the member invite failed (status {accepted.Status}): {TestContext.PageError(accepted.Body) ?? "no error shown"}.");
        return (email, password);
    }

    public static string N(Guid id) => id.ToString("N");

    /// <summary>Creates a contact for a client (core fixture: POST /api/contacts with the ingest key). Needs the ingest key.</summary>
    public static Task<Guid> NewContactAsync(TestContext ctx, TestClient client, string label, string language = "en") =>
        ctx.Fixtures.NewContactAsync(label, client, language);

    /// <summary>Property names that must never appear in an admin answer (hashes, stamps, raw tokens).</summary>
    public static readonly string[] SecretProperties = ["passwordHash", "securityStamp", "accessToken", "refreshToken", "webhookVerifyToken", "appSecret", "apiKey"];

    /// <summary>Every property name in a JSON tree (lower-cased), for "no secret field" checks.</summary>
    public static IEnumerable<string> PropertyNames(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    yield return name;
                    foreach (var inner in PropertyNames(value)) yield return inner;
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                foreach (var inner in PropertyNames(item)) yield return inner;
                break;
        }
    }

    public static void NoSecretProperties(JsonNode? node, string what)
    {
        var found = PropertyNames(node).Where(p => SecretProperties.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
        Expect.True(found.Count == 0, $"{what} contains secret fields: {string.Join(", ", found)}.");
    }

    /// <summary>A valid business type body with all terms and one stage per category (names carry the run tag).</summary>
    public static object BusinessTypeBody(TestContext ctx, string code, string contactOne = "guest", string contactOther = "guests") => new
    {
        code,
        name = new Dictionary<string, string> { ["en"] = $"{TestContext.ClientNamePrefix} type {ctx.Tag}", ["fr"] = $"{TestContext.ClientNamePrefix} type FR", ["ar"] = $"{TestContext.ClientNamePrefix} نوع" },
        terminology = new Dictionary<string, Dictionary<string, object>>
        {
            ["en"] = new()
            {
                ["contact"] = new { one = contactOne, other = contactOther },
                ["member"] = new { one = "member", other = "members" },
                ["service"] = new { one = "service", other = "services" },
                ["booking"] = new { one = "visit", other = "visits" },
                ["order"] = new { one = "order", other = "orders" },
                ["client"] = new { one = "business", other = "businesses" },
            },
        },
        aiProfile = "Automation test business type. Not for real clients.",
        sortOrder = 990,
        stages = new object[]
        {
            Stage("new", "open", "New"), Stage("booked", "booked", "Booked"), Stage("attended", "attended", "Attended"),
            Stage("no_show", "no_show", "No show"), Stage("converted", "converted", "Converted"), Stage("lost", "closed_lost", "Lost"),
            Stage("needs_human", "needs_human", "Needs a person"),
        },
    };

    private static object Stage(string key, string category, string label) =>
        new { key, category, labels = new Dictionary<string, string> { ["en"] = label } };

    /// <summary>The run's automation business type code (one fixed code, re-used across runs, inactive between them).</summary>
    public const string TypeCode = "auto_automation_type";

    /// <summary>Creates the automation business type, or resets an existing one to the standard body, and makes it active.
    /// Callers deactivate it in finally with <see cref="DeactivateTypeAsync"/>.</summary>
    public static async Task<JsonNode> EnsureTypeAsync(TestContext ctx, string contactOne = "guest", string contactOther = "guests")
    {
        var body = BusinessTypeBody(ctx, TypeCode, contactOne, contactOther);
        var created = await ctx.Admin.PostAsync($"{Api}/business-types", body, ct: ctx.Ct);
        if (created.Status != 200)
        {
            created.ExpectStatus(409);
            (await ctx.Admin.PutAsync($"{Api}/business-types/{TypeCode}", body, ctx.Ct)).ExpectOk();
        }
        return (await ctx.Admin.PutAsync($"{Api}/business-types/{TypeCode}/active", new { active = true }, ctx.Ct)).ExpectOk().RequireJson();
    }

    public static async Task DeactivateTypeAsync(TestContext ctx)
    {
        try
        {
            var response = await ctx.Admin.PutAsync($"{Api}/business-types/{TypeCode}/active", new { active = false }, CancellationToken.None);
            if (!response.IsSuccess) ctx.Log($"Couldn't deactivate business type {TypeCode} (status {response.Status}).");
        }
        catch (Exception ex)
        {
            ctx.Log($"Couldn't deactivate business type {TypeCode}: {ex.Message}");
        }
    }

    /// <summary>Inserts a fake Telegram connection on a client and drops the client's cached connection list so the app sees it.</summary>
    public static async Task<FakeChannel> FakeTelegramAsync(TestContext ctx, TestClient client)
    {
        var channel = await ctx.Webhooks.FakeTelegramAsync(client);
        await ctx.Fixtures.EvictCachePrefixAsync("channels:integrations:" + N(client.ClientId));
        return channel;
    }
}
