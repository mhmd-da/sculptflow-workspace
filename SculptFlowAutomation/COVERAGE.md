# Coverage map

Rebuilt 2026-10-31 from the full QA pass of 30 Oct 2026 (rounds t0–t10 and the document review) against staging
(main app 4dd626d). **735 tests in 66 suites; 165 reproduce a QA finding** (they fail until the bug is fixed).
`dotnet run -- list --findings` prints them with their finding ids.

## Latest full run

31 Oct 2026, staging, `--tests all --allow external,ai,billing,shared` with every secret set: **1 h 29 min, 138 test
clients (all deleted afterwards).**

| | Tests |
| --- | --- |
| Regression tests (no finding) passed | 569 of 570 |
| Finding tests failing for their documented reason (bug still there) | 163 of 165 (95 distinct finding ids) |
| Unexpected results | 3, all fixed in the tests and re-run green/red as expected |

The three unexpected results were test problems, not changes in the app:

- `admin-data.overview` failed because more than 100 clients were created during the run, so the main test client dropped
  off "recent clients". It now checks a client registered right before the call.
- `inbox-opt-out.stop-twice` (t3-inbox-03) passed because the second confirmation's trace step hadn't been written when
  the test read the trace. It now waits until the trace stops growing; on re-runs it fails as it should.
- `signup.rate-limit` (t11-auto-01) passed once: the 2nd attempt got 429 from a counter shared with earlier attempts
  through the same proxy address. Re-runs and 53 extra attempts got no 429. The test now also requires the 429 to
  persist for 3 more attempts, which a limiter keyed on the visitor's IP would do.

## Suites

