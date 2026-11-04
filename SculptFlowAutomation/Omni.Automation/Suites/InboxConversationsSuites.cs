using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Conversations API and the Inbox page over 50 conversations (t3-inbox-04, -19).</summary>
[Suite(41)]
public static class InboxConversationsSuite
{
    private const Needs Api = Needs.Session | Needs.IngestKey;

    public static TestSuite Build() => new SuiteBuilder("inbox-conversations", "Inbox: conversations",
            "Creating, listing and paging conversations, take-over / return to AI, close / reopen, read markers and isolation.")
        .Test("signed-out", "Conversations: signed-out callers get 401", async ctx =>
        {
            (await ctx.Anonymous.GetAsync("/api/conversations", ct: ctx.Ct)).ExpectStatus(401);
            (await ctx.Anonymous.PostAsync($"/api/conversations/{Guid.NewGuid()}/messages/send", new { content = "x" }, ct: ctx.Ct)).ExpectStatus(401);
        }, needs: Needs.None)
        .Test("create", "Conversations: a new conversation is created and listed first", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "create");
            var detail = (await client.Http.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("website", detail.Prop("conversation")["channel"], "Channel");
            Expect.EqualText("active", detail.Prop("conversation")["status"], "Status");
            Expect.Equal(0, detail.Array("messages").Count, "Messages in a new conversation");
            Expect.ContainsId((await client.Http.GetAsync("/api/conversations?take=5", ct: ctx.Ct)).ExpectOk().Array("items"), id, "Newest conversations");
        }, needs: Api)
        .Test("create-trial-mode", "Conversations: on a plan without the AI assistant a new conversation starts with the team", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "mode");
            Expect.EqualText("human", (await client.Http.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk().Prop("conversation")["mode"], "Mode");
        }, needs: Api)
        .Test("create-other-contact", "Conversations: a conversation for another client's contact is refused (404)", async ctx =>
        {
            var other = await ctx.OtherClientAsync();
            var foreignContact = await InboxHelpers.NewContactAsync(ctx, other, "foreign");
            var client = await ctx.ClientAsync();
            (await client.Http.PostAsync("/api/conversations", new { contactId = foreignContact, channel = "website" }, ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Api)
        .Test("create-bad-channel", "Conversations: an unknown channel is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await InboxHelpers.NewContactAsync(ctx, client, "badchannel");
            (await client.Http.PostAsync("/api/conversations", new { contactId, channel = "pigeon" }, ct: ctx.Ct)).ExpectStatus(400);
        }, needs: Api)
        .Test("create-unconnected-whatsapp", "Conversations: a WhatsApp conversation can't be opened when WhatsApp isn't connected", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var contactId = await InboxHelpers.NewContactAsync(ctx, client, "nowa");
            var response = await client.Http.PostAsync("/api/conversations", new { contactId, channel = "whatsapp" }, ct: ctx.Ct);
            Expect.True(!response.IsSuccess && response.Status < 500,
                $"Opening a WhatsApp conversation for a client with no WhatsApp connected answered {response.Status}: it leaves an orphan thread in the Inbox that can never send.{response.Snippet(200)}");
        }, needs: Api, finding: "t3-inbox-19")
        .Test("list-paging", "Conversations: the list pages with skip/take, clamps silly values and counts everything", async ctx =>
        {
            var client = await ctx.ClientAsync();
            for (var i = 0; i < 3; i++) await InboxHelpers.NewThreadAsync(ctx, client, $"list{i}");
            var first = (await client.Http.GetAsync("/api/conversations?take=2", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(2, first.Array("items").Count, "Items on a page of 2");
            var total = first.Prop("totalCount").GetValue<int>();
            Expect.True(total >= 3, $"totalCount should count every conversation (got {total}).");
            var second = (await client.Http.GetAsync("/api/conversations?skip=2&take=2", ct: ctx.Ct)).ExpectOk().Array("items");
            Expect.True(second.Count >= 1, "The second page is empty.");
            Expect.LacksId(second, Guid.Parse(first.Array("items")[0]!["id"]!.ToString()), "Second page");
            var clamped = (await client.Http.GetAsync("/api/conversations?take=100000&skip=-5", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(total, clamped.Array("items").Count, "Items with take=100000 (clamped to 200)");
            var bad = await client.Http.GetAsync("/api/conversations?take=abc", ct: ctx.Ct);
            Expect.True(bad.Status < 500, $"take=abc crashed the list ({bad.Status}).");
        }, needs: Api)
        .Test("inbox-over-50", "Inbox page: a conversation older than the 50 newest opens with its contact's name", async ctx =>
        {
            var client = await ctx.RegisterClientAsync("many");
            await ctx.Db.ExecuteForClientAsync(client.ClientId, """
                with c as (
                    insert into crm.contacts (client_id, full_name, source, stage_key, created_at, updated_at)
                    select @clientId, 'Auto Many ' || lpad(g::text, 2, '0') || ' ' || @tag, 'automation', 'new',
                           now() - make_interval(hours => g), now()
                    from generate_series(1, 55) g
                    returning id, created_at)
                insert into crm.conversations (client_id, contact_id, channel, external_thread_id, status, ai_enabled, human_takeover, mode,
                    last_message_at, last_message_direction, created_at, updated_at)
                select @clientId, c.id, 'website', 'auto-many-' || c.id, 'active', false, true, 'human', c.created_at, 'inbound', c.created_at, now()
                from c
                """, new { tag = ctx.Tag }, ctx.Ct);
            var list = (await client.Http.GetAsync("/api/conversations?skip=50&take=50", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(55, list.Prop("totalCount").GetValue<int>(), "Conversations created for the check");
            var oldest = list.Array("items").Last()!;
            var name = oldest["contactFullName"]!.ToString();

            // The server searches and counts every conversation, not the first page.
            var found = (await client.Http.GetAsync($"/api/conversations?search={Uri.EscapeDataString(name)}", ct: ctx.Ct)).ExpectOk();
            Expect.True(found.Array("items").Any(i => i?["id"]?.ToString() == oldest["id"]!.ToString()),
                $"Searching \"{name}\" didn't find the 55th conversation.");
            Expect.True(list.Json?["counts"]?["all"]?.GetValue<int>() >= 55, $"The tab counts don't cover every conversation: {list.Json?["counts"]?.ToJsonString()}.");

            // Signs in by hand: Browser.OpenAsync clicks the first submit button on the sign-in page, which is the language menu's.
            await using var page = await ctx.Browser.OpenAnonymousAsync();
            await page.Page.GotoAsync(ctx.Browser.Url("/Account/Login"), new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });
            await page.Page.FillAsync("form.au-auth__form input[name=Email]", client.Email);
            await page.Page.FillAsync("form.au-auth__form input[name=Password]", client.Password);
            await Task.WhenAll(
                page.Page.WaitForURLAsync(u => !u.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase),
                    new() { Timeout = 60000, WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded }),
                page.Page.ClickAsync("form.au-auth__form button[type=submit]"));
            await page.Page.GotoAsync(ctx.Browser.Url($"/inbox?conversationId={oldest["id"]}"),
                new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            // The header first says "Unknown" while it loads the contact of a conversation outside the first page.
            try
            {
                await page.Page.WaitForFunctionAsync("name => (document.getElementById('inbox-thread-name')?.textContent || '').trim() === name", name, new() { Timeout = 30000 });
            }
            catch (TimeoutException) { /* checked below with what is shown */ }
            var shown = (await page.Page.TextContentAsync("#inbox-thread-name"))?.Trim();
            await ctx.Browser.ScreenshotAsync(page, "inbox-oldest-conversation");
            Expect.True(shown == name, $"The 55th conversation opened with the name \"{shown}\" instead of \"{name}\": the Inbox only loads the 50 newest conversations.");
        }, needs: Needs.Session | Needs.Database | Needs.Browser, finding: "t3-inbox-04")
        .Test("take-over", "Conversations: take-over puts the conversation with the team", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "takeover");
            var conversation = (await client.Http.PostAsync($"/api/conversations/{id}/take-over", ct: ctx.Ct)).ExpectOk();
            Expect.EqualText("human", conversation.Prop("mode"), "Mode after take-over");
        }, needs: Api)
        .Test("return-to-ai-no-plan", "Conversations: return to AI is refused (422) on a plan without the AI assistant", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "returnai");
            (await client.Http.PostAsync($"/api/conversations/{id}/return-to-ai", ct: ctx.Ct)).ExpectStatus(422);
            Expect.EqualText("human", (await client.Http.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk().Prop("conversation")["mode"], "Mode after the refusal");
        }, needs: Api)
        .Test("close-reopen", "Conversations: close and reopen work and are safe to repeat", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "close");
            for (var i = 0; i < 2; i++)
            {
                Expect.EqualText("closed", (await client.Http.PostAsync($"/api/conversations/{id}/close", ct: ctx.Ct)).ExpectOk().Prop("status"), $"Status after close #{i + 1}");
            }
            for (var i = 0; i < 2; i++)
            {
                Expect.EqualText("active", (await client.Http.PostAsync($"/api/conversations/{id}/reopen", ct: ctx.Ct)).ExpectOk().Prop("status"), $"Status after reopen #{i + 1}");
            }
        }, needs: Api)
        .Test("read", "Conversations: marking read answers 204; an unknown id 404", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "read");
            (await client.Http.PostAsync($"/api/conversations/{id}/read", ct: ctx.Ct)).ExpectStatus(204);
            (await client.Http.PostAsync($"/api/conversations/{Guid.NewGuid()}/read", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Api)
        .Test("isolation", "Conversations: another client's conversation id gives 404 for every action", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "xclient");
            var other = (await ctx.OtherClientAsync()).Http;
            (await other.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/conversations/{id}/messages/send", new { content = "x" }, ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/conversations/{id}/read", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/conversations/{id}/take-over", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/conversations/{id}/return-to-ai", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/conversations/{id}/close", ct: ctx.Ct)).ExpectStatus(404);
            (await other.PostAsync($"/api/conversations/{id}/reopen", ct: ctx.Ct)).ExpectStatus(404);
            Expect.LacksId((await other.GetAsync("/api/conversations?take=200", ct: ctx.Ct)).ExpectOk().Array("items"), id, "The other client's list");
            Expect.EqualText("active", (await client.Http.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk().Prop("conversation")["status"], "Status for the owner");
        }, needs: Api)
        .Test("isolation-messages", "Conversations: another client's conversation messages give 404 (like every other action)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "xmsgs");
            var other = (await ctx.OtherClientAsync()).Http;
            (await other.GetAsync($"/api/conversations/{id}/messages", ct: ctx.Ct)).ExpectStatus(404);
        }, needs: Api, finding: "t3-inbox-19")
        .Build();
}

