# Milestone C: execution plan (go-live features and the new pricing)

Written 2026-10-21 from `generic-rename-plan.md` ("Other features" and "Final decisions", 2026-10-16), your pricing page
(`pricing/omni-pricing.html`, 2026-10-21) and the code on `refactor/generic-platform` (main app 80207cb, portal 7c7b38e).
Sessions log their progress in `milestone-c-progress.md`. `docs/omni-subscription-plans.md` (the older proposal) is
superseded by the pricing page.

## 1. What Milestone C delivers

- **Email** through the Brevo HTTP API, in the reader's language (English, Arabic, French).
- **Password reset** by email from the sign-in page; the portal can send a reset email to any client user.
- **Member invites**: the client user (owner) invites by email; the invitee sets a name and password from the link. Only the
  `client` role manages members (unchanged rule).
- **STOP opt-out**: a contact who writes STOP (or the Arabic/French words) stops getting campaigns, reminders and template
  messages, gets one confirmation, and can write START to come back. Members see and change it on the contact page.
- **Contact data export and delete** (privacy requests) from the contact page and the portal.
- **Booking reminders** 24 h and 2 h before a booking, over WhatsApp (an approved template per language) or Telegram (text),
  in the contact's language.
- **Plans that match the pricing page**: a 7-day free trial, Inbox $39, + Bookings $59, Growth $99, AI Pro $169, Business $299,
  AI replies at $0.02, AI bots counted per channel, and paid add-ons (users, AI bots, channels, WhatsApp numbers).

### Where things stand (measured 2026-10-21)

| Area | Today | C's work |
|---|---|---|
| Email | No provider, no email anywhere | `IEmailProvider` + Brevo client, templates per language |
| Password | Identity with `AddDefaultTokenProviders`; DataProtection keys are not persisted, so Identity's tokens die on every deploy | Own one-time tokens in the database |
| Members | `MemberService.AddAsync`: the owner types the member's password; seats checked (`max_users`) | Invites replace it |
| Opt-out | `crm.contacts.marketing_opt_in` + `opted_out_at`; campaigns skip opted-out contacts; only the portal can change it | Keywords, confirmation, contact page, every business-started send checks it |
| Contact data | No export; no delete in the dashboard | Export (JSON) and delete |
| Reminders | None; `scheduling.bookings` has no reminder fields | Settings, worker, sent-log table |
| Plans | None seeded (made in the portal); periods `month`/`year` only; `ai_reply` is billed through rate cards; entitlement keys exist for users, channels, WhatsApp numbers; no add-ons | Seeded plans, trial, `max_ai_bots`, add-ons |
| Senders | WhatsApp (Meta) and Telegram only | Reminders use those two |

## 2. Decisions (my defaults; Mohammad asked me to proceed without waiting)

