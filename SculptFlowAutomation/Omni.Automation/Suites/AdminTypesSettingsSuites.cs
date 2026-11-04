using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Business types: list, read, create validation, the automation type's lifecycle and its terminology.</summary>
[Suite(85)]
public static class AdminBusinessTypesSuite
{
    public static TestSuite Build() => new SuiteBuilder("admin-btypes", "Admin: business types",
            "Business types and their stages and words. Writes only on the automation type (auto_automation_type), which is inactive between tests.")
        .Test("list-get", "Business types list and details include general and clinic with stages and terms", async ctx =>
        {
            var list = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/business-types", ct: ctx.Ct)).ExpectOk().Array();
            foreach (var code in new[] { "general", "clinic" })
            {
                Expect.True(list.Any(t => t?["code"]?.ToString() == code), $"Business type {code} isn't listed.");
            }
            var general = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/business-types/general", ct: ctx.Ct)).ExpectOk();
            Expect.True(general.Array("stages").Count >= 7, "The general type has fewer than 7 stages.");
            Expect.EqualText("contacts", general.Prop("terminology")["en"]?["contact"]?["other"], "General type's English word for contacts");
            Expect.Equal(404, (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/business-types/nope_type", ct: ctx.Ct)).Status, "Unknown type");
        }, needs: Needs.AdminKey)
        .Test("create-validation", "Creating a business type validates code, names, terms and stages (400; taken code 409)", async ctx =>
        {
            JsonObject Body(Action<JsonObject> change)
            {
                var body = System.Text.Json.JsonSerializer.SerializeToNode(AdminHelpers.BusinessTypeBody(ctx, $"auto_bad_{ctx.Tag.ToLowerInvariant()}"), OmniClient.JsonOptions)!.AsObject();
                change(body);
                return body;
            }
            var cases = new (string What, JsonObject Body, int Status)[]
            {
                ("code with upper case and spaces", Body(b => b["code"] = "Bad Code"), 400),
                ("no English name", Body(b => b["name"] = new JsonObject { ["fr"] = "Nom" }), 400),
                ("an unknown language", Body(b => b["name"]!["xx"] = "?"), 400),
                ("terms missing", Body(b => b["terminology"]!["en"]!.AsObject().Remove("order")), 400),
                ("an unknown term", Body(b => b["terminology"]!["en"]!["widget"] = new JsonObject { ["one"] = "w", ["other"] = "ws" }), 400),
                ("no stages", Body(b => b["stages"] = new JsonArray()), 400),
                ("a duplicate stage key", Body(b => b["stages"]!.AsArray().Add(new JsonObject { ["key"] = "new", ["category"] = "open", ["labels"] = new JsonObject { ["en"] = "Again" } })), 400),
                ("a bad stage category", Body(b => b["stages"]![0]!["category"] = "bogus"), 400),
                ("a category without a stage", Body(b => b["stages"]!.AsArray().RemoveAt(6)), 400),
                ("a stage without an English label", Body(b => b["stages"]![0]!["labels"] = new JsonObject { ["fr"] = "Nouveau" }), 400),
                ("an AI profile over the limit", Body(b => b["aiProfile"] = new string('a', 20001)), 400),
                ("the taken code general", Body(b => b["code"] = "general"), 409),
            };
            var wrong = new List<string>();
            foreach (var (what, body, status) in cases)
            {
                var r = await ctx.Admin.PostAsync($"{AdminHelpers.Api}/business-types", body, ct: ctx.Ct);
                if (r.Status != status) wrong.Add($"{what} -> {r.Status} (expected {status})");
                if (r.Status == 200)
                {
                    // Created by mistake: switch it off so it never shows on the sign-up page.
                    await ctx.Admin.PutAsync($"{AdminHelpers.Api}/business-types/{body["code"]}/active", new { active = false }, CancellationToken.None);
                }
            }
            Expect.True(wrong.Count == 0, $"Business type validation: {string.Join("; ", wrong)}.");
        }, needs: Needs.AdminKey)
        .Test("lifecycle", "The automation type is saved, edited, offered at sign-up while active, and hidden once inactive", async ctx =>
        {
            try
            {
                var type = await AdminHelpers.EnsureTypeAsync(ctx);
                Expect.True(type["isActive"]?.GetValue<bool>() == true, "The type isn't active after activation.");
                Expect.EqualText("guests", type["terminology"]?["en"]?["contact"]?["other"], "Saved English word for contacts");
                Expect.Equal(7, type["stages"]!.AsArray().Count, "Saved stage count");

                var edit = System.Text.Json.JsonSerializer.SerializeToNode(AdminHelpers.BusinessTypeBody(ctx, "ignored_code", "visitor", "visitors"), OmniClient.JsonOptions)!.AsObject();
                var edited = (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/business-types/{AdminHelpers.TypeCode}", edit, ctx.Ct)).ExpectOk();
                Expect.EqualText(AdminHelpers.TypeCode, edited.Prop("code"), "Code after an edit that sent another code");
                Expect.EqualText("visitors", edited.Prop("terminology")["en"]?["contact"]?["other"], "Edited word for contacts");

                var register = (await ctx.Anonymous.GetAsync("/Account/Register", ct: ctx.Ct)).ExpectOk();
                Expect.True(register.Body.Contains(AdminHelpers.TypeCode), "The sign-up page doesn't offer the active automation type.");

                var off = (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/business-types/{AdminHelpers.TypeCode}/active", new { active = false }, ctx.Ct)).ExpectOk();
                Expect.True(off.Prop("isActive").GetValue<bool>() == false, "The type is still active after deactivation.");
                register = (await ctx.Anonymous.GetAsync("/Account/Register", ct: ctx.Ct)).ExpectOk();
                Expect.True(!register.Body.Contains(AdminHelpers.TypeCode), "The sign-up page still offers the inactive automation type.");
            }
            finally { await AdminHelpers.DeactivateTypeAsync(ctx); }
        }, needs: Needs.AdminKey)
        .Test("inactive-move", "A client can't be moved to an inactive type (422)", async ctx =>
        {
            var client = await ctx.NamedClientAsync("adm-btype2");
            try
            {
                await AdminHelpers.EnsureTypeAsync(ctx);
                await AdminHelpers.DeactivateTypeAsync(ctx);
                var r = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type", new { businessTypeCode = AdminHelpers.TypeCode }, ctx.Ct);
                Expect.Equal(422, r.Status, "Moving a client to an inactive type");
                var preview = await ctx.Admin.GetAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type/preview?to={AdminHelpers.TypeCode}", ct: ctx.Ct);
                Expect.Equal(422, preview.Status, "Previewing a move to an inactive type");
            }
            finally { await AdminHelpers.DeactivateTypeAsync(ctx); }
        }, needs: Needs.Session | Needs.AdminKey)
        .Test("terminology", "A client of the automation type sees its own words in the dashboard", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("adm-terms");
            try
            {
                await AdminHelpers.EnsureTypeAsync(ctx, "guest", "guests");
                (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type", new { businessTypeCode = AdminHelpers.TypeCode }, ctx.Ct)).ExpectOk();
                await ctx.Fixtures.EvictCachePrefixAsync("core:client:" + AdminHelpers.N(client.ClientId));
                var page = (await client.Http.GetAsync("/dashboard/contacts", ct: ctx.Ct)).ExpectOk();
                Expect.True(page.Body.Contains("Guests", StringComparison.Ordinal) || page.Body.Contains("guests", StringComparison.Ordinal),
                    "The contacts page of a client whose business type calls contacts \"guests\" doesn't use that word.");
            }
            finally
            {
                await ctx.Admin.PutAsync($"{AdminHelpers.Api}/clients/{client.ClientId}/business-type", new { businessTypeCode = "general" }, CancellationToken.None);
                await AdminHelpers.DeactivateTypeAsync(ctx);
            }
        }, needs: Needs.Session | Needs.AdminKey)
        .Build();
}

/// <summary>Configuration settings: list (secrets masked), validation of every value type, and one restored round trip.</summary>
[Suite(86)]
public static class AdminSettingsSuite
{
    private const string Section = "Billing";
    private const string Key = "RecentTransactions";

    public static TestSuite Build() => new SuiteBuilder("admin-settings", "Admin: settings",
            "The 113 runtime settings. Only validation is exercised, except one Risk.Shared round trip that restores the value.")
        .Test("list-masked", "Settings list masks every secret", async ctx =>
        {
            var list = (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/settings", ct: ctx.Ct)).ExpectOk().Array();
            Expect.True(list.Count > 50, $"Only {list.Count} settings listed.");
            var leaks = list.Where(s => s?["isSecret"]?.GetValue<bool>() == true)
                .Where(s => new[] { "storedValue", "effectiveValue", "defaultValue" }
                    .Any(p => s![p]?.ToString() is { Length: > 0 } v && v != "(set)"))
                .Select(s => $"{s!["section"]}:{s["key"]}").ToList();
            Expect.True(leaks.Count == 0, $"Secret settings show a value: {string.Join(", ", leaks)}.");
        }, needs: Needs.AdminKey)
        .Test("validation", "Invalid values, unknown settings, bad booleans and choices are refused and change nothing", async ctx =>
        {
            async Task<string> Snapshot() => string.Join("|", (await ctx.Admin.GetAsync($"{AdminHelpers.Api}/settings", ct: ctx.Ct)).ExpectOk().Array()
                .Where(s => (s?["section"]?.ToString(), s?["key"]?.ToString()) is (Section, Key) or ("Accounts", "SignupEnabled") or ("Ai", "BotEngine"))
                .Select(s => $"{s!["section"]}:{s["key"]}={s["storedValue"]}@{s["updatedAt"]}"));
            var before = await Snapshot();
            var cases = new (string Path, string? Value)[]
            {
                ($"{Section}/{Key}", "0"), ($"{Section}/{Key}", "101"), ($"{Section}/{Key}", "abc"), ($"{Section}/{Key}", ""),
                ($"{Section}/{Key}", "15.5"), ($"{Section}/{Key}", null), ("Nope/Nope", "1"), ("Accounts/SignupEnabled", "maybe"), ("Ai/BotEngine", "bogus"),
            };
            var accepted = new List<string>();
            try
            {
                foreach (var (path, value) in cases)
                {
                    var r = await ctx.Admin.PutAsync($"{AdminHelpers.Api}/settings/{path}", new { value, note = $"Omni.Automation {ctx.Tag}" }, ctx.Ct);
                    if (r.Status != 400) accepted.Add($"{path} = \"{value}\" -> {r.Status}");
                }
                Expect.True(accepted.Count == 0, $"Invalid settings weren't refused: {string.Join("; ", accepted)}.");
                Expect.Equal(before, await Snapshot(), "Settings after refused changes");
            }
            finally
            {
                if (accepted.Count > 0) ctx.Log("Some invalid values were accepted: restore Billing:RecentTransactions, Accounts:SignupEnabled and Ai:BotEngine by hand. Before: " + before);
            }
        }, needs: Needs.AdminKey)
        .Test("round-trip", "A valid value (with spaces) is trimmed, applied and then restored", async ctx =>
        {
            JsonNode Row(JsonArray list) => list.First(s => s?["section"]?.ToString() == Section && s?["key"]?.ToString() == Key)!;
            var original = Row((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/settings", ct: ctx.Ct)).ExpectOk().Array());
            var stored = original["storedValue"]?.ToString();
            var effective = int.Parse(original["effectiveValue"]!.ToString());
            var next = effective == 100 ? 99 : effective + 1;
            try
            {
                var saved = (await ctx.Admin.PutAsync($"{AdminHelpers.Api}/settings/{Section}/{Key}", new { value = $" {next} ", note = $"Omni.Automation {ctx.Tag}, restored at once" }, ctx.Ct)).ExpectOk();
                Expect.EqualText(next.ToString(), saved.Prop("effectiveValue"), "Effective value after saving it with spaces");
            }
            finally
            {
                var restore = stored is null
                    ? await ctx.Admin.DeleteAsync($"{AdminHelpers.Api}/settings/{Section}/{Key}", CancellationToken.None)
                    : await ctx.Admin.PutAsync($"{AdminHelpers.Api}/settings/{Section}/{Key}", new { value = stored, note = original["note"]?.ToString() }, CancellationToken.None);
                if (!restore.IsSuccess) throw new TestFailure($"Couldn't restore {Section}:{Key} to \"{stored ?? "(default)"}\" (status {restore.Status}). Restore it by hand.");
            }
            var after = Row((await ctx.Admin.GetAsync($"{AdminHelpers.Api}/settings", ct: ctx.Ct)).ExpectOk().Array());
            Expect.EqualText(effective.ToString(), after["effectiveValue"], "Effective value after restoring");
        }, needs: Needs.AdminKey, risk: Risk.Shared)
        .Build();
}