/// <summary>Member sends refused before anything goes out, and the legacy add-message endpoint (t3-inbox-05).</summary>
[Suite(42)]
public static class InboxMessagesSuite
{
    private const Needs Api = Needs.Session | Needs.IngestKey;

    public static TestSuite Build() => new SuiteBuilder("inbox-messages", "Inbox: sending and the legacy add-message endpoint",
            "Member sends that must be refused before anything is sent, and the legacy POST /api/conversations/{id}/messages.")
        .Test("send-empty", "Send: an empty or missing message is refused (400)", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "empty");
            (await client.Http.PostAsync($"/api/conversations/{id}/messages/send", new { content = "   " }, ct: ctx.Ct)).ExpectStatus(400);
            (await client.Http.PostAsync($"/api/conversations/{id}/messages/send", new { }, ct: ctx.Ct)).ExpectStatus(400);
        }, needs: Api)
        .Test("send-no-channel", "Send: a conversation on a channel Omni can't send through is refused (422) and nothing is saved", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "nochannel");
            (await client.Http.PostAsync($"/api/conversations/{id}/messages/send", new { content = "hello" }, ct: ctx.Ct)).ExpectStatus(422);
            Expect.Equal(0, (await client.Http.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk().Array("messages").Count, "Messages after the refused send");
        }, needs: Api)
        .Test("send-template-non-whatsapp", "Send: a WhatsApp template can't be sent in a non-WhatsApp conversation", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "tplweb");
            (await client.Http.PostAsync($"/api/conversations/{id}/messages/send-template", new { whatsAppTemplateId = Guid.NewGuid(), bodyParameters = Array.Empty<string>() }, ct: ctx.Ct))
                .ExpectClientError("Sending a WhatsApp template in a website conversation");
        }, needs: Api)
        .Test("forge-customer", "Add-message: a member can't record a fake customer message", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "forge");
            var response = await client.Http.PostAsync($"/api/conversations/{id}/messages", new
            {
                direction = "inbound", senderType = "contact", channel = "website", content = "FORGED customer message",
            }, ct: ctx.Ct);
            Expect.True(!response.IsSuccess, $"The legacy add-message endpoint saved a customer message written by a member ({response.Status}).");
            Expect.Equal(0, (await client.Http.GetAsync($"/api/conversations/{id}", ct: ctx.Ct)).ExpectOk().Array("messages").Count, "Messages after the forged add");
        }, needs: Api, finding: "t3-inbox-05")
        .Test("forge-ai", "Add-message: a member can't record a fake AI message on another channel", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "forgeai");
            var response = await client.Http.PostAsync($"/api/conversations/{id}/messages", new
            {
                direction = "outbound", senderType = "ai", channel = "whatsapp", content = "forged AI", isAiGenerated = true, origin = "ai",
            }, ct: ctx.Ct);
            Expect.True(!response.IsSuccess, $"The legacy add-message endpoint saved an 'AI' WhatsApp message inside a website conversation ({response.Status}).");
        }, needs: Api, finding: "t3-inbox-05")
        .Test("forge-long-type", "Add-message: an over-long message type or origin never answers 500", async ctx =>
        {
            var client = await ctx.ClientAsync();
            var (_, id) = await InboxHelpers.NewThreadAsync(ctx, client, "forgelong");
            var type = await client.Http.PostAsync($"/api/conversations/{id}/messages", new { direction = "outbound", senderType = "member", channel = "website", content = "x", messageType = new string('m', 40) }, ct: ctx.Ct);
            var origin = await client.Http.PostAsync($"/api/conversations/{id}/messages", new { direction = "outbound", senderType = "member", channel = "website", content = "x", origin = new string('o', 60) }, ct: ctx.Ct);
            Expect.True(type.Status < 500 && origin.Status < 500, $"Over-long values crashed the endpoint: messageType -> {type.Status}, origin -> {origin.Status}.");
        }, needs: Api, finding: "t3-inbox-05")
        .Build();
}

