using System.Text.Json.Nodes;
using Omni.Automation.Core;
using Omni.Automation.Http;

namespace Omni.Automation.Suites;

/// <summary>Which client a signed event lands in (t10-01).</summary>
[Suite(52)]
public static class WhatsAppRoutingSuite
{
    private const Needs Signed = Needs.Session | Needs.Database | Needs.MetaSecret;

    /// <summary>Inserts a disconnected WhatsApp row (no token) holding <paramref name="wabaId"/>, the state a client is left in
    /// after disconnecting (or after squatting a WABA id through the unverified PUT, t8-01).</summary>
    private static Task InsertDisconnectedAsync(TestContext ctx, TestClient client, string phoneNumberId, string wabaId) =>
        ctx.Db.ExecuteForClientAsync(client.ClientId, """
            insert into channels.channel_integrations (id, client_id, channel, status, display_name, phone_number_id, whatsapp_business_id, created_at, updated_at)
            values (gen_random_uuid(), @clientId, 'whatsapp', 'disconnected', @name, @phone, @waba, now(), now())
            """, new { name = $"{TestContext.ClientNamePrefix} WA disconnected", phone = phoneNumberId, waba = wabaId }, ctx.Ct);

    public static TestSuite Build() => new SuiteBuilder("wa-routing", "WhatsApp: routing between clients",
            "Which client a signed Meta event lands in when numbers are disconnected or two clients hold the same WABA id (t10-01).")
        .Test("disconnected-number", "WhatsApp routing: a disconnected number no longer receives customer messages", async ctx =>
        {
            var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-off");
            (await client.Http.PostAsync("/api/channel-integrations/whatsapp/disconnect", ct: ctx.Ct)).ExpectStatus(204);
            var waId = ctx.Webhooks.NewWaId();
            (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "hello to a disconnected number")).ExpectOk();
            Expect.True(await WhatsAppHelpers.ByWaIdAsync(ctx, client.ClientId, waId) is null,
                "A message to a disconnected WhatsApp number created a contact and conversation (routing ignores the connection status).");
        }, needs: Signed, finding: "t10-01")
        .Test("disconnected-waba", "WhatsApp routing: a WABA-level event isn't applied to a client whose connection is disconnected", async ctx =>
        {
            var client = await ctx.NamedClientAsync("wa-squat2");
            var waba = $"auto-waba-{ctx.Tag}-lone-{Random.Shared.Next(100000, 999999)}";
            await InsertDisconnectedAsync(ctx, client, $"auto-{ctx.Tag}-lone-{Random.Shared.Next(100000, 999999)}", waba);
            var name = $"auto_{ctx.Tag}_lone".ToLowerInvariant();
            await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(waba, WhatsAppHelpers.Change("message_template_status_update", new JsonObject
            {
                ["event"] = "APPROVED", ["message_template_id"] = 990000000000 + Random.Shared.Next(1000, 9999), ["message_template_name"] = name,
                ["message_template_language"] = "en_US", ["reason"] = "NONE",
            })));
            var stored = await ctx.Db.ScalarAsync<long>("select count(*) from channels.whatsapp_templates where client_id = @clientId and name = @name", new { clientId = client.ClientId, name }, ctx.Ct);
            Expect.True(stored == 0, "A template event for a WABA id held only by a disconnected connection created a template in that client (routing ignores the connection status).");
        }, needs: Signed, finding: "t10-01")
        .Test("shared-waba", "WhatsApp routing: a WABA-level event goes to the connected owner, not a disconnected client with the same WABA id", async ctx =>
        {
            var squatter = await ctx.NamedClientAsync("wa-squat");
            var owner = await ctx.NamedClientAsync("wa-owner");
            var waba = $"auto-waba-{ctx.Tag}-shared-{Random.Shared.Next(100000, 999999)}";
            await InsertDisconnectedAsync(ctx, squatter, $"auto-{ctx.Tag}-squat-{Random.Shared.Next(100000, 999999)}", waba);
            // The app picks the first row with that WABA id (no status filter, no order). Make sure that row is the
            // disconnected one, so the outcome doesn't depend on where Postgres happened to put the rows.
            FakeChannel? channel = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (channel is not null)
                {
                    await ctx.Db.ExecuteForClientAsync(owner.ClientId, "delete from channels.channel_integrations where client_id = @clientId and id = @id", new { id = channel.IntegrationId }, ctx.Ct);
                }
                channel = await ctx.Webhooks.FakeWhatsAppAsync(owner, wabaId: waba);
                var first = await ctx.Db.ScalarAsync<Guid>("select client_id from channels.channel_integrations where channel = 'whatsapp' and whatsapp_business_id = @waba limit 1", new { waba }, ctx.Ct);
                if (first == squatter.ClientId) break;
                ctx.Log($"The owner's row comes first (attempt {attempt + 1}); re-inserting it.");
            }
            var name = $"auto_{ctx.Tag}_shared".ToLowerInvariant();
            await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel!.WabaId!, WhatsAppHelpers.Change("message_template_status_update", new JsonObject
            {
                ["event"] = "APPROVED", ["message_template_id"] = 990000000000 + Random.Shared.Next(1000, 9999), ["message_template_name"] = name,
                ["message_template_language"] = "en_US", ["reason"] = "NONE",
            })));
            var rows = await ctx.Db.QueryAsync("select client_id from channels.whatsapp_templates where name = @name and client_id in (@a, @b)",
                new { name, a = owner.ClientId, b = squatter.ClientId }, ctx.Ct);
            var inOwner = rows.Count(r => (Guid)r["client_id"]! == owner.ClientId);
            var inSquatter = rows.Count(r => (Guid)r["client_id"]! == squatter.ClientId);
            Expect.True(inOwner == 1 && inSquatter == 0,
                $"The template event landed in the owner {inOwner} time(s) and in the disconnected client holding the same WABA id {inSquatter} time(s).");
        }, needs: Signed, finding: "t10-01")
        .Test("unknown-target", "WhatsApp routing: an unknown phone number id and WABA is acknowledged and nothing is stored", async ctx =>
        {
            var (client, _) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-hook");
            var nowhere = new FakeChannel(Guid.Empty, "whatsapp", null, $"auto-{ctx.Tag}-nowhere", $"auto-waba-{ctx.Tag}-nowhere");
            var waId = ctx.Webhooks.NewWaId();
            (await ctx.Webhooks.WhatsAppTextAsync(nowhere, waId, "nowhere")).ExpectOk();
            Expect.Equal(0L, await ctx.Db.ScalarAsync<long>("select count(*) from crm.contacts where phone = @phone", new { phone = "+" + waId }, ctx.Ct), "Contacts created anywhere");
            Expect.True(await WhatsAppHelpers.ByWaIdAsync(ctx, client.ClientId, waId) is null, "A contact was created.");
        }, needs: Signed)
        .Test("status-other-number", "WhatsApp routing: a status for one client's message sent via another client's number changes nothing", async ctx =>
        {
            var (client, channel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-hook");
            var (_, otherChannel) = await WhatsAppHelpers.WhatsAppAsync(ctx, "wa-owner2");
            var waId = ctx.Webhooks.NewWaId();
            (await ctx.Webhooks.WhatsAppTextAsync(channel, waId, "hello")).ExpectOk();
            var wamid = ctx.Webhooks.NewWamid();
            await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(channel.WabaId!, WhatsAppHelpers.ParserEcho(ctx, channel, waId, "outbound", wamid)));
            await WhatsAppHelpers.PostAsync(ctx, WhatsAppHelpers.Delivery(otherChannel.WabaId!, WhatsAppHelpers.Status(otherChannel, wamid, "read", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), waId)));
            var row = await WhatsAppHelpers.MessageByExternalIdAsync(ctx, client.ClientId, wamid);
            Expect.True(InboxHelpers.S(row!, "read") == "False", "Another client's number changed this client's message status.");
        }, needs: Signed)
        .Build();
}