| # | Question | Default |
|---|---|---|
| D1 | Branch | New branch **`feature/go-live`** from `refactor/generic-platform` in both repos, so A+B stay deployable exactly as they are. Merge C after the A+B deploy (or deploy all three together if C is done first) |
| D2 | Commits | One commit per session once its checks pass. Never push |
| D3 | Email | Brevo transactional API (`POST /v3/smtp/email`). The key is a secret (`Brevo:ApiKey`, user-secrets / env, never a setting). Sender, switch and lifetimes are settings. With no key, emails are written to the log instead (dev and tests) |
| D4 | Tokens | Own table `identity.account_tokens` (purpose, SHA-256 hash, expiry, used time): survives deploys, single use. Invite 7 days, reset 60 minutes |
| D5 | Invites | Replace "add with a password". A pending invite holds a seat. Resend and revoke. An email that already has a user is refused (one user, one client, as today). The invitee lands signed in |
| D6 | Reset | "Forgot password?" on sign-in; same answer whether or not the email exists; at most 3 emails per address per hour; a reset signs out every other session (security stamp) and clears lockout. Admin users reset in the portal as today |
| D7 | STOP words | Whole message only (trimmed, case and accents ignored, end punctuation ignored): `STOP`, `STOPALL`, `UNSUBSCRIBE`, `ARRET`, `ARRÊT`, `DESABONNER`, `توقف`, `ايقاف`, `إيقاف`, `الغاء الاشتراك`. Back in: `START`, `UNSTOP`, `SUBSCRIBE`, `DEMARRER`, `اشتراك`. Both lists are settings. `CANCEL` is **not** a stop word (contacts use it for bookings) |
| D8 | What STOP blocks | Campaigns, reminders and template sends from the Inbox (refused with a clear message). A member can still reply inside a conversation the contact opens again. The AI is not asked about the STOP/START message itself. One confirmation goes out in the contact's language |
| D9 | Contact data | Export = one JSON file (contact, conversations, messages, bookings, orders, activity). Delete = permanent, with the contact's name typed to confirm; billing rows keep amounts with the contact link removed. **Only the `client` role** exports or deletes. The portal can do both |
| D10 | Reminders | Per client, off by default: two reminders (default 24 h and 2 h, each 0–168 h, 0 = off). WhatsApp needs an approved template per language chosen in settings (parameters: name, date and time in client time, business name); Telegram sends text from the strings file. Language: contact's stored language → client default. Skipped (with a reason) when the contact opted out, the booking isn't booked/confirmed, the booking was made inside the reminder window, the channel has no sender, or no template exists for the language (falls back to the client-default-language template) |
| D11 | Trial | `billing.plans.trial_days`: a trial plan runs one period of that many days at price 0 and then **ends** (expired) instead of renewing. `Billing:SignupPlanCode` default becomes `trial`. The portal moves the client to a paid plan |
| D12 | Plan seeds | New `Database/seed-billing.sql`, safe in production: inserts the six plans, their entitlements and an `ai_reply` rate of $0.02 on the default rate card only where missing, so portal edits survive re-runs |
| D13 | AI bots | One AI bot = one connected channel the AI answers on. **As built (S6):** no per-channel switch; the first `max_ai_bots` (plan + add-ons) connected channels in connection order get the AI, the others go to the team (`ai_bot_limit`), Return to AI is refused there and Business info names them. A plan without the key (made before C) has no AI bot limit |
| D14 | WhatsApp numbers | The app still connects one WhatsApp number per client. `max_whatsapp_numbers` keeps its meaning (the plans store 1/2/5); several numbers per client is a separate feature, not in C |
| D15 | Add-ons | Catalog `billing.addons` (key, name, monthly price) seeded at **extra user $8, extra channel $5, extra AI bot $15, extra WhatsApp number $10** (change them in the portal). Per client `billing.client_addons` (quantity). The plan limit + quantity is the limit. Adding charges the add-on's price from the wallet at once (no proration, like plan changes); each renewal charges plan + add-ons. Managed in the portal only (no payments in the dashboard yet) |
| D16 | Features the pricing page lists but the app doesn't have | Advanced reporting, API access, quick replies, customer reactivation, premium support: plans carry the entitlement flags, nothing new is built for them in C |
| D17 | Tester | On hold, as in A and B |

## 3. Design

### 3.1 Email (S1)

- `Business/Providers/Email/IEmailProvider` → `BrevoEmailProvider` (via `Business/HttpClients/Brevo/BrevoApiClient`) or
  `LogEmailProvider` when `Brevo:ApiKey` is empty. Returns the provider message id. 4xx → `ProviderRequestException`, 5xx/timeout →
  provider-unavailable (the page shows a generic "couldn't send" message).
- `Business/Services/Email/AccountEmailService`: builds each email (subject + HTML + text) from `en/ar/fr.json` keys `emails.*`
  in the reader's language (`users.language` → client default → the language of the page that asked), `dir="rtl"` for
  Arabic, one inline-styled layout, no external images.
- Settings: `Email:Enabled` (true), `Email:FromAddress` (`no-reply@omni.app`, set yours), `Email:FromName` (`Omni`),
  `Account:InviteDays` (7), `Account:ResetMinutes` (60), `Account:ResetEmailsPerHour` (3).

