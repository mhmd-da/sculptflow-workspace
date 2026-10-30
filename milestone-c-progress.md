# Milestone C progress

Plan: `milestone-c-plan.md`. Branch `feature/go-live` in both repos (from `refactor/generic-platform`: main app 80207cb,
portal 7c7b38e). Nothing pushed. Test floor at the start: 2,084 main app, 80 portal (8 known warnings, all in tests).

Check tools (scratchpad of the session that wrote them; recreate if gone): throwaway Postgres on port 55433 (`initdb` +
`postgres.exe -p 55433`), `schema-twice.ps1` (schema.sql + seeds twice on a fresh database; swaps the vector column and the
demo seed's placeholder embedding for real[] because the local server has no pgvector), `run-app.ps1` (publish + run in
Staging on `http://localhost:5199` against that database).

## S1 · Email and password reset (main app) — done 2026-10-21

- Brevo: `IBrevoApiClient` (`POST /v3/smtp/email`, `api-key` header, secret `Brevo:ApiKey`, optional `Brevo:ApiBaseUrl`),
  `IEmailProvider` = `BrevoEmailProvider` (sender from `Email:FromAddress` / `FromName`; empty address refuses) or
  `LogEmailProvider` (no key, outside Production: the email and its link go to the log).
- `IReaderTextFactory` / `ReaderText`: text in a reader's language (first offered of their preferences, else English) with a
  business type's words; reused later for invites, opt-out confirmations and reminders.
- `EmailLayout` (inline styles, lang/dir, encoded text, plain-text twin), `AccountEmailService.SendPasswordResetAsync`
  (user language → client default → page language).
- `identity.account_tokens` (hash only, purpose invite/password_reset, used/revoked/expiry) + `AccountTokenRepository`.
- `PasswordResetService`: same answer for unknown addresses, active client/member users only, `Accounts:ResetEmailsPerHour`
  (3), `Accounts:ResetLinkMinutes` (60); reset validates first, then password + lockout cleared + email confirmed + new
  security stamp + every open reset link closed, one transaction. Links use `App:PublicBaseUrl` when set.
- Pages `/Account/ForgotPassword`, `/Account/ResetPassword` (anonymous), Login link "Forgot password?" and the
  "password changed" notice. Platform admin `POST /api/platform-admin/members/{userId}/password-reset-email`.
