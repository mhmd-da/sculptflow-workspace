# Coverage map

Checked 2026-10-13 against the main app working tree (`refactor/remove-infobip`, uncommitted), on a throwaway database.

## Numbers

- **API routes:** the main app has 231. Every one has at least one test. Locally, 228 were called in a run. The other 3
  (knowledge search, knowledge activate, website pages) only run once OpenAI embeddings are configured.
- **Pages:** every page renders, and detail pages with unknown ids are checked for server errors.
- **Tests:** 178 in 36 suites.
  - A default run (no opt-ins) gave 155 passed, 8 failed and 15 skipped. The 15 skipped are opt-in.
  - With every opt-in allowed, all 178 ran. The only extra failures were the 8 tests that need OpenAI, because it
    isn't configured locally.

## Real problems the tests found (main app)

Fixed 2026-10-13 (uncommitted, main app working tree): all of these except `admin-clinics.deactivate`. A re-run against the
fixed code gave 167 passed, 1 failed (deactivate), 10 skipped (opt-in).


| Test | What happens | Should be |
| --- | --- | --- |
| `edge.negative-skip` | `?skip=-5` on every list (leads, appointments, bookings, conversations, notifications, dashboard, WhatsApp health) gives a 500 (Postgres "OFFSET must not be negative") | Clamp skip to ≥ 0 |
| `edge.lead-unknown-clinic` | Public lead intake with a clinic id that doesn't exist gives a 500 (foreign key violation) | 400/404 |
| `edge.conversation-unknown-lead` | Opening a conversation for a lead that doesn't exist gives a 500 | 400/404 |
| `edge.message-sender` | Logging a message with an unknown `senderType` gives a 500 (check constraint), while an unknown `direction` correctly gives 400 | 400 |
| `admin-clinics.deactivate` | A clinic deactivated from the admin portal keeps working: its signed-in staff can still use the API and can sign in | Locked out until reactivated (known open item) |
| `edge.lead-empty` | Lead intake with no name, phone or email creates an empty lead | 400 |
| `edge.template-name` | A WhatsApp template named "Bad Name!" is saved as a draft; Meta only accepts lower-case letters, digits and underscores, so it fails later at submission | 400 up front |
| `edge.channel-unknown` | `POST /api/channel-integrations/pigeon/disconnect` answers 204 | 400 for an unknown channel type |

The first four are server errors on bad input. They'd show up as 500s in logs and to anyone calling the API wrongly.

## What each area checks

| Area | Scenarios |
| --- | --- |
| Sign-up, sign-in, security | Wrong password, duplicate email, sign-out, signed-out access to every API, wrong ingest/admin keys, one clinic reading or writing another's data |
| Pages | Every page; detail pages with unknown ids don't 500 |
| Leads, procedures, appointments, bookings | Create, edit, status flows, filters, invalid statuses, unknown ids, idempotent intake |
| Opening hours and slots | Saving hours, slots per range and per day |
| Inbox | Messages, read, take-over, return to AI, close, reopen (manual and on a new patient message), duplicate inbound message, n8n ingest, staff send with no channel, bad bodies, unknown media |
| AI agent tools | Slots, booking, double booking, taken slot, closed hours, past times, unknown lead, schedule, reschedule, cancel with nothing booked, hand-off, human-mode guard, every AI test code |
| Knowledge and benchmark | Settings validation, entries, upload, websites (duplicate, private address, invalid mode, deactivate, rescrape), benchmark cases, runs, generations and unknown ids (through the platform-admin benchmark API; staff get 404) |
| Campaigns, templates, channels, calendar | Unknown ids, validation, fake provider credentials rejected, calendar actions without a connection, connect redirects and forged OAuth callbacks |
| Webhooks and callbacks | Unsigned or wrongly signed Meta deliveries refused, a signed one accepted, Telegram unknown connection, template/health events, calendar sync and benchmark callbacks |
| Admin portal (every `/api/platform-admin` route) | Clinics (search, filters, edit, timezone validation, deactivate), staff (lock, membership, password reset, email confirmed), leads/conversations/messages/appointments, content, channels, overview, settings (validation only), cache, billing (top-up, adjustments, subscription lifecycle, clinic-only rate cards, idempotency, reconciliation) |
| End to end | Lead to message, AI booking, attended, quote, deposit, surgery booked, dashboard |

## Still not covered end to end

These need real provider accounts, or a fake-provider mode in the main app (a main-app change):

- Sending a real WhatsApp, Telegram or Facebook message, and campaign sending.
- A connected WhatsApp, Telegram or calendar account with real inbound traffic and syncing.
- The n8n AI reply itself.
- Completing a Google or TikTok sign-in.

Time-based background jobs (renewals, expiry, scrape and benchmark workers) are covered by the main app's own unit
tests, not here.
