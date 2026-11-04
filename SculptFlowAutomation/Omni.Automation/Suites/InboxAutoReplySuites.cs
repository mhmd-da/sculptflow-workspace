using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Auto-reply settings and the replies they trigger on a fake Telegram bot (t3-inbox-03).</summary>
[Suite(47)]
public static class InboxAutoReplySuite
{
    private const string NewMessage = "Thanks for writing! We'll answer soon.";
    private const string ReopenedMessage = "Welcome back!";

    private static Dictionary<string, string> Form(string channel, bool newOn, string? newMessage, string reopenedMode, string? reopenedMessage) => new()
    {
        ["Replies[0].Channel"] = channel,
        ["Replies[0].NewConversationEnabled"] = newOn ? "true" : "false",
        ["Replies[0].NewConversationMessage"] = newMessage ?? "",
        ["Replies[0].ReopenedMode"] = reopenedMode,
        ["Replies[0].ReopenedMessage"] = reopenedMessage ?? "",
    };

    private static async Task<ApiResponse> SaveAsync(TestContext ctx, TestClient client, Dictionary<string, string> form) =>
        await client.Http.SubmitFormAsync("/settings/auto-respond", form, ct: ctx.Ct);

    /// <summary>The "tg-auto" client: a fake Telegram bot with a new-conversation and a custom reopened auto-reply.</summary>
    private static async Task<(TestClient Client, FakeChannel Channel)> AutoClientAsync(TestContext ctx)
    {
        var (client, channel) = await InboxHelpers.TelegramAsync(ctx, "tg-auto");
        await InboxHelpers.OncePerRunAsync(ctx, "auto-reply:tg-auto", async () =>
        {
            var saved = await SaveAsync(ctx, client, Form("telegram", true, NewMessage, "custom", ReopenedMessage));
            if (saved.Status != 302) throw new TestFailure($"Saving the auto-replies failed ({saved.Status}): {TestContext.PageError(saved.Body) ?? "no error shown"}.");
            return "saved";
        });
        return (client, channel);
    }

    private static async Task ExpectRefusedAsync(TestContext ctx, Dictionary<string, string> form, string what)
    {
        var client = await ctx.ClientAsync();
        var response = await SaveAsync(ctx, client, form);
        Expect.True(response.Status == 200 && TestContext.PageError(response.Body) is not null,
            $"{what} was not refused with a message (status {response.Status}).");
    }