- `EmailSendException` → 502 with the fixed text `emails.errors.notSent` (the provider's reason only in the log).
- Settings added: `Accounts:ResetLinkMinutes`, `Accounts:ResetEmailsPerHour`, `Accounts:InviteDays` (for S2),
  `Email:FromAddress`, `Email:FromName` (88 declared).
- Guard: `CopyScriptCoverageTests` has a `NotInOldProject` list for tables new in Milestone C (nothing to copy).
- Checks: build 0 errors, no new warnings; tests **2,127** passed (+43); schema + seed-config + seed + seed-demo twice OK;
  published app in Staging: Login, Login?reset=done, ForgotPassword, ResetPassword in en/ar/fr at 390 and 1366 (right dir,
  no overflow, no raw keys); full flow in the browser: request for reception@aurora-demo.test → link from the log →
  new password → signed in with it.
- Note: the email followed the client's default language (English) although the page was French, as designed
  (user → client default → page).

## S2 · Member invites (main app) — done 2026-10-21

- Adding a member with a password is gone (`AddMemberRequest`, `POST /api/members`, `MemberService.AddAsync`). Members join
  only through an emailed invite: `IInviteService` (invite, list, resend, cancel, landing, accept), `/api/members/invites`
  (GET, POST 201, `POST {id}/resend`, `DELETE {id}`), page `/Account/AcceptInvite` (anonymous; signs the new member in).
- Invites live in `identity.account_tokens` (purpose `invite`, hash only, `Accounts:InviteDays` = 7). An open invite holds
  a seat, also when the owner reactivates a member (`MemberService` counts open invites). Resend revokes the old link (and
  needs a seat again if it had expired); a failed email rolls the invite back. An email that already signs in is refused at
  invite and at accept time; a second open invite for the same email is refused (`members.errors.alreadyInvited`).
- Invite email: client's default language, then the inviter's (`AccountEmailService.SendInviteAsync`). `EmailLayout` now
  escapes only `& < > " '` (accented letters stay readable).
- Members page: invite form (email, optional name), "Invites waiting" list (sent, waiting/expired, resend, cancel), seats =
  active people + open invites. `MemberRules.EnsureClientRole` shared by both services.
- Platform admin: `GET /api/platform-admin/members/invites?clientId`, `DELETE /api/platform-admin/members/invites/{id}`.
- Checks: build clean (same 8 test warnings); tests **2,149** (+22); browser: seat limit refusal shown, deactivate → invite →
  link from the log → accept page → signed in as the new member; accept page (valid and dead link) and Members in en/ar/fr at
  390 and 1366. Fixed: the French "invites hold a seat" note overflowed at 390 (moved under the table) — re-check in S3's walk.

## P1 · Portal: reset email and invites — done 2026-10-21 (portal `11b42e7`)

- Member details: "Email a reset link" (`POST members/{userId}/password-reset-email`, audit `member.password_reset_email_sent`).
- Members filtered to one client: "Invites waiting" panel with cancel (`GET members/invites`, `DELETE members/invites/{id}`,
  audit `member.invite_revoked`). Copy `Entities/Responses/Members/InviteResponse`; `MAIN_APP_SYNC.md` at `4e683cf`.
- Checks: portal build 0 warnings; tests **83** (+3); leftover guard OK; live against the local main app (portal on 5299,
  main app on 5199 with `PlatformAdmin__ApiKey`, a local admin made with `create-admin`): reset email sent and logged by
  the main app; a seeded invite listed and cancelled.
- Seen, not changed: the member details "Access" note still says the main app doesn't re-check accounts; it does since
  Milestone A (stale text from before).

## S3 · STOP opt-out (main app) — done 2026-10-22

- `OptOutKeywords` (whole message only; case, accents, Arabic diacritics/alef forms/tatweel, surrounding punctuation and
  repeated spaces ignored; messages over 40 characters never match) and `IOptOutService` (match, apply + event, confirmation
  text in the contact's language then the client default, contact page switch). Settings `OptOut:Enabled`,
  `StopKeywords`, `StartKeywords`, `SendConfirmation` (92 declared). Events `contact_opted_out` / `contact_opted_in`.
- `MessageService.IngestAsync`: a customer text or command (Telegram `/stop`) that is a stop/start word updates the
  contact in the same save, skips the AI (`opt_out_keyword`), and sends one confirmation through the new
  `IMessageService.SendSystemTextAsync` (sender/origin `system`, metadata `{"automatic":true}`, billed, never changes
  the mode; a failure is traced only). `inbox.js` shows such messages as bubbles, not markers (jsonb spacing tolerated).
  S5 reuses `SendSystemTextAsync` for Telegram reminders.
- Inbox templates and campaign sends to an opted-out contact are refused (`inbox.errors.contactOptedOut`, 422).
- Contact page: "Opted out" chip, "Messages from you" note with Opt out / Opt back in (any member).
- Decided against the plan's n8n field `contactOptedOut`: the AI only answers messages the contact sent, so it adds
  nothing, and leaving the payload alone means no n8n re-import for C.
- Checks: tests **2,181** (+32, incl. a Postgres flow: "Stop!" → contact opted out, event, billed system reply);
  browser: contact page en/ar/fr at 390/1366 (no overflow, no raw keys), opt out and back in; Members fr 390 now fits.

## S4 · Contact export and delete (main app) — done 2026-10-22

- `IContactDataService` + `IContactDataRepository`: export (`ContactExport`: contact, conversations, every message,
  bookings, orders, activity; JSON file via `ContactExportFile`, named `contact-<date>-<id8>.json`, never by name) and
  permanent delete (typed name, else phone, else email; notifications deleted, contact deleted set-based, the database
  cascades the rest and unlinks events; one transaction; event `contact_deleted` with no contact link).
- Dashboard: account owner only (`ContactDataRules.EnsureClientRole`, 403). `GET /api/contacts/{id}/export`,
  `DELETE /api/contacts/{id}` `{confirmation}`; contact page "Privacy" panel (Export link, "Delete for good" disclosure
  with the typed confirmation); the contacts list shows the "deleted" message.
- Platform admin: `GET /api/platform-admin/contacts/{id}/export`, `DELETE /api/platform-admin/contacts/{id}`.
- Not touched by a delete (documented): booking events already copied to an external calendar, message-trace steps
  (they expire after 14 to 60 days).
- Fixed on the way: S3's change-log row in system-design had "Â§" (a PowerShell re-encode of a helper script); corrected.
- Checks: tests **2,199** (+18, incl. a Postgres flow seeding a contact with conversation, messages, booking, order,
  event and notification, then export and delete); browser: export download (6 messages, 1 booking), Privacy panel in
  en/ar/fr at 390/1366, wrong confirmation refused, right one (case and spaces ignored) deletes and returns to the list.

## S5 · Booking reminders (main app) — done 2026-10-22

- Tables `scheduling.reminder_settings` (one per client; no row = off; hours before each of two reminders, 0 = off,
  max 168; templates jsonb language → template id) and `scheduling.booking_reminders` (unique booking + kind + start;
  status sending/sent/skipped/failed + reason + channel + message).
- `IBookingReminderService` + `BookingReminderWorker` (every `Reminders:IntervalSeconds`, `Reminders:Enabled`,
  `Reminders:BatchSize`; 95 settings). Nearer reminder first; skip reasons: made inside the window, superseded, opted
  out, no conversation/phone, channel unsupported, no/unapproved template, more than three template values; failures:
  no balance, send failed (recorded, never retried). A reschedule (new start) gets new rows.
- WhatsApp: `IMessageService.SendReminderTemplateAsync` (sender/origin `system`, `{"automatic":true}`, keeps the
  conversation's mode, event `booking_reminder_sent`), values {{1}} name, {{2}} date + time in the client's timezone and
  the template's language, {{3}} business. Telegram: `SendSystemTextAsync` with `reminders.text` in the contact's
  language. Template choice: contact language → client default → any chosen.
- Page `/settings/reminders` (sidebar under Calendar integrations, bookings entitlement), booking page reminder list,
  labels via `ReminderText` with a guard test (`Every_reminder_status_and_reason_has_a_label`).
- Checks: tests **2,214** (+15, incl. three Postgres flows: due/claim-once/skip rules/reschedule, Telegram Arabic text and
  missing template, failed send recorded once); schema twice on a fresh and on a used database; browser: settings page in
  en/ar/fr at 390/1366, saved with the demo "appointment_reminder" template; the worker picked the due demo booking,
  called Meta (refused: the demo token is fake) and the booking page shows "Failed" with the reason in all three languages.
- Known: a failed send doesn't record its channel (only sent/skipped do). Demo reminders turned off again on the scratch DB.

## S6 · New pricing: plans, trial, add-ons, AI bots (main app) — done 2026-10-22

- `Database/seed-billing.sql` (new, re-runnable, never changes existing rows): plans `trial` ($0, 7 days, 1 user, 1
  channel, inbox only), `inbox` ($39), `inbox-bookings` ($59), `growth` ($99, campaigns, $10 credit), `ai-pro` ($169,
  2 WhatsApp numbers, 2 AI bots, advanced reporting, $20 credit), `business` ($299, 12 users, unlimited channels, 5
  numbers, 5 bots, API, $40 credit); add-ons `extra-user` $8, `extra-channel` $5, `extra-ai-bot` $15,
  `extra-whatsapp-number` $10; default rate card if missing and `ai_reply` $0.02 on it.
- `billing.plans.trial_days` (1–90, price 0): the period lasts that many days and then expires (no renewal; event
  `subscription_expired` with `trialEnded`, credit expired). `Billing:SignupPlanCode` default is now `trial`.
- Add-ons: tables `billing.addons`, `billing.client_addons`, ledger type `addon_charge` (constraints upgraded in place on
  used databases), `IAddonService` + admin endpoints `GET addons`, `PUT addons/{code}`, `GET clients/{id}/addons`,
  `PUT clients/{id}/addons/{code}` (Idempotency-Key). Added units charged now; plan + add-ons charged at every period
  start (past due when the wallet can't pay both); fewer units refund nothing. Limits = plan + units (`AddonLimits`).
- AI bots (`max_ai_bots`): the first N connected channels, in connection order, get the AI; new conversations on the
  others start with the team, customer messages there move AI conversations to the team (reason `ai_bot_limit`), Return
  to AI is refused there; Business info → AI tab names those channels. A plan without the key has no AI bot limit (old
  plans keep working). WhatsApp numbers: still one per client in the app; the limit is only stored/shown.
- Settings → Billing: trial end date and days left, add-ons table, "contact us for more" line.
- Checks: tests **2,232** (+18, incl. seven Postgres flows: trial expiry, add-on charge/retry/remove, refusals, renewal
  with add-ons → past due, catalog rules, summary); schema + all seeds twice on a fresh DB and upgraded on the used one;
  `test-copy.ps1` PASS; browser: Billing (trial and paid) and the AI tab in en/ar/fr at 390/1366 through the admin API.
  Fixed on the way: RTL "+2" and account names on the Billing page (`dir`).

## P2 · Portal: add-ons, trial plans, AI bots, contact privacy — done 2026-10-22

- New Billing → Add-ons page (catalog: name, limit raised on create, price per unit and period, active, client count).
  Client billing page: Add-ons panel and "Change add-ons" (units in total; added units charged now with the form's
  operation id as Idempotency-Key; 0 removes), trial row on the subscription, trial plans in the plan picker.
- Plans: "Free trial days" field and "free trial · N days, no renewal" in the list. Client details: AI bots row (plan +
  add-ons) naming the channels without AI. Contacts: "Export data" (the main app's JSON passed through, indented,
  readable Arabic) and "Delete" after typing the contact's name ("delete" when it has none); opt-out note kept.
- Copies: `AddonResponse`, `ClientAddonRow`, `AddonRequest`, `SetClientAddonRequest`, `PlanRequest/Response.TrialDays`,
  `ClientBillingSummary.TrialDays/Addons`, `InboxSettingsDto.ChannelsWithoutAiBot/AiBotLimit`; MAIN_APP_SYNC.md at
  main app `e4a86df` (S6 follow-up: ledger label "Add-ons", and a guard fix in seed-billing.sql that the S6 commit missed
  because the guard only scans tracked files).
- Checks: portal tests **94** (+11); live with both apps: catalog edit, client add-on set (wallet charged 15, ledger
  "Add-ons"), AI bots row, trial days saved through the plan form, export download, wrong name refused, delete → contact
  gone and audited (`contact.deleted` under the client). Scratch values restored.

## S7 · Final checks and summary — done 2026-10-22

- Full suites: main app **2,232**, portal **94**, both green; leftover-names guards green in both repos; `test-copy.ps1`
  PASS (S6). Page sweep with the demo owner: 18 dashboard pages × en/ar/fr = 54 loads, all 200 in the right language with
  no raw text keys (S1–S6 each had its own visual check at 390/1366).
- `docs/deploy-runbook.md`: new "Milestone C" section (C1–C8), committed `c753f8f`. `milestone-c-plan.md` updated to what
  was built (D13 AI bots by connection order, plan codes, no n8n field). `milestone-c-summary.md` written.
