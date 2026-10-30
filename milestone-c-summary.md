# Milestone C summary: go-live features

Done on branch `feature/go-live` in both repos (from `refactor/generic-platform`), committed per session, **not pushed or
deployed**. Main app: 8 commits `862788e`..`c753f8f`, 2,232 tests green. Portal: 2 commits `11b42e7`, `7b45d75`, 94 tests
green. Plan and decisions: `milestone-c-plan.md`; session log with every check: `milestone-c-progress.md`.

## What clients get

- **Forgot password:** a reset link by email (Brevo), valid 60 minutes, used once; it signs out other sessions.
- **Member invites:** the owner (role `client`) invites by email; the member sets a password from the link (7 days) and
  lands signed in. Pending invites hold a seat; resend and cancel. Members can't manage members.
- **STOP opt-out:** a contact who sends a stop word (STOP, ARRÊT, توقف, ...) gets one confirmation in their language and
  is then left out of campaigns, reminders and Inbox templates; START undoes it. The contact page shows it.
- **Privacy:** the owner can export everything about a contact as JSON, or delete the contact and all its data after
  typing its name.
- **Booking reminders:** Settings → Booking reminders: two reminders (default 24 h and 2 h before), WhatsApp through an
  approved template per language, Telegram as text. The booking page shows each reminder and why one wasn't sent.
- **New pricing:** Free Trial (7 days, then it ends), Inbox $39, + Bookings $59, Growth $99, AI Pro $169, Business $299,
  AI reply $0.02. The AI answers on as many connected channels as the plan's AI bots. Settings → Billing shows trial days
  left and add-ons.

## What you get in the portal

Send a member a reset link, see and cancel invites; Billing → Add-ons (catalog: extra user $8, channel $5, AI bot $15,
WhatsApp number $10 per month); per client "Change add-ons" (charged from the wallet now and every period with the plan);
free-trial days on plans; the AI bot limit on client details; export and delete a contact.

## What you need to do (also in `SculptFlowApp/docs/deploy-runbook.md`, section "Milestone C")

1. **Brevo:** verify the sending domain (SPF, DKIM, DMARC), set `Brevo__ApiKey` on Render, set `Email:FromAddress` in
   the portal's Configuration page. Without the key, emails fail in Production.
2. **Deploy:** after A+B (or merged into it), merge `feature/go-live`, run `schema.sql`, `seed-config.sql`,
   `seed-billing.sql`, and set `Billing:SignupPlanCode` = `trial` if new signups should get the trial. No n8n re-import.
3. **WhatsApp Manager:** a utility reminder template in English, Arabic and French with {{1}} name, {{2}} date and time,
   {{3}} business; clients pick them in their reminder settings once approved.
4. **Existing clients:** they keep their old plans (no AI bot limit) until you move them to the new ones in the portal.
5. **Check prices** of the add-ons and plans in the portal.

## Choices I made without asking (change any of them)

- AI bots: the first N connected channels in connection order get the AI; there is no per-channel switch yet.
- WhatsApp numbers: the app still connects one number per client; the plan limit is stored and shown only.
- Add-ons: no proration and no refund when units go down; added units are charged at once.
- A trial expires at its end; nothing moves the client to a paid plan automatically.
- The n8n "opted out" field was dropped: the AI is never called for a STOP or START message.
- Advanced reporting, API access and the other pricing-page extras are plan flags only; nothing new was built for them.

## Known gaps

- A failed reminder doesn't record its channel (sent and skipped ones do).
- Brevo was only tested with a fake server and the log provider (no real key here).
- The automation tester is still on hold (as in A and B).