| Order | Suite | What it checks | Tests | QA findings |
| --- | --- | --- | --- | --- |
| 0 | health | target up, sign-up works, cleanup endpoint, database | 4 | 0 |
| 1 | signup | both business types, defaults, validation, duplicate email, Arabic, sign-up closed, per-IP limit | 11 | 1 |
| 2 | signin | wrong password, lockout, return URLs / open redirect, sign-out, cookie flags, Google pages without Google | 16 | 0 |
| 3 | password-reset | neutral answer, hourly cap, DB-inserted links: weak, mismatch, success, reuse, expired, clears lockout | 9 | 0 |
| 4 | api-security | signed-out access to all client API routes and pages, wrong keys, headers, error shapes | 10 | 2 |
| 5 | tenant-isolation | another client's ids on every resource type, SignalR isolation | 15 | 5 |
| 6 | page-render | every page for general and clinic, plan gates, unknown ids, signed-out pages | 8 | 1 |
| 10 | members | seat limit, invites, accept (DB token), dead links, cancel, resend, seats when the plan shrinks | 13 | 0 |
| 11 | member-roles | decision D5 (members can't manage members, everything else allowed), deactivate, live events after deactivation | 7 | 1 |
| 12 | business-info | details, name rules, input checks, HTML/bidi, default language, availability, special dates | 11 | 4 |
| 13 | client-settings | auto-reply, reminders and inbox-AI validation | 4 | 0 |
| 14 | language | Accept-Language, visitor picker, user language incl. API errors, Arabic RTL | 5 | 0 |
| 15 | business-type | clinic → general → clinic round trip (doc-review retest) | 2 | 0 |
| 20 | billing-plans | plans and the trial | 10 | 0 |
| 21 | billing-subs | subscribe, upgrade, downgrade (no proration, as documented), cancel, idempotency | 14 | 3 |
| 22 | billing-renewal | renewals, past due, trial expiry by time travel, the BillingMaintenance worker (waits ≤ 6 min) | 11 | 2 |
| 23 | billing-wallet | top-ups, adjustments, refunds, idempotency replay/conflict, concurrency, amounts | 22 | 5 |
| 24 | billing-addons | add-ons, seat limit, bot limit | 13 | 2 |
| 25 | billing-rates | rate cards (incl. client-specific), rates, quotes | 12 | 3 |
| 26 | billing-usage | AI reply usage, refunds, retries | 7 | 1 |
| 27 | billing-gating | plan gating of features, pages and APIs | 9 | 3 |
| 30 | ops-services | services CRUD and validation | 9 | 1 |
| 31 | ops-availability | rules, exceptions, timezone, notice, buffer, horizon, slots | 10 | 0 |
| 32 | ops-bookings | create, status, calendar, filters, the booking-rules gap (double, past, overlap, concurrent) | 22 | 12 |
| 33 | ops-reminders | settings and reminder rows | 4 | 1 |
| 34 | ops-calendar | calendar integrations without a real account, forged OAuth callbacks, sync callback | 12 | 2 |
| 35 | ops-campaigns | create, validation, audience, scheduling, cancel (nothing is ever sent) | 12 | 6 |
| 36 | ops-dashboard | dashboard and reports | 6 | 1 |
| 37 | ops-orders | orders: validation, amounts, currency, another client's booking | 8 | 5 |
| 40 | inbox-contacts | CRUD, stages, export/delete, search, paging, validation | 24 | 6 |
| 41 | inbox-conversations | list beyond 50, take-over, return-to-AI, close, reopen | 14 | 3 |
| 42 | inbox-messages | sending, the legacy add-message endpoint, long messages | 6 | 3 |
| 43 | inbox-ingest | message ingest with the ingest key | 10 | 1 |
| 44 | inbox-notifications | notifications and read state | 6 | 1 |
| 45 | inbox-telegram | fake connection: secret checks, every update type, /start, idempotency, the 15-message burst | 30 | 5 |
| 46 | inbox-opt-out | STOP/START words in en/ar | 12 | 3 |
| 47 | inbox-auto-reply | auto-replies for new and reopened conversations | 10 | 1 |
| 48 | inbox-telegram-replies | member replies to fake chats (clean failure, usage released), media | 8 | 0 |
| 50 | wa-ingest | the n8n ingest path: contacts, windows, statuses, contactId across clients | 31 | 14 |
| 51 | wa-webhook | signed Meta deliveries: handshake, new contacts, duplicates, old timestamps, bad events | 19 | 8 |
| 52 | wa-routing | events across clients sharing a WABA id, disconnected connections | 5 | 3 |
| 53 | wa-events | template and account events, health, bans | 18 | 9 |
| 54 | wa-templates | create, validation, edit/delete | 10 | 5 |
| 55 | wa-sending | the unverified connect, sending, disconnecting, campaigns with a rejected template | 10 | 5 |
| 56 | wa-webhook-messages | every message type, echoes, statuses | 16 | 2 |
| 60 | ai-bots | CRUD from templates, editor validation, activation, bot limit, selected sources | 24 | 1 |
| 61 | ai-panel | test chat: grounded answers, languages, handoff, injection, booking for a non-UTC client | 19 | 3 |
| 62 | ai-kb | knowledge entries, upload, settings, plan gating, websites and the SSRF guard, benchmark | 25 | 4 |
| 63 | ai-tools | /api/ai/*: auth, capabilities, client/bot isolation, slots, book, reschedule, cancel | 19 | 1 |
| 64 | ai-live | live AI over a fake Telegram: reply, charge, media, mid-run downgrade, block lift, languages, handoff, STOP | 16 | 7 |
| 70 | n8n | engine defaults/seed | 2 | 1 |
| 71 | n8n-run | each engine switched to n8n and back: AI reply, benchmark callback, calendar hand-off | 3 | 2 |
| 80 | admin-auth | key and admin sign-in, password change | 10 | 2 |
| 81 | admin-admins | create, deactivate, self rule | 7 | 0 |
| 82 | admin-clients | search, filters, edit, validation, deactivation lockout, inbox settings | 15 | 5 |
| 83 | admin-members | members | 6 | 0 |
| 84 | admin-data | contacts, conversations, overview, content | 10 | 1 |
| 85 | admin-btypes | business types | 5 | 0 |
| 86 | admin-settings | settings validation | 3 | 0 |
| 87 | admin-cache | keys, get, delete, tokens hidden | 4 | 1 |
| 88 | admin-monitor | logs, traces, timings, channels, benchmark, the automation endpoint | 7 | 0 |
| 90 | ui-signed-out | sign-in and sign-up in ar/fr | 2 | 0 |
| 91 | ui-general | 20 pages × en/ar/fr × desktop/iPhone: errors, RTL, overflow, raw keys, English leftovers, clinic words | 12 | 0 |
| 92 | ui-clinic | the same for a clinic client | 12 | 0 |
| 93 | ui-details | dir=auto names, old logo/icon, error reference, untranslated labels, mobile overflow | 8 | 7 |
| 100 | journey | sign-up → service and hours → contact → AI booking via tools → dashboard → order | 1 | 0 |

Two findings were first found while automating and have ids in the QA report: **t11-auto-01** (the per-IP sign-up
limit almost never triggers on staging: it keys on a changing proxy address) and **t11-auto-02** (a deactivated member's open Inbox keeps receiving live events).

## What isn't automated, and why

- **Real delivery to a person:** sending real WhatsApp/Telegram messages, campaign sending, WhatsApp billing settle/release
  (needs a real number), real email (Brevo not configured; invite and reset links are inserted in the database instead).
- **Real third-party accounts:** Google/Outlook OAuth and a successful calendar sync, Google sign-in.
- **Deploy-dependent:** sessions and OAuth state across a deploy (the data-protection keys finding), work lost on restart.
- **Timing-dependent or costly:** out-of-order answers in AI bursts and burst errors (t7-06, t4-ai-18), the platform-wide AI
  run cap (t7-05), 100-page crawls, a 300k-character entry, concurrent benchmark runs.
- **Safety:** concurrent double sign-up (could orphan a client), the last-admin rule (would need deactivating a real admin),
  a running campaign worker (nothing may be sent).
- **Not a checkable expectation:** missing features, enhancements and UX opinions; findings the document review marked
  by-design or planned (asserted as documented where it matters, e.g. decision D5, no proration, silent handoff).
- **Admin portal pages themselves** (Omni.Admin cookies, layout, audit page): only the main app's platform-admin API is
  tested here; the portal has its own test project.

Run once each to confirm and kept: the 15-message Telegram burst (t3-inbox-02, Risk.Shared) and the campaign with a rejected
template (t8-11, Risk.Billing).
