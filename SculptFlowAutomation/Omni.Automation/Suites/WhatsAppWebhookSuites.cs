using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Signed Meta deliveries: handshake, signature gate, new contacts, duplicates, batches (t10-03, -05, -10, -12, t8-05, t8-08).</summary>
[Suite(51)]
public static class WhatsAppWebhookSuite
{
    private const Needs Signed = Needs.Session | Needs.Database | Needs.MetaSecret;

    private static Task<(TestClient Client, FakeChannel Channel)> HookAsync(TestContext ctx) => WhatsAppHelpers.WhatsAppAsync(ctx, "wa-hook");

    public static TestSuite Build()
    {
        var suite = new SuiteBuilder("wa-webhook", "WhatsApp: signed Meta webhook",
                "Deliveries signed with the Meta app secret to fake WhatsApp numbers: the handshake and signature gate, new contacts, " +
                "duplicates and batches, message types, stop words, echoes and statuses (t10).")
            .Test("verify", "WhatsApp webhook: the GET handshake echoes the challenge for the right verify token", async ctx =>
            {
                var token = ctx.Env.MetaWebhookVerifyToken ?? throw new TestSkipped("No Meta webhook verify token configured for this environment.");
                var response = (await ctx.Anonymous.GetAsync($"{Webhooks.WhatsAppPath}?hub.mode=subscribe&hub.verify_token={Uri.EscapeDataString(token)}&hub.challenge=auto-{ctx.Tag}", ct: ctx.Ct)).ExpectOk();
                Expect.Equal($"auto-{ctx.Tag}", response.Body, "Challenge echoed");
            }, needs: Needs.None)
            .Test("verify-refused", "WhatsApp webhook: a wrong token, another mode, or no challenge is refused (403)", async ctx =>
            {
                var token = Uri.EscapeDataString(ctx.Env.MetaWebhookVerifyToken ?? "unset");
                foreach (var query in new[] { "", "?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=x", $"?hub.mode=unsubscribe&hub.verify_token={token}&hub.challenge=x",
                             $"?hub.mode=subscribe&hub.verify_token={token}&hub.challenge=", "?hub.mode=subscribe&hub.verify_token=&hub.challenge=x" })
                {
                    (await ctx.Anonymous.GetAsync(Webhooks.WhatsAppPath + query, ct: ctx.Ct)).ExpectStatus(403);
                }
            }, needs: Needs.None)
            .Test("unsigned", "WhatsApp webhook: an unsigned or wrongly signed delivery is refused (401)", async ctx =>
            {
                var body = """{"object":"whatsapp_business_account","entry":[]}""";
                (await ctx.Anonymous.PostRawAsync(Webhooks.WhatsAppPath, body, "application/json", ct: ctx.Ct)).ExpectStatus(401);
                (await ctx.Anonymous.PostRawAsync(Webhooks.WhatsAppPath, body, "application/json",
                    new Dictionary<string, string> { ["X-Hub-Signature-256"] = Webhooks.Sign(body, "not-the-secret") }, ctx.Ct)).ExpectStatus(401);
                (await ctx.Anonymous.PostRawAsync(Webhooks.WhatsAppPath, body, "application/json",
                    new Dictionary<string, string> { ["X-Hub-Signature-256"] = "deadbeef" }, ctx.Ct)).ExpectStatus(401);
            }, needs: Needs.None)
            .Test("signed-other-bytes", "WhatsApp webhook: a valid signature over different bytes is refused (401)", async ctx =>
            {
                if (!ctx.Env.HasMetaSecret) throw new TestSkipped("No Meta app secret configured for this environment.");
                var signed = """{"object":"whatsapp_business_account","entry":[]}""";
                var sent = """{"object":"whatsapp_business_account", "entry":[]}""";
                (await ctx.Anonymous.PostRawAsync(Webhooks.WhatsAppPath, sent, "application/json",
                    new Dictionary<string, string> { ["X-Hub-Signature-256"] = Webhooks.Sign(signed, ctx.Env.MetaAppSecret!) }, ctx.Ct)).ExpectStatus(401);
            }, needs: Needs.MetaSecret)
            .Test("malformed-json", "WhatsApp webhook: a signed body that isn't JSON gets 400; an empty entry list 200", async ctx =>
            {
                (await ctx.Webhooks.WhatsAppSignedRawAsync("""{"object":"whatsapp_business_account","entry":[""")).ExpectStatus(400);
                (await ctx.Webhooks.WhatsAppSignedRawAsync("""{"object":"whatsapp_business_account","entry":[]}""")).ExpectStatus(200);
            }, needs: Needs.MetaSecret)
            .Test("non-object", "WhatsApp webhook: signed JSON of an unexpected shape is acknowledged, not answered 500", async ctx =>
            {
                var statuses = new List<string>();
                foreach (var body in new[] { "[]", """{"object":"whatsapp_business_account","entry":[1]}""",
                             """{"object":"whatsapp_business_account","entry":[{"id":"x","changes":[{"field":"messages","value":"oops"}]}]}""" })
                {
                    statuses.Add($"{body} -> {(await ctx.Webhooks.WhatsAppSignedRawAsync(body)).Status}");
                }
                Expect.True(statuses.All(s => !s.EndsWith("500")), $"Unexpected shapes crashed the webhook: {string.Join("; ", statuses)}.");
            }, needs: Needs.MetaSecret, finding: "t10-12")
            .Test("object-page", "WhatsApp webhook: a delivery whose object isn't whatsapp_business_account is ignored", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var delivery = WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waId, "Page Object", WhatsAppHelpers.Text(ctx, waId, "hello")));
                delivery["object"] = "page";
                (await ctx.Webhooks.WhatsAppSignedAsync(delivery)).ExpectStatus(200);
                Expect.True(await WhatsAppHelpers.ByWaIdAsync(ctx, client.ClientId, waId) is null, "A delivery with object \"page\" was processed as WhatsApp traffic (a contact was created).");
            }, needs: Signed, finding: "t10-12")
            .Test("new-contact", "WhatsApp webhook: a first message creates the contact, conversation, open window and a NEW_CONTACT notification", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "Hi! What are your opening hours?", profileName: "Rana Auto")).ExpectOk();
                var row = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId);
                Expect.Equal("Rana Auto", InboxHelpers.S(row, "full_name"), "Contact name from the WhatsApp profile");
                Expect.Equal("whatsapp", InboxHelpers.S(row, "source"), "Contact source");
                Expect.True(InboxHelpers.S(row, "window_open") == "True", "The 24-hour window isn't open.");
                var conversationId = InboxHelpers.G(row, "conversation_id");
                var message = (await InboxHelpers.MessagesAsync(ctx, conversationId)).Single();
                Expect.Equal("Hi! What are your opening hours?", InboxHelpers.S(message, "content"), "Message");
                Expect.True((await InboxHelpers.NotificationsAsync(ctx, client)).Any(n => n?["type"]?.ToString() == "NEW_CONTACT" && n["conversationId"]?.ToString() == conversationId.ToString()),
                    "No NEW_CONTACT notification.");
            }, needs: Signed)
            .Test("same-contact", "WhatsApp webhook: later messages from the same wa_id go to the same contact and conversation", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "first")).ExpectOk();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "second")).ExpectOk();
                Expect.Equal(1L, await WhatsAppHelpers.ContactCountAsync(ctx, client.ClientId, waId), "Contacts for one wa_id");
                Expect.Equal(2, (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId), "conversation_id"))).Count, "Messages in the conversation");
            }, needs: Signed)
            .Test("retry", "WhatsApp webhook: Meta redelivering the same delivery stores the message once", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var wamid = ctx.Webhooks.NewWamid();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "only once", wamid: wamid)).ExpectOk();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "only once", wamid: wamid)).ExpectOk();
                Expect.Equal(1, (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId), "conversation_id"))).Count, "Messages after a redelivery");
            }, needs: Signed)
            .Test("concurrent-copies", "WhatsApp webhook: three simultaneous copies of a new message all get 200 and one row", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "first")).ExpectOk();
                var delivery = WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waId, "Automation Contact", WhatsAppHelpers.Text(ctx, waId, "do you have parking?")));
                var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ctx.Webhooks.WhatsAppSignedAsync(delivery)));
                var messages = await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId), "conversation_id"));
                Expect.Equal(2, messages.Count, "Messages (the first plus one copy)");
                Expect.True(responses.All(r => r.Status == 200), $"Statuses for the simultaneous copies: {string.Join(",", responses.Select(r => r.Status))} (a duplicate must be acknowledged, not answered 500).");
            }, needs: Signed, finding: "t10-10")
            .Test("fast-ack", "WhatsApp webhook: a 30-entry delivery is answered within 5 seconds and every message is then stored", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waIds = Enumerable.Range(0, 30).Select(_ => ctx.Webhooks.NewWaId()).ToList();
                var delivery = WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waIds[0], "Bulk 0", WhatsAppHelpers.Text(ctx, waIds[0], "bulk 0")));
                foreach (var (waId, i) in waIds.Skip(1).Select((w, i) => (w, i + 1)))
                {
                    delivery["entry"]!.AsArray().Add(new JsonObject
                    {
                        ["id"] = channel.WabaId,
                        ["changes"] = new JsonArray(WhatsAppHelpers.Messages(channel, waId, $"Bulk {i}", WhatsAppHelpers.Text(ctx, waId, $"bulk {i}"))),
                    });
                }
                var response = (await ctx.Webhooks.WhatsAppSignedAsync(delivery)).ExpectOk();
                Expect.True(response.ElapsedMs < 5000, $"Meta waited {response.ElapsedMs} ms for its answer to a 30-entry delivery (QA saw 69 s); it redelivers slow ones.");
                foreach (var waId in waIds) await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId);
            }, needs: Signed | Needs.Database, finding: "t10-10")
            .Test("concurrent-first", "WhatsApp webhook: simultaneous first messages from one new number make one contact, all 200", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var deliveries = Enumerable.Range(1, 4).Select(i => WhatsAppHelpers.Delivery(channel.WabaId!,
                    WhatsAppHelpers.Messages(channel, waId, "Racing Rami", WhatsAppHelpers.Text(ctx, waId, $"message {i}")))).ToList();
                var responses = await Task.WhenAll(deliveries.Select(d => ctx.Webhooks.WhatsAppSignedAsync(d)));
                Expect.True(responses.All(r => r.Status == 200), $"Statuses: {string.Join(",", responses.Select(r => r.Status))}: racing first messages from a new number fail with 500 (duplicate contact).");
                Expect.Equal(1L, await WhatsAppHelpers.ContactCountAsync(ctx, client.ClientId, waId), "Contacts for one number");
            }, needs: Signed, finding: "t10-10")
            .Test("batch", "WhatsApp webhook: three messages in one change are all stored, in order", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waId, "Batch Bilal",
                    WhatsAppHelpers.Text(ctx, waId, "one", timestamp: now - 4), WhatsAppHelpers.Text(ctx, waId, "two", timestamp: now - 2), WhatsAppHelpers.Text(ctx, waId, "three", timestamp: now))));
                var contents = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId), "conversation_id")))
                    .Select(m => InboxHelpers.S(m, "content")).ToList();
                Expect.True(contents.SequenceEqual(new[] { "one", "two", "three" }), $"Stored: [{string.Join(", ", contents)}].");
            }, needs: Signed)
            .Test("two-entries", "WhatsApp webhook: two entries in one delivery create two contacts", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var a = ctx.Webhooks.NewWaId();
                var b = ctx.Webhooks.NewWaId();
                var delivery = WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, a, "Entry A", WhatsAppHelpers.Text(ctx, a, "hello from A")));
                delivery["entry"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = channel.WabaId,
                    ["changes"] = new JsonArray(WhatsAppHelpers.Messages(channel, b, "Entry B", WhatsAppHelpers.Text(ctx, b, "hello from B"))),
                });
                await WhatsAppHelpers.PostAsync(ctx, delivery);
                await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, a);
                await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, b);
            }, needs: Signed)
            .Test("two-contacts", "WhatsApp webhook: a change carrying messages from two contacts files each under its sender", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var x = ctx.Webhooks.NewWaId();
                var y = ctx.Webhooks.NewWaId();
                var value = Webhooks.MessagesValue(channel, x, "Multi X", WhatsAppHelpers.Text(ctx, x, "from X: what are your hours?"), WhatsAppHelpers.Text(ctx, y, "from Y: how much is service A?"));
                value["contacts"]!.AsArray().Add(new JsonObject { ["profile"] = new JsonObject { ["name"] = "Multi Y" }, ["wa_id"] = y });
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Change("messages", value)));
                var yRow = await WhatsAppHelpers.ByWaIdAsync(ctx, client.ClientId, y);
                var xMessages = (await InboxHelpers.MessagesAsync(ctx, InboxHelpers.G(await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, x), "conversation_id")))
                    .Select(m => InboxHelpers.S(m, "content")).ToList();
                Expect.True(yRow is not null && !xMessages.Any(c => c!.StartsWith("from Y")),
                    $"Y's message was filed under X (X's thread: [{string.Join(", ", xMessages)}]; Y's contact {(yRow is null ? "was never created" : "exists")}): the AI would answer Y's question to X.");
            }, needs: Signed, finding: "t10-05")
            .Test("ad-referral", "WhatsApp webhook: a message from a click-to-WhatsApp ad keeps the ad and marks the contact's source", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var message = WhatsAppHelpers.Text(ctx, waId, "Hi, I saw your ad. Is the offer still valid?");
                message["referral"] = new JsonObject
                {
                    ["source_url"] = "https://fb.me/auto", ["source_id"] = "120201", ["source_type"] = "ad",
                    ["headline"] = $"[{ctx.Tag}] 50% off Service A", ["body"] = "This week only", ["ctwa_clid"] = "ARAkAuto",
                };
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waId, "Ad Contact", message)));
                var row = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId);
                var detail = await ctx.Db.ScalarAsync<string?>("select source_detail from crm.contacts where id = @id", new { id = row["contact_id"] }, ctx.Ct);
                Expect.True(InboxHelpers.S(row, "source") == "whatsapp_ad" && detail?.Contains("50% off Service A") == true,
                    $"The contact from the ad has source \"{InboxHelpers.S(row, "source")}\" / \"{detail}\"; expected whatsapp_ad with the ad's headline.");
                var metadata = await ctx.Db.ScalarAsync<string?>("select metadata_json::text from crm.messages where conversation_id = @c order by created_at desc limit 1", new { c = row["conversation_id"] }, ctx.Ct);
                Expect.True(metadata?.Contains("ARAkAuto") == true, $"The message lost the ad referral (ctwa_clid): metadata {metadata ?? "null"}.");
            }, needs: Signed, finding: "t10-08")
            .Test("changed-number", "WhatsApp webhook: a customer's changed number moves their contact, so the new number continues it", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var oldId = ctx.Webhooks.NewWaId();
                var newId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, oldId, "hello from my old number")).ExpectOk();
                var before = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, oldId);
                var system = WhatsAppHelpers.Message(ctx, oldId, "system", new JsonObject
                {
                    ["body"] = $"User A changed from {oldId} to {newId}", ["wa_id"] = newId, ["type"] = "user_changed_number",
                });
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, oldId, "Changed", system)));
                (await ctx.Webhooks.WhatsAppTextAsync(channel, newId, "hello from my new number")).ExpectOk();
                var after = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, newId);
                Expect.True(after["contact_id"]?.ToString() == before["contact_id"]?.ToString() && await WhatsAppHelpers.ContactCountAsync(ctx, client.ClientId, oldId) == 0,
                    "The new number made a second contact instead of continuing the first one.");
                var notice = await ctx.Db.ScalarAsync<string?>("select content from crm.messages where conversation_id = @c and message_type = 'system' limit 1", new { c = before["conversation_id"] }, ctx.Ct);
                Expect.True(!string.IsNullOrWhiteSpace(notice), "The number-change notice was saved as an empty bubble.");
            }, needs: Signed, finding: "t10-08")
            .Test("old-timestamp", "WhatsApp webhook: a message Meta timestamped 30 hours ago doesn't open the window", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "(sent 30 hours ago) are you open today?", at: DateTimeOffset.UtcNow.AddHours(-30))).ExpectOk();
                var row = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId);
                Expect.True(InboxHelpers.S(row, "window_open") == "False",
                    "A redelivered 30-hour-old message opened a fresh 24-hour window (it's computed from the server clock, not Meta's timestamp).");
            }, needs: Signed, finding: "t8-05")
            .Test("stop", "WhatsApp webhook: a typed STOP opts the new contact out", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "STOP")).ExpectOk();
                var row = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId);
                Expect.True(row["opted_out_at"] is not null && InboxHelpers.S(row, "marketing_opt_in") == "False", "STOP didn't opt the contact out.");
            }, needs: Signed)
            .Test("stop-button", "WhatsApp webhook: a template's STOP / \"Stop promotions\" quick-reply opts the contact out", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.Messages(channel, waId, "Optout Olga",
                    WhatsAppHelpers.Message(ctx, waId, "button", new JsonObject { ["payload"] = "STOP", ["text"] = "Stop promotions" }))));
                var row = await WhatsAppHelpers.RequireByWaIdAsync(ctx, client.ClientId, waId);
                Expect.True(row["opted_out_at"] is not null, "Tapping the template's STOP quick-reply button didn't opt the contact out.");
            }, needs: Signed, finding: "t8-08")
            .Test("bad-event-in-batch", "WhatsApp webhook: one bad event doesn't fail the whole delivery", async ctx =>
            {
                var (client, channel) = await HookAsync(ctx);
                var waId = ctx.Webhooks.NewWaId();
                var delivery = WhatsAppHelpers.Delivery(channel.WabaId!,
                    WhatsAppHelpers.Messages(channel, waId, "Batch Victim", WhatsAppHelpers.Text(ctx, waId, "what are your opening hours?")),
                    WhatsAppHelpers.Change("message_template_status_update", new JsonObject
                    {
                        ["event"] = "APPROVED", ["message_template_id"] = 990000000777, ["message_template_name"] = "auto_" + new string('n', 252),
                        ["message_template_language"] = "en_US", ["reason"] = "NONE",
                    }));
                var response = await ctx.Webhooks.WhatsAppSignedAsync(delivery);
                Expect.True(response.Status == 200, $"A delivery with one over-long template name (Meta allows 512 characters) answered {response.Status}: Meta retries it forever and the customer message in it never gets an AI reply.");
                Expect.True(await WhatsAppHelpers.ByWaIdAsync(ctx, client.ClientId, waId) is not null, "The customer message in the same delivery wasn't stored.");
            }, needs: Signed, finding: "t10-03");

        return suite.Build();
    }
}

