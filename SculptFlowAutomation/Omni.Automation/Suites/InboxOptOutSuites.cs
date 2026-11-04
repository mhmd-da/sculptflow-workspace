using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Stop/start words over a fake Telegram bot and the member toggle (t3-inbox-03).</summary>
[Suite(46)]
public static class InboxOptOutSuite
{
    private const Needs Tg = Needs.Session | Needs.Database;

    /// <summary>Sends <paramref name="texts"/> one by one from a new chat on the "tg" client and returns the contact after the last.</summary>
    private static async Task<(TestClient Client, Guid ContactId, Guid ConversationId)> ChatAsync(TestContext ctx, params string[] texts)
    {
        var (client, channel) = await InboxHelpers.TelegramAsync(ctx);
        var chat = ctx.Webhooks.NewChatId();
        foreach (var text in texts) await InboxHelpers.TelegramTextAsync(ctx, channel, text, chat);
        var thread = await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat);
        return (client, InboxHelpers.G(thread, "contact_id"), InboxHelpers.G(thread, "id"));
    }

    private static async Task ExpectOptedOutAsync(TestContext ctx, TestClient client, Guid contactId, bool expected, string after)
    {
        var contact = await InboxHelpers.ContactAsync(ctx, client, contactId);
        Expect.True(InboxHelpers.IsOptedOut(contact) == expected,
            $"After {after} the contact should be {(expected ? "opted out" : "subscribed")} (optedOutAt {contact["optedOutAt"]?.ToJsonString() ?? "null"}, marketingOptIn {contact["marketingOptIn"]}).");
    }

    /// <summary>Waits until every message's bot decision is traced, then a little longer so the steps written after it land too.</summary>
    private static async Task<List<Dictionary<string, object?>>> SettledStepsAsync(TestContext ctx, Guid clientId, Guid conversationId, int messages)
    {
        await InboxHelpers.WaitForStepsAsync(ctx, clientId, conversationId, s => InboxHelpers.Count(s, "bot.skipped") >= messages, $"the bot decision for {messages} message(s)");
        // What follows the decision (a confirmation's send attempt to Telegram) lands seconds later, slower under load:
        // wait until the trace stops growing, up to 30 seconds.
        var steps = await InboxHelpers.StepsAsync(ctx, clientId, conversationId);
        for (var waited = 0; waited < 30; waited += 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ctx.Ct);
            var next = await InboxHelpers.StepsAsync(ctx, clientId, conversationId);
            if (next.Count == steps.Count) return next;
            steps = next;
        }
        return steps;
    }

    public static TestSuite Build() => new SuiteBuilder("inbox-opt-out", "Inbox: stop and start words",
            "A customer message that is only a stop/start word changes the contact's opt-out and gets one confirmation " +
            "(over a fake Telegram bot, so the confirmation fails and is traced). Members can toggle it on the contact page.")
        .Test("stop", "Opt-out: STOP opts the contact out", async ctx =>
        {
            var (client, contactId, _) = await ChatAsync(ctx, "hello", "STOP");
            await ExpectOptedOutAsync(ctx, client, contactId, true, "STOP");
        }, needs: Tg)
        .Test("start", "Opt-out: start after STOP opts the contact back in", async ctx =>
        {
            var (client, contactId, _) = await ChatAsync(ctx, "STOP", "start");
            await ExpectOptedOutAsync(ctx, client, contactId, false, "STOP then start");
        }, needs: Tg)
        .Test("arabic", "Opt-out: the Arabic stop word إيقاف opts the contact out", async ctx =>
        {
            var (client, contactId, _) = await ChatAsync(ctx, "إيقاف");
            await ExpectOptedOutAsync(ctx, client, contactId, true, "إيقاف");
        }, needs: Tg)
        .Test("punctuation", "Opt-out: \"stop.\" with punctuation still counts", async ctx =>
        {
            var (client, contactId, _) = await ChatAsync(ctx, "stop.");
            await ExpectOptedOutAsync(ctx, client, contactId, true, "\"stop.\"");
        }, needs: Tg)
        .Test("command", "Opt-out: the Telegram command /stop opts the contact out", async ctx =>
        {
            var (client, contactId, _) = await ChatAsync(ctx, "/stop");
            await ExpectOptedOutAsync(ctx, client, contactId, true, "/stop");
        }, needs: Tg)
        .Test("sentence", "Opt-out: a sentence containing \"stop\" changes nothing", async ctx =>
        {
            var (client, contactId, _) = await ChatAsync(ctx, "please stop calling me");
            await ExpectOptedOutAsync(ctx, client, contactId, false, "\"please stop calling me\"");
        }, needs: Tg)
        .Test("not-for-ai", "Opt-out: a stop word is never passed to the AI (traced as opt_out_keyword)", async ctx =>
        {
            var (client, _, conversationId) = await ChatAsync(ctx, "STOP");
            var steps = await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId, s => InboxHelpers.Count(s, "bot.skipped") > 0, "the bot decision");
            Expect.True(InboxHelpers.Count(steps, "bot.skipped", reason: "opt_out_keyword") == 1, $"Trace: {InboxHelpers.Describe(steps)}.");
        }, needs: Tg)
        .Test("confirmation", "Opt-out: STOP gets one confirmation attempt; its failure is traced and the message is kept", async ctx =>
        {
            var (client, _, conversationId) = await ChatAsync(ctx, "STOP");
            var steps = await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId,
                s => InboxHelpers.Count(s, "send.blocked", kind: "opt_out_confirmation") > 0, "the traced opt-out confirmation");
            Expect.Equal(1, InboxHelpers.Count(steps, "send.blocked", kind: "opt_out_confirmation"), $"Confirmation attempts (trace: {InboxHelpers.Describe(steps)})");
            var messages = await InboxHelpers.MessagesAsync(ctx, conversationId);
            Expect.True(messages.Any(m => InboxHelpers.S(m, "content") == "STOP" && InboxHelpers.S(m, "direction") == "inbound"), "The STOP message itself wasn't kept.");
        }, needs: Tg)
        .Test("start-not-opted-out", "Opt-out: \"start\" from a contact who never opted out gets no confirmation", async ctx =>
        {
            var (client, _, conversationId) = await ChatAsync(ctx, "hello", "start");
            var steps = await SettledStepsAsync(ctx, client.ClientId, conversationId, 2);
            var attempts = InboxHelpers.Count(steps, "send.blocked", kind: "opt_out_confirmation") + InboxHelpers.Count(steps, "send.requested", kind: "system");
            Expect.True(attempts == 0, $"A contact who never opted out was sent a \"subscribed again\" confirmation for \"start\" (trace: {InboxHelpers.Describe(steps)}).");
        }, needs: Tg, finding: "t3-inbox-03")
        .Test("stop-twice", "Opt-out: a second STOP from an already opted-out contact gets no second confirmation", async ctx =>
        {
            var (client, _, conversationId) = await ChatAsync(ctx, "STOP", "STOP");
            var steps = await SettledStepsAsync(ctx, client.ClientId, conversationId, 2);
            var attempts = InboxHelpers.Count(steps, "send.blocked", kind: "opt_out_confirmation");
            Expect.True(attempts == 1, $"{attempts} confirmations for two STOPs; only the one that changed the state should be confirmed (trace: {InboxHelpers.Describe(steps)}).");
        }, needs: Tg, finding: "t3-inbox-03")
        .Test("start-command-new", "Opt-out: /start from a brand-new contact isn't treated as a re-subscription", async ctx =>
        {
            var (client, _, conversationId) = await ChatAsync(ctx, "/start");
            var steps = await SettledStepsAsync(ctx, client.ClientId, conversationId, 1);
            Expect.True(InboxHelpers.Count(steps, "bot.skipped", reason: "opt_out_keyword") == 0 && InboxHelpers.Count(steps, "send.blocked", kind: "opt_out_confirmation") == 0,
                $"Telegram's automatic /start was taken as the START keyword: the new contact gets \"You're subscribed again\" and no greeting (trace: {InboxHelpers.Describe(steps)}).");
        }, needs: Tg, finding: "t3-inbox-03")
        .Test("member-toggle", "Opt-out: a member can opt a contact out and back in from the contact page", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await InboxHelpers.NewContactAsync(ctx, client, "toggle");
            var path = $"/dashboard/contacts/{contactId}";
            var off = await client.Http.SubmitFormAsync(path, new Dictionary<string, string> { ["optedOut"] = "true" }, handler: "OptOut", ct: ctx.Ct);
            Expect.Equal(302, off.Status, "Opt-out form answer");
            await ExpectOptedOutAsync(ctx, client, contactId, true, "the member's opt-out");
            var on = await client.Http.SubmitFormAsync(path, new Dictionary<string, string> { ["optedOut"] = "false" }, handler: "OptOut", ct: ctx.Ct);
            Expect.Equal(302, on.Status, "Opt-in form answer");
            await ExpectOptedOutAsync(ctx, client, contactId, false, "the member's opt-in");
        }, needs: Needs.Session | Needs.IngestKey)
        .Build();
}

