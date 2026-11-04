using Omni.Automation.Core;

namespace Omni.Automation.Suites;

/// <summary>One end-to-end story on a private client: sign up, add a service and opening hours, add a contact, let the AI
/// Booking bot book through its tools (as the bot engine and n8n call them), see the booking in the dashboard, record the
/// order, and check the dashboard's numbers.</summary>
[Suite(100)]
public static class JourneySuite
{
    public static TestSuite Build() => new SuiteBuilder("journey", "End-to-end journey", "Sign-up to a booked, paid service in the dashboard numbers.")
        .Test("signup-to-revenue", "Journey: sign up → service and hours → contact → AI booking → dashboard booking → order → dashboard numbers", async ctx =>
        {
            // 1. Sign up (the core registers through /Account/Register and creates the first service).
            var client = await ctx.RegisterClientAsync("journey");
            (await client.Http.GetAsync("/dashboard", ct: ctx.Ct)).ExpectOk();

            // 2. A service and opening hours.
            var service = (await client.Http.PostAsync("/api/services", new
            {
                name = $"Haircut {ctx.Tag}", code = $"auto-{ctx.Tag}-haircut", description = "Journey service", bookingDuration = 45,
            }, ct: ctx.Ct)).ExpectStatus(201).Id();
            Expect.ContainsId((await client.Http.GetAsync("/api/services", ct: ctx.Ct)).ExpectOk().Array(), service, "Services list");
            await ctx.Fixtures.SaveAvailabilityAsync(client);

            // 3. A contact.
            var contact = await ctx.Fixtures.NewContactAsync("journey", client);

            // 4. The AI plan and an active Booking bot.
            await ctx.SetPlanAsync(client, AiHelpers.AiPlan);
            var bot = await AiHelpers.CreateBotAsync(ctx, client, "booking");
            await AiHelpers.ExpectStatusSetAsync(ctx, client, bot, "active");

            // 5. The bot's tools: open slots for the service, then the booking.
            var headers = AiHelpers.BotHeaders(bot);
            var tomorrow = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd");
            var slots = (await ctx.Ingest.GetAsync($"/api/ai/bookings/available?clientId={client.ClientId}&contactId={contact}&serviceId={service}&date={tomorrow}",
                headers, ctx.Ct)).ExpectOk();
            Expect.Equal("true", slots.Str("canCreateNewBooking").ToLowerInvariant(), "canCreateNewBooking for a contact with no booking");
            Expect.Equal(45, slots.Prop("durationMinutes").GetValue<int>(), "Slot length for the 45-minute service");
            var slot = slots.Array("slots").Skip(6).FirstOrDefault() ?? throw new TestFailure($"No open slot tomorrow.{slots.Snippet(200)}");
            var booked = (await ctx.Ingest.PostAsync($"/api/ai/bookings/schedule?clientId={client.ClientId}&contactId={contact}",
                new { scheduledStart = slot["start"]!.ToString(), serviceId = service, notes = "", reason = "Journey", confirmReplaceExisting = false }, headers, ctx.Ct)).ExpectOk();
            Expect.Equal("BOOKED", booked.Str("code"), "AI booking");
            var bookingId = Guid.Parse(booked.Prop("booking")["id"]!.ToString());

            // 6. The booking in the dashboard.
            var list = (await client.Http.GetAsync("/api/bookings?status=booked", ct: ctx.Ct)).ExpectOk();
            Expect.ContainsId(list.Array("items"), bookingId, "Dashboard bookings");
            var detail = (await client.Http.GetAsync($"/api/bookings/{bookingId}", ct: ctx.Ct)).ExpectOk();
            Expect.Equal(service.ToString(), detail.Str("serviceId"), "Service of the booking");
            Expect.Equal(DateTimeOffset.Parse(slot["start"]!.ToString()), DateTimeOffset.Parse(detail.Str("scheduledStart")), "Start of the booking");

            // 7. The order, completed with its final amount.
            var order = (await client.Http.PostAsync("/api/orders", new
            {
                clientId = client.ClientId, contactId = contact, serviceId = service, bookingId, quotedAmount = 120m, currencyCode = "USD", notes = "Journey order",
            }, ct: ctx.Ct)).ExpectStatus(201).Id();
            (await client.Http.PatchAsync($"/api/orders/{order}", new { status = "completed", finalAmount = 120m, serviceDate = DateTimeOffset.UtcNow }, ctx.Ct)).ExpectOk();

            // 8. The dashboard's numbers (all time).
            var summary = (await client.Http.GetAsync("/api/dashboard/summary", ct: ctx.Ct)).ExpectOk();
            Expect.True(summary.Prop("newInterestedPeople").GetValue<int>() >= 1, "The dashboard counts no new contacts.");
            Expect.Equal(1, summary.Prop("bookingsMade").GetValue<int>(), "Bookings made on the dashboard");
            Expect.Equal(1, summary.Prop("ordersMade").GetValue<int>(), "Orders made on the dashboard");
            Expect.Equal(120m, summary.Prop("revenue").GetValue<decimal>(), "Revenue on the dashboard");
        }, needs: Needs.Session | Needs.AdminKey | Needs.IngestKey | Needs.Database, risk: Risk.Billing)
        .Build();
}