    public static TestSuite Build() => new SuiteBuilder("inbox-auto-reply", "Inbox: auto-replies",
            "Settings → Auto-respond: saving and validation, and the automatic reply to a new or reopened Telegram conversation " +
            "(sent through a fake bot, so the attempt is checked in the trace).")
        .Test("save", "Auto-reply: a new-conversation and a custom reopened reply are saved", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var text = $"Hello from {ctx.Tag}";
            var saved = await SaveAsync(ctx, client, Form("telegram", true, text, "custom", "Back " + ctx.Tag));
            Expect.Equal(302, saved.Status, $"Save answer ({TestContext.PageError(saved.Body)})");
            var page = (await client.Http.GetAsync("/settings/auto-respond", ct: ctx.Ct)).ExpectOk();
            page.ExpectBodyContains(text);
            page.ExpectBodyContains("Back " + ctx.Tag);
        })
        .Test("needs-message", "Auto-reply: switching a reply on without a message is refused", async ctx =>
        {
            await ExpectRefusedAsync(ctx, Form("telegram", true, "", "off", null), "A new-conversation reply without a message");
        })
        .Test("custom-needs-message", "Auto-reply: a custom reopened reply without a message is refused", async ctx =>
        {
            await ExpectRefusedAsync(ctx, Form("telegram", false, null, "custom", ""), "A custom reopened reply without a message");
        })
        .Test("same-needs-new", "Auto-reply: \"same as new\" for reopened needs the new-conversation message", async ctx =>
        {
            await ExpectRefusedAsync(ctx, Form("telegram", false, "", "same", null), "\"Same\" without a new-conversation message");
        })
        .Test("too-long", "Auto-reply: a message over the length limit is refused", async ctx =>
        {
            await ExpectRefusedAsync(ctx, Form("telegram", true, new string('a', 5000), "off", null), "A 5,000-character reply");
        })
        .Test("unknown-channel", "Auto-reply: a channel Omni can't send through is refused", async ctx =>
        {
            await ExpectRefusedAsync(ctx, Form("pigeon", true, "hi", "off", null), "An auto-reply for channel \"pigeon\"");
        })
        .Test("first-message", "Auto-reply: a contact's first message triggers the new-conversation reply (failure traced)", async ctx =>
        {
            var (client, channel) = await AutoClientAsync(ctx);
            var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hello there");
            var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
            var steps = await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId,
                s => InboxHelpers.Count(s, "send.blocked", kind: "auto_reply") > 0, "the auto-reply attempt");
            Expect.Equal(1, InboxHelpers.Count(steps, "send.requested", kind: "auto_reply"), $"Auto-reply sends (trace: {InboxHelpers.Describe(steps)})");
        }, needs: Needs.Session | Needs.Database)
        .Test("second-message", "Auto-reply: a second message in an open conversation gets no new auto-reply", async ctx =>
        {
            var (client, channel) = await AutoClientAsync(ctx);
            var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hello");
            var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
            await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId, s => InboxHelpers.Count(s, "send.blocked", kind: "auto_reply") > 0, "the first auto-reply");
            await InboxHelpers.TelegramTextAsync(ctx, channel, "anyone?", chat);
            var steps = await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId, s => InboxHelpers.Count(s, "bot.skipped") >= 2, "the second message's trace");
            await Task.Delay(TimeSpan.FromSeconds(4), ctx.Ct);
            steps = await InboxHelpers.StepsAsync(ctx, client.ClientId, conversationId);
            Expect.Equal(1, InboxHelpers.Count(steps, "send.requested", kind: "auto_reply"), $"Auto-reply sends after two messages (trace: {InboxHelpers.Describe(steps)})");
        }, needs: Needs.Session | Needs.Database)
        .Test("reopened", "Auto-reply: a message that reopens a closed conversation triggers the reopened reply", async ctx =>
        {
            var (client, channel) = await AutoClientAsync(ctx);
            var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "hello");
            var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
            await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId, s => InboxHelpers.Count(s, "send.blocked", kind: "auto_reply") > 0, "the first auto-reply");
            (await client.Http.PostAsync($"/api/conversations/{conversationId}/close", ct: ctx.Ct)).ExpectOk();
            await InboxHelpers.TelegramTextAsync(ctx, channel, "I'm back", chat);
            var steps = await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId,
                s => InboxHelpers.Count(s, "send.requested", kind: "auto_reply") >= 2, "the reopened auto-reply");
            Expect.True(InboxHelpers.Count(steps, "conversation.reopened") >= 1, $"The conversation wasn't traced as reopened (trace: {InboxHelpers.Describe(steps)}).");
        }, needs: Needs.Session | Needs.Database)
        .Test("start-greeting", "Auto-reply: a new Telegram contact's /start gets the new-conversation reply", async ctx =>
        {
            var (client, channel) = await AutoClientAsync(ctx);
            var chat = await InboxHelpers.TelegramTextAsync(ctx, channel, "/start");
            var conversationId = InboxHelpers.G(await InboxHelpers.TelegramThreadAsync(ctx, client.ClientId, chat), "id");
            await InboxHelpers.WaitForStepsAsync(ctx, client.ClientId, conversationId, s => InboxHelpers.Count(s, "bot.skipped") > 0, "the bot decision");
            await Task.Delay(TimeSpan.FromSeconds(4), ctx.Ct);
            var steps = await InboxHelpers.StepsAsync(ctx, client.ClientId, conversationId);
            Expect.True(InboxHelpers.Count(steps, "send.requested", kind: "auto_reply") == 1,
                $"Every new Telegram contact starts with /start, which is answered as an opt-in instead of the new-conversation auto-reply (trace: {InboxHelpers.Describe(steps)}).");
        }, needs: Needs.Session | Needs.Database, finding: "t3-inbox-03")
        .Build();
}