### 3.2 Account tokens (S1)

`identity.account_tokens`: `id`, `user_id` (nullable for invites), `client_id`, `purpose` (`invite` | `password_reset`),
`email`, `full_name`, `token_hash` (unique), `created_by_user_id`, `expires_at`, `used_at`, `revoked_at`, `created_at`. The link
carries the raw token (32 random bytes, base64url); only the hash is stored. Using a token: hash lookup, not used, not
revoked, not expired, then mark used in the same save as the password change.

### 3.3 Invites (S2)

- `POST /api/members/invites` `{ email, fullName? }`, `GET /api/members/invites`, `POST /api/members/invites/{id}/resend`,
  `DELETE /api/members/invites/{id}` (client role only; 403 otherwise). Seat check counts active users + open invites.
- `/Account/AcceptInvite?token=…`: name, password, confirm; creates the `member` user + active membership in one transaction,
  marks the invite used, signs in. A used/expired/revoked link shows a plain message and "ask for a new invite".
- `POST /api/members` (add with a password) is removed; the Members page shows "Invite member" and a pending list.

### 3.4 Opt-out (S3)

- `ContactOptOut` (static helper): keyword match per D7, from settings `OptOut:StopKeywords` / `OptOut:StartKeywords`.
- `MessageService.IngestAsync`: a customer message that matches sets/clears `marketing_opt_in` + `opted_out_at`, logs
  `contact_opted_out` / `contact_opted_in`, skips the AI for that message (`AiEligible = false`, trace step), and sends the
  confirmation (`messages.optOut.confirmed` / `messages.optOut.resumed` in the contact's language) as an outbound system text.
- Business-started sends refuse an opted-out contact: `SendTemplateAsync` (Inbox), campaigns (already), reminders.
- Contact page: an "Opted out of messages since …" badge and a switch (any member may change it; logged with the member).
- n8n: payload `contactOptedOut` (the AI never offers promotions to an opted-out contact); contract test + system-design §7.

### 3.5 Contact data (S4)

- `GET /api/contacts/{id}/export` → `contact-{name}-{date}.json`; `DELETE /api/contacts/{id}` with `{ confirmName }`.
- `ContactDataService` gathers through repositories (no new DbContext use). Delete: one transaction; FKs that would block
  (billing usage, campaign recipients, events) are set to null or cascade per their current rule; checked against every FK
  to `crm.contacts`. Event `contact_deleted` without personal data.
- Platform admin: `GET /api/platform-admin/contacts/{id}/export`, `DELETE /api/platform-admin/contacts/{id}` (answers
  `PlatformAdminChange`).

### 3.6 Reminders (S5)

- `scheduling.booking_settings` gains `reminder_first_hours` (24), `reminder_second_hours` (2), `reminders_enabled` (false),
  `reminder_templates` jsonb (`{ "en": templateId, "ar": …, "fr": … }`).
- `scheduling.booking_reminders`: `booking_id`, `kind` (`first` | `second`), `scheduled_start` (the booking start the reminder
  was for, so a reschedule sends again), `status` (`sent` | `skipped` | `failed`), `reason`, `message_id`, `created_at`;
  unique (`booking_id`, `kind`, `scheduled_start`).
- `BookingReminderWorker` every `Reminders:IntervalSeconds` (60): finds due bookings (start − offset ≤ now < start, booked or
  confirmed, client has bookings and reminders on), writes the row first (idempotent), then sends through
  `IMessageService` (template or text, origin `reminder`, billed like any message). Failures are recorded, never retried in
  a loop (one try per reminder, as campaigns).
- Booking detail shows "Reminder sent / skipped (why)". Settings live on the Availability / booking rules page.

### 3.7 Plans (S6)

| Plan (code) | Price | trial_days | Users | Channels | WhatsApp numbers | AI bots | Credit | Features |
|---|---|---|---|---|---|---|---|---|
| Free Trial (`trial`) | 0 | 7 | 1 | 1 | 1 | 0 | 0 | inbox |
| Inbox (`inbox`) | 39 | – | 2 | 2 | 1 | 0 | 0 | inbox |
| + Bookings (`bookings`) | 59 | – | 3 | 3 | 1 | 0 | 0 | + bookings |
| Growth (`growth`) | 99 | – | 5 | 4 | 1 | 0 | 10 | + campaigns |
| AI Pro (`ai-pro`) | 169 | – | 5 | 4 | 2 | 2 | 20 | + AI assistant, advanced reporting |
| Business (`business`) | 299 | – | 12 | unlimited | 5 | 5 | 40 | + API access |

- Ledger: new entry type `addon_charge` (≤ 0, wallet), posted only through `BillingLedger.PostAsync`. Add-ons are charged
  at add time and at each period start with the plan; a renewal that can't pay plan + add-ons goes `past_due` as today.
- Entitlements read `plan value + Σ add-on quantity` for limit keys (`unlimited` stays unlimited).
- `max_ai_bots`: checked when an Inbox setting turns the AI on for one more channel, and when the AI is asked for a
  channel (no bot on that channel → human mode, as when `ai_assistant` is off).
- The dashboard Billing page shows the plan, trial days left, limits with add-ons, and "Ask us for more" for add-ons.

## 4. Rules for every session

- Repo, branch: `feature/go-live` (D1). Read the repo's `CLAUDE.md` first. Never read `PROJECT_HANDOFF.md` or the `omni-*` HTML files.
- New UI text goes into `en.json`, `ar.json` and `fr.json` together (B's rules: glossary, plurals, no glued words).
- Docs in the same change: `system-design.html` (features, routes, schema, settings, n8n contract, change log, "Last
  verified"), `configuration.md`, `billing.md` (S6), `UI_GUIDE.md` when a pattern is new, `deploy-runbook.md` (S7).
- `schema.sql` changes stay re-runnable; a change to a table `copy-from-old.sql` copies updates the script and its tests.
- Progress in `milestone-c-progress.md` (workspace root, not in git). Test floor: 2,084 main app, 80 portal.

### Standard checks (end of every main-app session)

1. `dotnet build Omni.App.slnx`: 0 errors, no new warnings.
2. Throwaway Postgres (port 55433), `OMNI_TEST_DB`, full `dotnet test`: 0 failures, count ≥ the floor; guards green
   (LeftoverNames, UiTextKeys with languages, ServerMessageKeys, N8nContract, CopyScriptCoverage, ConfigSettings,
   DisplayFormat, RtlStyle).
3. Schema or seeds touched: `schema.sql` + `seed-config.sql` + `seed-billing.sql` + `seed.sql` + `seed-demo.sql` apply twice on an
   empty DB.
4. Published app in Staging on the scratch DB: touched pages in en, ar, fr at 1366 and 390: 200, no console errors, no raw
   keys, no horizontal scroll.
5. Docs and progress log written; `git status` shows no secrets and no `launchSettings.json`.

Portal sessions: build, `Omni.Admin.Tests` (≥ 80), leftover-names script, touched pages against the local main app.

## 5. Sessions

Order: **S1 → S2 → P1 → S3 → S4 → S5 → S6 → P2 → S7**. All on Opus (auth, privacy and money rules).

### S1 · Email and password reset (main app) · size M
Brevo client + provider + log provider, account tokens, `PasswordResetService`, settings, "Forgot password?" and
`/Account/ResetPassword`, rate limit, security-stamp sign-out, platform-admin `POST /api/platform-admin/members/{userId}/password-reset-email`.
**Acceptance:** a reset email (log provider) leads to a new password that works; the old cookie is signed out; a used,
expired or foreign token fails; the 4th request in an hour sends nothing but answers the same; Brevo request shape tested
with a fake handler.

### S2 · Member invites (main app) · size M
Invites API, AcceptInvite page, Members page (invite form, pending list, resend, revoke), seats with open invites,
platform-admin invites list + revoke. **Acceptance:** owner invites → link → member signs in with role `member`; a member
can't invite (403); seats full → refused; resend makes the old link invalid; revoked/expired links refused.

### P1 · Portal: invites and reset email · size S
Members page: pending invites with revoke, "Send password reset email" per client user. Copied shapes, `MAIN_APP_SYNC.md`.

### S3 · STOP opt-out (main app) · size M
Keyword helper + settings, ingest hook, confirmation, contact page badge and switch, Inbox template refusal. (The n8n
payload field was dropped in S3: the AI is never triggered for a STOP/START message, so n8n needs no change.) **Acceptance:** "stop", "STOP!", «توقف», "Arrêt" opt out and confirm in the contact's language; "please stop calling
me" does not; START opts back in; a campaign and an Inbox template skip/refuse the contact; the AI isn't triggered.

### S4 · Contact export and delete (main app) · size M
Service, APIs, contact page actions (client role), platform-admin endpoints. **Acceptance:** export holds every related
row; delete removes them and leaves billing totals unchanged; a member gets 403; wrong confirm name refused.

### S5 · Booking reminders (main app) · size M–L
Settings + UI, table, worker, sends, booking detail status. **Acceptance:** worker tests with a fake clock for each skip
rule, reschedule re-sends, two workers never double-send (unique row first), WhatsApp template per language with fallback,
Telegram text in Arabic.

### S6 · Plans, trial, AI bots, add-ons (main app) · size L
`trial_days`, trial expiry, `seed-billing.sql` (plan codes `trial`, `inbox`, `inbox-bookings`, `growth`, `ai-pro`, `business`), `Billing:SignupPlanCode` = `trial`, `max_ai_bots` by connection order (see D13),
add-on catalog, client add-ons, `addon_charge`, renewal with add-ons, Billing page, platform-admin add-on endpoints,
`billing.md`. **Acceptance:** a new signup gets the trial; it expires after 7 days with no charge; plan limits plus add-ons
apply; adding an add-on charges the wallet once; renewal charges plan + add-ons; ledger reconciles.

### P2 · Portal: contacts, plans, add-ons · size M
Contact details: opt-out shown, export and delete. Plans: `trial_days`, `max_ai_bots` label. Client billing: add-ons
(add, change quantity, remove). Add-on catalog page. **Acceptance:** build, tests, guard; each action against the local
main app.

### S7 · Final checks and summary · size S–M
Every check in both repos; full page walk in three languages; `deploy-runbook.md` (Brevo key and domain, `seed-billing.sql`,
`SignupPlanCode`, reminder templates in WhatsApp Manager, n8n re-import); `milestone-c-summary.md` for you.

## 6. What Mohammad does himself

1. **Brevo:** create the account, verify the sending domain (SPF, DKIM, DMARC), make an API key, set it as `Brevo__ApiKey` on
   Render, and set `Email:FromAddress` in the portal's Configuration page.
2. **WhatsApp Manager:** create a utility template for booking reminders in English, Arabic and French (body with 3
   parameters: name, date and time, business name) and pick them in each client's reminder settings once approved.
3. **Deploy:** after A+B (or merged into it), merge `feature/go-live`, run `schema.sql`, `seed-config.sql`,
   `seed-billing.sql` and set `Billing:SignupPlanCode` = `trial` (runbook section "Milestone C"). No n8n re-import.
4. **Prices:** check the seeded add-on prices (D15) and change them in the portal if you want others.
5. **Staging test:** reset a password, invite a member, send STOP from a phone, export and delete a test contact, book a
   slot 25 hours ahead and watch both reminders.

## 7. Risks

- **Email deliverability**: without SPF/DKIM, resets land in spam. The runbook makes the domain step explicit.
- **WhatsApp reminders need approved templates**; until then WhatsApp reminders are skipped with a reason the client sees.
- **Money**: add-on charges touch the ledger; covered by the billing reconciliation test and idempotency keys per period.
- **Deletes are permanent**: client role only, typed confirmation, logged without personal data.
