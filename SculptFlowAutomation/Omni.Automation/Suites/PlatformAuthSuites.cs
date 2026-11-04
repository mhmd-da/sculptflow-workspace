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
/// <summary>Self sign-up (Register): both business types, what a new client gets, validation, duplicate email, the
/// sign-up language, and the Accounts sign-up settings (closed sign-up, per-IP limit). Every refused attempt still
/// counts toward the per-IP hourly limit (20 on staging), so the suite keeps attempts few.</summary>
[Suite(1)]
public static class SignupSuite
{
    public static TestSuite Build() => new SuiteBuilder("signup", "Sign-up", "Register a business: both business types, validation, duplicate email, language and the sign-up settings.")
        .Test("general", "A general business can sign up and lands on its dashboard", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var dashboard = (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
            Expect.True(dashboard.Body.Contains(WebUtility.HtmlEncode(client.Name), StringComparison.Ordinal) || dashboard.Body.Contains(client.Name, StringComparison.Ordinal),
                "The dashboard doesn't show the new client's name.");
        })
        .Test("clinic", "A clinic can sign up and lands on its dashboard", async ctx =>
        {
            var clinic = await ctx.ClinicClientAsync();
            (await clinic.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
        })
        .Test("defaults", "A new client is the owner (role client) on the free trial, in UTC, with an empty wallet", async ctx =>
        {
            foreach (var client in new[] { await ctx.ClientAsync(), await ctx.ClinicClientAsync() })
            {
                var rows = await ctx.Db.QueryAsync("""
                    select c.timezone, c.business_type_code, u.role, u.email_confirmed, p.code as plan, s.status, a.wallet_balance, a.currency
                    from core.clients c
                    join identity.client_users cu on cu.client_id = c.id
                    join identity.users u on u.id = cu.user_id
                    left join billing.subscriptions s on s.client_id = c.id
                    left join billing.plans p on p.id = s.plan_id
                    left join billing.accounts a on a.client_id = c.id
                    where c.id = @id
                    """, new { id = client.ClientId }, ctx.Ct);
                Expect.Equal(1, rows.Count, $"{client.Label}: users / subscriptions rows");
                var row = rows[0];
                Expect.Equal(client.BusinessType, row["business_type_code"]?.ToString(), $"{client.Label}: business type");
                Expect.Equal("UTC", row["timezone"]?.ToString(), $"{client.Label}: timezone");
                Expect.Equal("client", row["role"]?.ToString(), $"{client.Label}: owner's role");
                Expect.Equal("trial", row["plan"]?.ToString(), $"{client.Label}: plan");
                Expect.Equal("active", row["status"]?.ToString(), $"{client.Label}: subscription status");
                Expect.True(row["wallet_balance"] is decimal balance && balance == 0m, $"{client.Label}: the wallet should start empty (got {row["wallet_balance"] ?? "no billing account"}).");
            }
        }, needs: Needs.Session | Needs.Database)
        .Test("no-token", "A sign-up without the antiforgery token is refused (400)", async ctx =>
        {
            var http = ctx.NewHttp();
            // An invalid email too, so nothing could be created even if the token check were missing.
            var body = await new FormUrlEncodedContent(SignupHelpers.Fields(ctx, email: "not-an-email")).ReadAsStringAsync(ctx.Ct);
            var response = await http.PostRawAsync("/Account/Register", body, "application/x-www-form-urlencoded", ct: ctx.Ct);
            Expect.Equal(400, response.Status, "Status of a sign-up POST without the token");
        }, needs: Needs.None)
        .Test("weak-password", "A weak password is refused and no account is created", async ctx =>
        {
            var email = SignupHelpers.NewEmail(ctx, "weak");
            var response = await SignupHelpers.SubmitAsync(ctx, ctx.NewHttp(), SignupHelpers.Fields(ctx, email: email, password: "abcdef"));
            Expect.Equal(200, response.Status, "Sign-up with password 'abcdef'");
            Expect.True(PlatformHelpers.Alert(response).Length > 0, "No error is shown for a weak password.");
            var (_, signIn) = await ctx.SignInAsync(email, "abcdef");
            Expect.Equal(200, signIn.Status, "Signing in with the refused account (must not exist)");
        }, needs: Needs.None)
        .Test("invalid-email", "An invalid email is refused with a clear message", async ctx =>
        {
            var response = await SignupHelpers.SubmitAsync(ctx, ctx.NewHttp(), SignupHelpers.Fields(ctx, email: "not-an-email"));
            Expect.Equal(200, response.Status, "Sign-up with email 'not-an-email'");
            Expect.True(PlatformHelpers.Alert(response).Contains("valid email", StringComparison.OrdinalIgnoreCase),
                $"Expected 'Enter a valid email address.', got \"{PlatformHelpers.Alert(response)}\".");
        }, needs: Needs.None)
        .Test("unknown-type", "An unknown business type is refused", async ctx =>
        {
            var response = await SignupHelpers.SubmitAsync(ctx, ctx.NewHttp(), SignupHelpers.Fields(ctx, email: SignupHelpers.NewEmail(ctx, "type"), businessType: "xyz"));
            Expect.Equal(200, response.Status, "Sign-up with business type 'xyz'");
            Expect.True(PlatformHelpers.Alert(response).Contains("kind of business", StringComparison.OrdinalIgnoreCase),
                $"Expected 'Choose what kind of business you run.', got \"{PlatformHelpers.Alert(response)}\".");
        }, needs: Needs.None)
        .Test("duplicate-email", "An email that already has an account (any case) is refused with a 'sign in instead' offer", async ctx =>
        {
            var existing = await ctx.ClientAsync();
            var response = await SignupHelpers.SubmitAsync(ctx, ctx.NewHttp(), SignupHelpers.Fields(ctx, email: existing.Email.ToUpperInvariant()));
            Expect.Equal(200, response.Status, "Sign-up with an existing email in upper case");
            Expect.True(response.Body.Contains("already exists", StringComparison.OrdinalIgnoreCase), $"The page doesn't say the account exists: \"{PlatformHelpers.Alert(response)}\".");
            Expect.True(response.Body.Contains("Sign in instead", StringComparison.OrdinalIgnoreCase), "The page doesn't offer 'Sign in instead'.");
            await PlatformHelpers.SignedInAsync(ctx, existing.Email, existing.Password);
        })
        .Test("language", "Signing up after picking Arabic makes Arabic the new client's default language", async ctx =>
        {
            var http = ctx.NewHttp();
            // The sign-in pages' language picker (POST /language) sets the omni.lang cookie for the visitor.
            var picked = await http.SubmitFormAsync("/Account/Register", new Dictionary<string, string> { ["language"] = "ar", ["returnUrl"] = "/Account/Register" },
                postTo: "/language", ct: ctx.Ct);
            Expect.Equal(302, picked.Status, "Picking Arabic on the sign-up page");
            var page = (await http.GetAsync("/Account/Register", ct: ctx.Ct)).ExpectOk();
            Expect.True(PlatformHelpers.HtmlTag(page.Body).Contains("dir=\"rtl\""), $"The sign-up page isn't right-to-left in Arabic: {PlatformHelpers.HtmlTag(page.Body)}");

            var email = SignupHelpers.NewEmail(ctx, "lang");
            Guid? clientId = null;
            try
            {
                var response = await SignupHelpers.SubmitAsync(ctx, http, SignupHelpers.Fields(ctx, email: email, label: "lang"));
                Expect.Equal(302, response.Status, $"Sign-up in Arabic ({PlatformHelpers.Alert(response)})");
                clientId = (await http.PostAsync("/api/services", new { name = "Lang", code = $"auto-{ctx.Tag}-lang", bookingDuration = 30 }, ct: ctx.Ct))
                    .ExpectStatus(201).Id("clientId");
                // A new owner has no language of their own, so the dashboard opens in the client's default.
                var dashboard = (await http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
                var tag = PlatformHelpers.HtmlTag(dashboard.Body);
                Expect.True(tag.Contains("lang=\"ar\"") && tag.Contains("dir=\"rtl\""), $"The new client's dashboard isn't in Arabic: {tag}");
            }
            finally
            {
                if (clientId is { } id)
                {
                    var deleted = await ctx.Admin.DeleteAsync($"/api/platform-admin/automation/clients/{id}", CancellationToken.None);
                    ctx.Log($"Deleted the Arabic sign-up client: {deleted.Status}.");
                }
            }
        }, needs: Needs.AdminKey)
        .Test("closed", "With sign-up turned off, the page says so, shows no form and refuses a posted sign-up", async ctx =>
        {
            var stored = await PlatformHelpers.StoredSettingAsync(ctx, "Accounts", "SignupEnabled");
            try
            {
                await PlatformHelpers.SetSettingAsync(ctx, "Accounts", "SignupEnabled", "false");
                var page = (await ctx.Anonymous.GetAsync("/Account/Register", ct: ctx.Ct)).ExpectOk();
                Expect.True(page.Body.Contains("sign-ups are closed", StringComparison.OrdinalIgnoreCase), "The sign-up page doesn't say sign-ups are closed.");
                Expect.True(!page.Body.Contains("name=\"Password\"", StringComparison.Ordinal), "The closed sign-up page still shows the form.");

                // The form is gone, so post with the sign-in page's token (antiforgery tokens aren't page-specific).
                var email = SignupHelpers.NewEmail(ctx, "closed");
                var response = await ctx.NewHttp().SubmitFormAsync("/Account/Login", SignupHelpers.Fields(ctx, email: email, label: "closed"), postTo: "/Account/Register", ct: ctx.Ct);
                Expect.Equal(200, response.Status, "A posted sign-up while closed");
                Expect.True(response.Body.Contains("sign-ups are closed", StringComparison.OrdinalIgnoreCase), $"The refusal doesn't say sign-ups are closed: \"{PlatformHelpers.Alert(response)}\".");
                var (_, signIn) = await ctx.SignInAsync(email, SignupHelpers.Password);
                Expect.Equal(200, signIn.Status, "Signing in with the refused account (must not exist)");
            }
            finally
            {
                await PlatformHelpers.RestoreSettingAsync(ctx, "Accounts", "SignupEnabled", stored);
            }
        }, needs: Needs.AdminKey, risk: Risk.Shared, description: "Turns Accounts:SignupEnabled off for a few seconds (every sign-up on the target is refused meanwhile) and restores it.")
        .Test("rate-limit", "Sign-up attempts over Accounts:SignupsPerIpPerHour get 429", async ctx =>
        {
            var stored = await PlatformHelpers.StoredSettingAsync(ctx, "Accounts", "SignupsPerIpPerHour");
            // The limit is part of the limiter's partition, so a small, rarely used value gets a fresh window and
            // leaves the normal one alone. Empty forms create nothing.
            var limit = Random.Shared.Next(2, 10);
            try
            {
                await PlatformHelpers.SetSettingAsync(ctx, "Accounts", "SignupsPerIpPerHour", limit.ToString());
                var http = ctx.NewHttp(limited: true);
                ApiResponse? refused = null;
                for (var attempt = 1; attempt <= limit + 1 && refused is null; attempt++)
                {
                    var response = await http.SubmitFormAsync("/Account/Register", new Dictionary<string, string>(), ct: ctx.Ct);
                    if (response.Status == 429) refused = response;
                    else Expect.Equal(200, response.Status, $"Empty sign-up attempt {attempt} of {limit}");
                }
                Expect.True(refused is not null, $"{limit + 1} sign-up attempts in a row were all accepted with a limit of {limit} per hour.");
                Expect.True(refused!.Body.Contains("Too many sign-up attempts", StringComparison.OrdinalIgnoreCase), $"The 429 doesn't explain itself.{refused.Snippet(200)}");
                // A limiter keyed on the visitor keeps refusing for the rest of the hour. One keyed on a changing proxy
                // address refuses now and then (a partition shared with earlier attempts) and accepts the next attempt.
                for (var extra = 1; extra <= 3; extra++)
                {
                    var again = await http.SubmitFormAsync("/Account/Register", new Dictionary<string, string>(), ct: ctx.Ct);
                    Expect.Equal(429, again.Status, $"Attempt {extra} after the first 429 (the limiter isn't keyed on the visitor's IP)");
                }
            }
            finally
            {
                await PlatformHelpers.RestoreSettingAsync(ctx, "Accounts", "SignupsPerIpPerHour", stored);
            }
        }, needs: Needs.AdminKey, risk: Risk.Shared, description: "Lowers Accounts:SignupsPerIpPerHour for a few seconds (sign-ups from this machine's IP may be refused meanwhile) and restores it. Found while automating: the limiter keys on a changing proxy address, so a 429 almost never arrives on staging, and when one does the next attempt is accepted again.", finding: "t11-auto-01")
        .Test("signin-rate-limit", "Sign-in attempts over Accounts:AttemptsPerIpPerHour get 429, and keep getting it", async ctx =>
        {
            var stored = await PlatformHelpers.StoredSettingAsync(ctx, "Accounts", "AttemptsPerIpPerHour");
            // A rarely used value gets a fresh window (the limit is part of the partition). Empty forms sign nobody in or out.
            var limit = Random.Shared.Next(5, 15);
            try
            {
                await PlatformHelpers.SetSettingAsync(ctx, "Accounts", "AttemptsPerIpPerHour", limit.ToString());
                var http = ctx.NewHttp(limited: true);
                ApiResponse? refused = null;
                for (var attempt = 1; attempt <= limit + 1 && refused is null; attempt++)
                {
                    var response = await http.SubmitFormAsync("/Account/Login", new Dictionary<string, string>(), ct: ctx.Ct);
                    if (response.Status == 429) refused = response;
                    else Expect.Equal(200, response.Status, $"Empty sign-in attempt {attempt} of {limit}");
                }
                Expect.True(refused is not null, $"{limit + 1} sign-in attempts in a row were all accepted with a limit of {limit} per hour.");
                Expect.True(refused!.Body.Contains("Too many attempts", StringComparison.OrdinalIgnoreCase), $"The 429 doesn't explain itself.{refused.Snippet(200)}");
                var again = await http.SubmitFormAsync("/Account/ForgotPassword", new Dictionary<string, string>(), ct: ctx.Ct);
                Expect.Equal(429, again.Status, "Forgot password from the same address after the limit (one shared limit)");
            }
            finally
            {
                await PlatformHelpers.RestoreSettingAsync(ctx, "Accounts", "AttemptsPerIpPerHour", stored);
            }
        }, needs: Needs.AdminKey, risk: Risk.Shared, description: "Lowers Accounts:AttemptsPerIpPerHour for a few seconds (sign-ins from this machine's IP may be refused meanwhile) and restores it.", finding: "t1-accounts-08")
        .Build();
}

/// <summary>Sign-up form helpers.</summary>
internal static class SignupHelpers
{
    public const string Password = "Auto-Signup-2026x";

    public static string NewEmail(TestContext ctx, string label) =>
        $"auto-{ctx.Tag}-{label}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}@{TestContext.EmailDomain}";

    public static List<KeyValuePair<string, string>> Fields(TestContext ctx, string email, string password = Password,
        string businessType = "general", string label = "signup") => new()
    {
        new("FullName", $"Automation {label}"),
        new("ClientName", $"{TestContext.ClientNamePrefix} {label} {ctx.Tag}"),
        new("Email", email),
        new("BusinessTypeCode", businessType),
        new("Password", password),
        new("ConfirmPassword", password),
    };

    public static Task<ApiResponse> SubmitAsync(TestContext ctx, OmniClient http, IEnumerable<KeyValuePair<string, string>> fields) =>
        http.SubmitFormAsync("/Account/Register", fields, ct: ctx.Ct);
}

/// <summary>Sign-in and sign-out: wrong password, lockout, return URLs (open redirect), and the Google sign-in pages
/// without going to Google.</summary>
[Suite(2)]
public static class SignInSuite
{
    private static readonly string[] ExternalReturnUrls =
    {
        "https://evil.example", "//evil.example", "/\\evil.example", "\\\\evil.example", "/%5Cevil.example", "javascript:alert(1)", " //evil.example",
    };

    public static TestSuite Build() => new SuiteBuilder("signin", "Sign-in and sign-out", "Password sign-in, lockout, return URLs, sign-out and the Google sign-in pages.")
        .Test("ok", "The right password signs in and opens the dashboard", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (http, response) = await ctx.SignInAsync(client.Email, client.Password);
            Expect.Equal(302, response.Status, "Sign-in status");
            Expect.Equal("/dashboard", response.Location, "Where sign-in goes");
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, http), "An API call with the new session");
        })
        .Test("wrong-password", "A wrong password is refused with a neutral message and no session", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (http, response) = await ctx.SignInAsync(client.Email, client.Password + "x");
            Expect.Equal(200, response.Status, "Sign-in with a wrong password");
            Expect.True(PlatformHelpers.Alert(response).Contains("don't match", StringComparison.OrdinalIgnoreCase), $"Unexpected message: \"{PlatformHelpers.Alert(response)}\".");
            Expect.Equal(401, await PlatformHelpers.SessionStatusAsync(ctx, http), "An API call after the refused sign-in");
            await PlatformHelpers.SignedInAsync(ctx, client.Email, client.Password); // resets the failed-attempt count
        })
        .Test("unknown-email", "An unknown email gets the same message as a wrong password", async ctx =>
        {
            var (_, response) = await ctx.SignInAsync(SignupHelpers.NewEmail(ctx, "nobody"), "Whatever-2026x");
            Expect.Equal(200, response.Status, "Sign-in with an unknown email");
            Expect.True(PlatformHelpers.Alert(response).Contains("don't match", StringComparison.OrdinalIgnoreCase), $"Unexpected message: \"{PlatformHelpers.Alert(response)}\".");
        }, needs: Needs.None)
        .Test("empty", "Empty fields are refused with 'Enter your email and password'", async ctx =>
        {
            var (_, response) = await ctx.SignInAsync("", "");
            Expect.Equal(200, response.Status, "Sign-in with empty fields");
            Expect.True(PlatformHelpers.Alert(response).Contains("Enter your email and password", StringComparison.OrdinalIgnoreCase), $"Unexpected message: \"{PlatformHelpers.Alert(response)}\".");
        }, needs: Needs.None)
        .Test("no-token", "A sign-in POST without the antiforgery token is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var body = $"Email={Uri.EscapeDataString(client.Email)}&Password={Uri.EscapeDataString(client.Password)}";
            var response = await ctx.NewHttp().PostRawAsync("/Account/Login", body, "application/x-www-form-urlencoded", ct: ctx.Ct);
            Expect.Equal(400, response.Status, "Sign-in POST without the token");
        })
        .Test("lockout", "Five wrong passwords lock the account: the right one is refused, an open session keeps working", async ctx =>
        {
            var acct = await PlatformHelpers.AcctAsync(ctx);
            var email = acct.Client.Email;
            var open = await PlatformHelpers.SignedInAsync(ctx, email, acct.Password);
            try
            {
                for (var i = 1; i <= 5; i++) await ctx.SignInAsync(email, $"Wrong-{i}-Password9");
                var (http, locked) = await ctx.SignInAsync(email, acct.Password);
                Expect.Equal(200, locked.Status, "Sign-in with the right password after 5 wrong ones");
                Expect.True(PlatformHelpers.Alert(locked).Contains("Too many attempts", StringComparison.OrdinalIgnoreCase), $"Expected the lockout message, got \"{PlatformHelpers.Alert(locked)}\".");
                Expect.Equal(401, await PlatformHelpers.SessionStatusAsync(ctx, http), "API call after the locked-out sign-in");
                Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, open), "The session opened before the lockout");
            }
            finally
            {
                await PlatformHelpers.UnlockUsersAsync(ctx, acct.Client);
            }
            await PlatformHelpers.SignedInAsync(ctx, email, acct.Password);
        }, needs: Needs.Session | Needs.Database)
        .Test("return-url-external", "Sign-in ignores external return URLs (no open redirect)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var problems = new List<string>();
            foreach (var returnUrl in ExternalReturnUrls)
            {
                var path = "/Account/Login?ReturnUrl=" + Uri.EscapeDataString(returnUrl);
                var response = await ctx.NewHttp().SubmitFormAsync(path, new Dictionary<string, string> { ["Email"] = client.Email, ["Password"] = client.Password }, ct: ctx.Ct);
                if (response.Status != 302) problems.Add($"{returnUrl}: status {response.Status}");
                else if (!PlatformHelpers.IsSameSite(ctx, response.Location))
                    problems.Add($"{returnUrl}: redirected to {response.Location}");
            }
            Expect.True(problems.Count == 0, "Sign-in followed an external return URL: " + string.Join("; ", problems));
        })
        .Test("return-url-local", "Sign-in honours a local return URL", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var response = await ctx.NewHttp().SubmitFormAsync("/Account/Login?ReturnUrl=%2FMembers",
                new Dictionary<string, string> { ["Email"] = client.Email, ["Password"] = client.Password }, ct: ctx.Ct);
            Expect.Equal(302, response.Status, "Sign-in status");
            Expect.Equal("/Members", response.Location, "Where sign-in goes");
        })
        .Test("return-url-encoded", "A return URL with markup is encoded on the sign-in page, never reflected raw", async ctx =>
        {
            const string payload = "\"><script>alert('omni')</script>";
            var page = (await ctx.Anonymous.GetAsync("/Account/Login?ReturnUrl=" + Uri.EscapeDataString(payload), ct: ctx.Ct)).ExpectOk();
            Expect.True(!page.Body.Contains("<script>alert('omni')</script>", StringComparison.Ordinal), "The sign-in page reflects the return URL's markup raw.");
        }, needs: Needs.None)
        .Test("sign-out", "Signing out (POST with token) ends the session", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var http = await PlatformHelpers.SignedInAsync(ctx, client.Email, client.Password);
            var response = await http.SubmitFormAsync("/dashboard", new Dictionary<string, string>(), postTo: "/Account/Logout", ct: ctx.Ct);
            Expect.Equal(302, response.Status, "Sign-out status");
            Expect.True(response.Location?.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase) == true, $"Sign-out went to {response.Location}.");
            Expect.Equal(401, await PlatformHelpers.SessionStatusAsync(ctx, http), "An API call after signing out");
            Expect.Equal(302, (await http.GetAsync("/dashboard", ct: ctx.Ct)).Status, "Opening the dashboard after signing out");
        })
        .Test("sign-out-get", "GET /Account/Logout and a POST without the token don't sign out", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var http = await PlatformHelpers.SignedInAsync(ctx, client.Email, client.Password);
            (await http.GetAsync("/Account/Logout", ct: ctx.Ct)).ExpectStatus(302);
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, http), "Session after GET /Account/Logout");
            var noToken = await http.PostRawAsync("/Account/Logout", "", "application/x-www-form-urlencoded", ct: ctx.Ct);
            Expect.Equal(400, noToken.Status, "Sign-out POST without the token");
            Expect.Equal(200, await PlatformHelpers.SessionStatusAsync(ctx, http), "Session after the refused sign-out");
        })
        .Test("auth-cookie", "The session cookie is Secure, HttpOnly and SameSite=Lax", async ctx =>
        {
            var client = await ctx.ClientAsync();
            using var raw = new RawSession(ctx);
            var response = await raw.SignInAsync(client.Email, client.Password);
            var cookie = (response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
                .FirstOrDefault(c => c.StartsWith("omni.auth=", StringComparison.Ordinal))
                ?? throw new TestFailure("Sign-in set no omni.auth cookie.");
            var flags = cookie[(cookie.IndexOf(';') + 1)..].ToLowerInvariant();
            Expect.True(flags.Contains("secure"), "The omni.auth cookie isn't Secure.");
            Expect.True(flags.Contains("httponly"), "The omni.auth cookie isn't HttpOnly.");
            Expect.True(flags.Contains("samesite=lax"), $"The omni.auth cookie isn't SameSite=Lax ({flags.Trim()}).");
        })
        .Test("google-start", "'Continue with Google' needs the form token and sends the visitor to Google", async ctx =>
        {
            var http = ctx.NewHttp();
            var login = (await http.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk();
            if (!login.Body.Contains("/auth/google/start", StringComparison.OrdinalIgnoreCase)) throw new TestSkipped("Google sign-in isn't configured on this target.");
            var noToken = await ctx.NewHttp().PostRawAsync("/auth/google/start", "returnUrl=%2Fdashboard", "application/x-www-form-urlencoded", ct: ctx.Ct);
            Expect.Equal(400, noToken.Status, "Google start without the token");
            var start = await http.SubmitFormAsync("/Account/Login", new Dictionary<string, string> { ["returnUrl"] = "https://evil.example" }, postTo: "/auth/google/start", ct: ctx.Ct);
            Expect.Equal(302, start.Status, "Google start with the token");
            Expect.True(start.Location?.StartsWith("https://accounts.google.com/", StringComparison.OrdinalIgnoreCase) == true, $"Google start went to {start.Location}.");
            Expect.True(!start.Location!.Contains("evil.example", StringComparison.OrdinalIgnoreCase), "The external return URL was passed on to Google's round trip.");
        }, needs: Needs.None)
        .Test("google-callback", "The Google callback without a Google identity goes back to sign-in with 'failed'", async ctx =>
        {
            var response = await ctx.NewHttp().GetAsync("/auth/google/callback?returnUrl=https%3A%2F%2Fevil.example", ct: ctx.Ct);
            Expect.Equal(302, response.Status, "Callback status");
            Expect.Equal("/Account/Login?externalError=failed", response.Location, "Where the callback goes");
        }, needs: Needs.None)
        .Test("google-complete", "The Google sign-up completion page without a Google identity goes back to sign-in", async ctx =>
        {
            var response = await ctx.NewHttp().GetAsync("/Account/CompleteGoogleSignup", ct: ctx.Ct);
            Expect.Equal(302, response.Status, "Completion page status");
            Expect.Equal("/Account/Login?externalError=unverified", response.Location, "Where the completion page goes");
        }, needs: Needs.None)
        .Test("google-errors", "Each Google error code shows its own text; unknown codes and markup show nothing", async ctx =>
        {
            var texts = new Dictionary<string, string>();
            foreach (var code in new[] { "cancelled", "failed", "unverified", "locked", "signup_closed" })
            {
                var page = (await ctx.Anonymous.GetAsync("/Account/Login?externalError=" + code, ct: ctx.Ct)).ExpectOk();
                var text = PlatformHelpers.Alert(page);
                Expect.True(text.Length > 0, $"externalError={code} shows no message.");
                texts[code] = text;
            }
            Expect.Equal(texts.Count, texts.Values.Distinct().Count(), "Distinct messages for the 5 Google error codes");
            foreach (var bogus in new[] { "bogus", "<script>alert(1)</script>" })
            {
                var page = (await ctx.Anonymous.GetAsync("/Account/Login?externalError=" + Uri.EscapeDataString(bogus), ct: ctx.Ct)).ExpectOk();
                Expect.True(TestContext.PageError(page.Body) is null, $"externalError={bogus} shows a message: \"{PlatformHelpers.Alert(page)}\".");
                Expect.True(!page.Body.Contains("<script>alert(1)</script>", StringComparison.Ordinal), "The error code is reflected raw.");
            }
            var done = (await ctx.Anonymous.GetAsync("/Account/Login?reset=done", ct: ctx.Ct)).ExpectOk();
            Expect.True(done.Body.Contains("Your password was changed", StringComparison.OrdinalIgnoreCase), "?reset=done doesn't say the password was changed.");
        }, needs: Needs.None)
        .Build();
}

/// <summary>Forgot / reset password without email: the neutral answer and hourly cap, and reset links inserted in the
/// database (the way QA tested them): weak and mismatched passwords, success, reuse, expiry, other sessions signed out,
/// sibling links revoked, lockout cleared.</summary>
[Suite(3)]
public static class PasswordResetSuite
{
    public static TestSuite Build() => new SuiteBuilder("password-reset", "Password reset", "Forgot password and reset links (made in the database, since staging sends no email).")
        .Test("forgot-neutral", "Forgot password gives the same neutral answer for a known and an unknown email", async ctx =>
        {
            var known = await ctx.OtherClientAsync();
            var knownPage = await ctx.NewHttp().SubmitFormAsync("/Account/ForgotPassword", new Dictionary<string, string> { ["Email"] = known.Email }, ct: ctx.Ct);
            var unknownPage = await ctx.NewHttp().SubmitFormAsync("/Account/ForgotPassword", new Dictionary<string, string> { ["Email"] = SignupHelpers.NewEmail(ctx, "nobody") }, ct: ctx.Ct);
            Expect.Equal(200, knownPage.Status, "Forgot password for a known email");
            Expect.Equal(200, unknownPage.Status, "Forgot password for an unknown email");
            Expect.True(knownPage.Body.Contains("If an Omni account uses that email", StringComparison.OrdinalIgnoreCase), "The known email doesn't get the neutral answer.");
            Expect.Equal(PlatformHelpers.Alert(knownPage), PlatformHelpers.Alert(unknownPage), "The answer for a known vs an unknown email");
        })
        .Test("forgot-empty", "Forgot password with no email asks for one", async ctx =>
        {
            var page = await ctx.NewHttp().SubmitFormAsync("/Account/ForgotPassword", new Dictionary<string, string> { ["Email"] = "" }, ct: ctx.Ct);
            Expect.Equal(200, page.Status, "Forgot password with an empty email");
            Expect.True(page.Body.Contains("Enter your email", StringComparison.OrdinalIgnoreCase), $"Unexpected answer: \"{PlatformHelpers.Alert(page)}\".");
        }, needs: Needs.None)
        .Test("forgot-cap", "At most 3 reset links per account per hour, whatever the number of requests", async ctx =>
        {
            var known = await ctx.OtherClientAsync();
            for (var i = 0; i < 4; i++)
            {
                (await ctx.NewHttp().SubmitFormAsync("/Account/ForgotPassword", new Dictionary<string, string> { ["Email"] = known.Email }, ct: ctx.Ct)).ExpectOk();
            }
            var count = await ctx.Db.ScalarAsync<long>("""
                select count(*) from identity.account_tokens t join identity.users u on u.id = t.user_id
                where t.purpose = 'password_reset' and lower(u.email) = lower(@email) and t.created_at > now() - interval '1 hour'
                """, new { email = known.Email }, ctx.Ct);
            Expect.Equal(3L, count, "Reset links created in the last hour after 4+ requests");
        }, needs: Needs.Session | Needs.Database)
        .Test("dead-link", "A reset link that doesn't exist shows 'this link no longer works' and no form", async ctx =>
        {
            var page = (await ctx.Anonymous.GetAsync("/Account/ResetPassword?token=" + PlatformHelpers.NewToken(), ct: ctx.Ct)).ExpectOk();
            Expect.True(!PlatformHelpers.HasPasswordField(page.Body), "A made-up link shows the new-password form.");
            Expect.True(page.Body.Contains("no longer works", StringComparison.OrdinalIgnoreCase), "A made-up link doesn't say it no longer works.");
        }, needs: Needs.None)
        .Test("weak", "A reset with a weak password is refused and the link still works", async ctx =>
        {
            var acct = await PlatformHelpers.AcctAsync(ctx);
            var token = await PlatformHelpers.InsertResetTokenAsync(ctx, acct.Client, acct.UserId);
            var path = "/Account/ResetPassword?token=" + Uri.EscapeDataString(token);
            Expect.True(PlatformHelpers.HasPasswordField((await ctx.Anonymous.GetAsync(path, ct: ctx.Ct)).ExpectOk().Body), "A fresh reset link doesn't show the form.");
            var response = await ctx.NewHttp().SubmitFormAsync(path, ResetFields(token, "short", "short"), ct: ctx.Ct);
            Expect.Equal(200, response.Status, "Reset with password 'short'");
            Expect.True(PlatformHelpers.Alert(response).Length > 0, "No error for a weak password.");
            Expect.True(PlatformHelpers.HasPasswordField(response.Body), "The form is gone after a weak password (the link should still work).");
            await PlatformHelpers.SignedInAsync(ctx, acct.Client.Email, acct.Password);
        }, needs: Needs.Session | Needs.Database)
        .Test("mismatch", "A reset whose two passwords differ is refused", async ctx =>
        {
            var acct = await PlatformHelpers.AcctAsync(ctx);
            var token = await PlatformHelpers.InsertResetTokenAsync(ctx, acct.Client, acct.UserId);
            var response = await ctx.NewHttp().SubmitFormAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(token),
                ResetFields(token, "Auto-Reset-2026a", "Auto-Reset-2026b"), ct: ctx.Ct);
            Expect.Equal(200, response.Status, "Reset with two different passwords");
            Expect.True(PlatformHelpers.Alert(response).Contains("don't match", StringComparison.OrdinalIgnoreCase), $"Unexpected message: \"{PlatformHelpers.Alert(response)}\".");
        }, needs: Needs.Session | Needs.Database)
        .Test("success", "A reset link sets the new password once, signs out other sessions and revokes sibling links", async ctx =>
        {
            var acct = await PlatformHelpers.AcctAsync(ctx);
            var open = await PlatformHelpers.SignedInAsync(ctx, acct.Client.Email, acct.Password);
            var token = await PlatformHelpers.InsertResetTokenAsync(ctx, acct.Client, acct.UserId);
            var sibling = await PlatformHelpers.InsertResetTokenAsync(ctx, acct.Client, acct.UserId);
            var oldPassword = acct.Password;
            var newPassword = "Auto-Reset-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

            var response = await ctx.NewHttp().SubmitFormAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(token),
                ResetFields(token, newPassword, newPassword), ct: ctx.Ct);
            Expect.Equal(302, response.Status, $"Reset status ({PlatformHelpers.Alert(response)})");
            acct.Password = newPassword;
            Expect.Equal("/Account/Login?reset=done", response.Location, "Where the reset goes");

            await PlatformHelpers.SignedInAsync(ctx, acct.Client.Email, newPassword);
            var (_, old) = await ctx.SignInAsync(acct.Client.Email, oldPassword);
            Expect.Equal(200, old.Status, "Signing in with the old password");
            Expect.Equal(401, await PlatformHelpers.SessionStatusAsync(ctx, open), "The session opened before the reset");

            foreach (var (label, used) in new[] { ("the used link", token), ("a sibling link issued before the reset", sibling) })
            {
                var page = (await ctx.Anonymous.GetAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(used), ct: ctx.Ct)).ExpectOk();
                Expect.True(!PlatformHelpers.HasPasswordField(page.Body), $"{label} still shows the form.");
            }
            var reuse = await ctx.NewHttp().SubmitFormAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(token),
                ResetFields(token, "Auto-Reuse-2026x", "Auto-Reuse-2026x"), ct: ctx.Ct);
            Expect.True(reuse.Status == 200 && !PlatformHelpers.HasPasswordField(reuse.Body), $"Reusing the link answered {reuse.Status} {reuse.Location}.");
            var (_, reused) = await ctx.SignInAsync(acct.Client.Email, "Auto-Reuse-2026x");
            Expect.Equal(200, reused.Status, "Signing in with the password from the reused link");
        }, needs: Needs.Session | Needs.Database)
        .Test("clears-lockout", "A password reset lifts a lockout straight away", async ctx =>
        {
            var acct = await PlatformHelpers.AcctAsync(ctx);
            try
            {
                for (var i = 1; i <= 5; i++) await ctx.SignInAsync(acct.Client.Email, $"Wrong-{i}-Password9");
                var (_, locked) = await ctx.SignInAsync(acct.Client.Email, acct.Password);
                Expect.True(PlatformHelpers.Alert(locked).Contains("Too many attempts", StringComparison.OrdinalIgnoreCase), "The account wasn't locked by 5 wrong passwords.");

                var token = await PlatformHelpers.InsertResetTokenAsync(ctx, acct.Client, acct.UserId);
                var newPassword = "Auto-Reset-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
                var response = await ctx.NewHttp().SubmitFormAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(token),
                    ResetFields(token, newPassword, newPassword), ct: ctx.Ct);
                Expect.Equal(302, response.Status, $"Reset status ({PlatformHelpers.Alert(response)})");
                acct.Password = newPassword;
                await PlatformHelpers.SignedInAsync(ctx, acct.Client.Email, newPassword);
            }
            finally
            {
                await PlatformHelpers.UnlockUsersAsync(ctx, acct.Client);
            }
        }, needs: Needs.Session | Needs.Database)
        .Test("expired", "An expired reset link shows 'this link no longer works'", async ctx =>
        {
            var acct = await PlatformHelpers.AcctAsync(ctx);
            var token = await PlatformHelpers.InsertResetTokenAsync(ctx, acct.Client, acct.UserId, minutes: -1);
            var page = (await ctx.Anonymous.GetAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(token), ct: ctx.Ct)).ExpectOk();
            Expect.True(!PlatformHelpers.HasPasswordField(page.Body), "An expired link still shows the form.");
            var post = await ctx.NewHttp().SubmitFormAsync("/Account/ResetPassword?token=" + Uri.EscapeDataString(token),
                ResetFields(token, "Auto-Reset-2026z", "Auto-Reset-2026z"), ct: ctx.Ct);
            Expect.True(post.Status == 200 && !PlatformHelpers.HasPasswordField(post.Body), $"Posting an expired link answered {post.Status}.");
            await PlatformHelpers.SignedInAsync(ctx, acct.Client.Email, acct.Password);
        }, needs: Needs.Session | Needs.Database)
        .Build();

    private static Dictionary<string, string> ResetFields(string token, string password, string confirm) => new()
    {
        ["Token"] = token,
        ["Password"] = password,
        ["ConfirmPassword"] = confirm,
    };
}

