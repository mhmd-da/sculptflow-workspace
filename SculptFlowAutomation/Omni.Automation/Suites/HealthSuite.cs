using Omni.Automation.Core;

namespace Omni.Automation.Suites;

[Suite(0)]
public static class HealthSuite
{
    public static TestSuite Build() => new SuiteBuilder("health", "Health", "The target is up and the test client can be registered.")
        .Test("sign-in-page", "Sign-in page loads", async ctx =>
        {
            var page = (await ctx.Anonymous.GetAsync("/Account/Login", ct: ctx.Ct)).ExpectOk();
            Expect.True(page.Body.Contains("__RequestVerificationToken"), "The sign-in page has no form token.");
        }, needs: Needs.None)
        .Test("register", "A new client can sign up and gets a client id", async ctx =>
        {
            var client = await ctx.ClientAsync();
            Expect.True(client.ClientId != Guid.Empty, "No client id after sign-up.");
            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();
        })
        .Test("cleanup-endpoint", "The automation cleanup endpoint is deployed", async ctx =>
        {
            (await ctx.Admin.GetAsync("/api/platform-admin/automation/clients", ct: ctx.Ct)).ExpectOk().Array();
        }, needs: Needs.AdminKey)
        .Test("database", "The optional database connection works", async ctx =>
        {
            var count = await ctx.Db.ScalarAsync<long>("select count(*) from core.business_types");
            Expect.True(count > 0, "No business types in the database.");
        }, needs: Needs.Database)
        .Build();
}
