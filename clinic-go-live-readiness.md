# Is SculptFlow ready to go live for clinics?

Checked 2026-10-10 against `SculptFlowApp` `main` (refactor merged). Starts from the 30 Sep review
(`sculptflow-mvp-launch-readiness.html`) and re-checks each item in today's code. No code was changed. The n8n workflows,
Render, Supabase and Meta dashboards aren't visible from here, so items marked **can't verify** need you to check them.

## Verdict

**Not yet, but close for a hand-held pilot with one clinic.** Since 30 Sep several security blockers were fixed. What's
left is a short code list (about 1–2 weeks), some owner tasks on Render, Supabase and Meta, and two features a live
clinic will expect on day one: **appointment reminders** and **more than one staff login**. Open self-signup should
wait until the "before open signup" list is done.

## What changed since 30 Sep

| 30 Sep item | Now |
|---|---|
| Meta webhook signature | **Fixed**: `X-Hub-Signature-256` checked in constant time (`WhatsAppWebhookController.cs:130`) |
| Constant-time key compares | **Fixed**: ingest and platform-admin keys use `FixedTimeEquals` |
| Supabase public Data API exposing tables | **Likely fixed**: every table moved out of `public` into area schemas, which the Data API doesn't expose by default. Confirm in Supabase that no extra schemas were added to "Exposed schemas" |
| Tests | **Improved**: `PlasticSurgery.Tests` has 71 files. Still no CI (`.github/workflows` is empty) |
| Forwarded headers behind Render | **Half done**: `UseForwardedHeaders` is on (`Program.cs:156,363`), but DataProtection keys still aren't persisted, so logins drop on every deploy/restart and stored tokens can't be encrypted |
| `/api/ai/test/*` live in production | **Acceptable**: now behind the ingest key |

## Must fix before a real clinic uses it

1. **Close anonymous lead intake.** `POST /api/leads` is still `[AllowAnonymous]` and takes `ClinicId` from the body
   (`Controllers/Client/LeadsController.cs:32`); anyone can insert leads into any clinic. Add `[RequireIngestKey]` or remove it.
2. **Close or gate self-signup.** Register still creates a clinic for anyone, with no email check, CAPTCHA or rate limit
   (no `AddRateLimiter` anywhere). For a pilot, switch it off in production and create clinics from the admin portal.
3. **Persist DataProtection keys** (DB or Render disk), so logins and OAuth state survive deploys.
4. **Campaign opt-out.** Only the platform admin can opt a patient out (`LeadAdminService.cs:67`). There's no "STOP"
   handling and no clinic-side toggle, and inbound leads default to opted-in (`LeadService.cs:233,265`). WhatsApp policy
   requires honoring opt-outs. Either add STOP handling + a toggle, or hide Campaigns for the pilot.
5. **Scheduled campaigns never send.** No background job picks up `Scheduled` campaigns (`Business/Jobs` has none).
   Add a small worker or hide "Schedule".
6. **Secrets and production DB** (**can't verify**): rotate the keys that were in git history, use a production
   Supabase project separate from dev/testing, and turn on backups.
7. **AI flow end to end** (**can't verify**): move n8n to the new booking tools, then remove the deprecated
   `/api/ai/appointments/book|reschedule` (still present, `AiController.cs:214,267`). Run a scripted patient
   conversation: question → medical-question handoff → booking → reschedule → cancel.
8. **Meta app** (**can't verify**): app Live, business verified, Tech Provider access for Embedded Signup.
9. **Legal basics**: privacy policy, terms, data processing agreement with the clinic, and disclosure that AI replies go
   through OpenAI. Clinic chats are health data.

## Features a live clinic will expect

**Needed on day one**

| Feature | Today | Rough size |
|---|---|---|
| **Appointment reminders** (e.g. 24h and 2h before, WhatsApp template, confirm/cancel reply) | Not built: no reminder job. Only a "reminder" campaign type. Reminders are the main way clinics cut no-shows | ~3–5 days |
| **Multiple staff logins + password reset** | One login per clinic; "Invite staff" is disabled ("Coming soon"); no forgot-password or email sender | ~4–6 days (needs an email provider) |
| **Patient opt-out** | See must-fix 4 | ~1–2 days |
| **Delete or export a patient's data** | Nothing found. Patients can ask for it (GDPR and similar laws) | ~2–3 days |

**Should follow soon after**

| Feature | Today | Rough size |
|---|---|---|
| Instagram DMs | Not built (only allowed in the DB). Aesthetic clinics get many enquiries on Instagram | ~1–1.5 weeks |
| Conversation assignment + internal notes | Not built. Matters once a clinic has 2+ staff in the inbox | ~1 week |
| Basic roles (owner vs staff) | None by design | ~3–5 days |
| Calendar sync | Connect works; sync waits on an n8n workflow. Hide or label "coming soon" until done | n8n work |
| No-show follow-up and post-treatment check-in / review request | Not built; can reuse reminders + templates | ~2–3 days after reminders |
| Monitoring | No health check, error tracker or uptime alert; n8n failures are logged and swallowed | ~1–2 days |
| Encrypt stored channel and calendar tokens | Still plain text | ~1–2 days after keys are persisted |
| CI build + tests on push | None | ~0.5 day |
| Keep `launchSettings.json` out of git | Still tracked and locally modified with real keys | minutes (`skip-worktree` or user-secrets) |

## Suggested order

1. Code fixes: must-fix 1–5 + reminders + staff invites/password reset + opt-out → **about 2–3 weeks**.
2. Owner tasks in parallel: secrets, production DB and backups, Meta app, legal pages.
3. AI end-to-end run with a scripted conversation, then a pilot with one clinic.
4. "Should follow soon" list driven by what the pilot clinic asks for, Instagram first.
