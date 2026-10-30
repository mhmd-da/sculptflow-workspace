# Generic platform plan (v2): rename, business types, languages

Updated 2026-10-16 with your decisions. Checked against `main` in `SculptFlowApp` (60a4785) and `SculptFlowAdmin` (fa33015),
both clean, plus the n8n exports in `docs/n8n/`. Sizes are my estimates, not measured.

## Your decisions, with my notes

| # | Decision | My note |
|---|---|---|
| 1 | Customers | **`contact`**. It's the word generic omnichannel tools use: it covers prospects, buyers and campaign recipients. UI default "Contacts", clinic label "Patients" |
| 2 | Staff | **users + client memberships + teams later** (design below). UI says "Members" |
| 3 | `service` everywhere | DB `core.services`, entity `Service`. To avoid clashing with the `Business/Services` layer, the feature folder is **`Catalog`** in every layer: `CatalogService : ICatalogService`, `ServiceRepository`, `ServicesController` at `/api/services`. Never `ServiceService` |
| 4 | Appointment → **booking** | **Agree.** It covers appointments, consultations, classes, reservations and viewings. Clinics still see "Consultation" through labels |
| 5 | Procedure booking → **order** | **Agree.** It's a contact buying a service: quoted → deposit paid → confirmed → completed / canceled. If shop orders (e.g. Shopify) come later, they fit the same word |
| 6 | Stages by business type | Design below. This replaces "won" |
| 7 | Business types in a DB table, edited in the admin portal | As planned |
| 8 | `clinic` + `general` | As planned |
| 9 | Fresh Supabase + data migration script | Design below |
| 10 | New generic name | Options below |
| 11 | Update the automation tester directly on `master` | OK. It has 17 uncommitted changes today; I'll leave those as they are |
| 12 | n8n exports in `docs/n8n/` | Found 3 workflows at the **workspace root** (not inside a git repo). **The AI Agent export has the real `X-Ingest-Key` typed into a header.** Move them into `SculptFlowApp/docs/n8n/` on the branch, swap the key for an n8n credential before committing, and rotate the key, since it now sits in a plain file |

### What "won" meant

"Won" is sales jargon for "the deal closed: the person became a paying customer". You're right that it reads badly. With
stages per business type, nobody sees that word. Each stage gets a **category** that the code, reports and AI use, and a
label each business sees:

| Category (internal) | Clinic stages | General stages |
|---|---|---|
| `open` | New, Contacted, Qualified | New, Contacted, Qualified |
| `booked` | Consultation booked | Booked |
| `attended` | Consultation attended | Attended |
| `no_show` | No-show | No-show |
| `converted` | Procedure booked | Customer |
| `closed_lost` | Not interested, Lost | Not interested, Lost |
| `needs_human` | Needs human, Medical question | Needs human |

## Design

### Users, members and teams (decision 2)

- **`identity.users`** (today `identity_users`): every person who logs in. Contacts never log in.
- **`identity.client_users`** (today `clinic_users`): user ↔ client with a **role**: `owner`, `admin`, `member`.
  Signup creates the client **and** its first user as `owner`; that is the "client has a user". The admin portal creates
  the owner the same way. Keep "one active client per user" for now, but make it a rule rather than the unique index, so
  agencies with several clients can come later.
- **Teams later**: `identity.teams` (client_id, name) + `identity.team_users`. Conversations get `assigned_user_id` /
  `assigned_team_id` when assignment is built. No team work now, but nothing blocks it.
- Seats: entitlement `max_agents` ("Staff seats") → `max_users` ("Seats"). The AI becomes **AI assistant** (`ai_agent` →
  `ai_assistant`), so no word means both a person and the AI.
- Platform admins stay in the portal's own `admin.admin_users`, separate from client users.

### Business types and stages (decisions 6–8)

- `core.business_types`: `code` (pk), `name` (per language), `is_active`, `terminology` (per language: contact/contacts,
  member/members, service/services, booking/bookings, order/orders, client), `ai_profile` (prompt text), `sort_order`.
- `crm.pipeline_stages`: `business_type_code`, `key`, `category`, `labels` (en/ar/fr), `sort_order`. Contacts store
  `stage_key`; code and AI read the `category`.
- `core.clients.business_type_code` is chosen on Register, on Google signup completion and on client create in the
  portal. It can only change through `PUT /api/platform-admin/clients/{id}/business-type`. On a change, contacts move to
  the first stage of the new type with the same category. The portal shows a preview of the counts first.

### Multi-language: English, Arabic, French

**In scope:** the client dashboard (Razor pages, JS, server error messages, notifications), business-type terminology
and stage labels, and AI replies. **Out of scope for now:** the admin portal stays English (it's internal); clients' own
content (service names, knowledge base) stays in whatever language they write it.

- **Strings**: one JSON file per language (`Resources/i18n/en.json`, `ar.json`, `fr.json`) with keys like
  `inbox.empty.title`. It's read by an `IStringLocalizer` on the server and by the same JSON in JS. Terminology goes in as
  placeholders (`"Add {contact}"`) filled per business type and language. Arabic grammar (gender, dual and plural) breaks
  naive word-swapping, so Arabic strings avoid gluing a term into a sentence. Terminology entries carry singular and
  plural forms.
- **Footprint**: about 840 text nodes and 165 placeholder/title/aria texts in 41 pages, JS strings in 20 files, and about
  190 user-facing server messages, so **~1,300 strings**.
- **Language choice**: the user's own setting (`users.language`), else the client's default (`clients.default_language`),
  else the browser. There's a switcher in the header.
- **Arabic right-to-left**: `<html lang dir>` per request; ~110 CSS rules use left/right and move to logical properties
  (`margin-inline-start`); arrows and icons get mirrored; phone numbers and times are wrapped as left-to-right; an Arabic
  web font is added; the calendar and charts need checking.
- **Formatting**: dates and numbers follow the culture; Arabic uses Latin digits by default (decision below).
- **AI**: replies in the contact's language (`contacts.preferred_language`, else detected); `ai_profile` stays English
  with that rule. WhatsApp templates are already per language.
- **French** adds no new mechanics (no RTL), only a translation file, so I'd keep it: ~1–2 days of drafting + your
  review.
- **Translations**: I draft Arabic and French; a native speaker reviews before go-live.

**Why now:** the rename and the terminology pass already touch every one of those ~1,300 strings. Moving them into the
language files in the same pass means each string is touched once. Doing languages afterwards would redo that whole pass.

### Database and data migration (decision 9)

1. `Database/schema.sql`, `seed.sql`, `seed-config.sql` and `seed-demo.sql` are rewritten with the new names. Business
   types, stages and terminology go into the seed.
2. A fresh Supabase project is created from them (that's your step; I don't connect to Supabase).
3. **`Database/migrations/2026-10-generic/copy-from-old.sql`** runs on the new project. It imports the old project's
   schemas through `postgres_fdw`, then copies **every table** (empty ones copy nothing) with the column renames. It also
   maps old lead statuses to clinic stage keys, renames stored event types and config keys, and sets the existing clinics
   to business type `clinic`. Finally it prints old vs new row counts per table.
4. I test it locally on the throwaway Postgres (old schema from `main` + demo data → new schema) before you run it.

### Name (decision 10)

I didn't check trademarks or domains for these:

| Name | Idea |
|---|---|
| **Convora** | conversations + forum; short, works in Arabic and French |
| Relaya | relay: messages passing between a business and its contacts |
| Omnora | omnichannel, soft ending |
| Linqo | link/linking customers and businesses |
| Replyo | about replying, fast and friendly |

My pick is **Convora**. The code becomes `Convora.App`, `Convora.App.Tests` and `Convora.Admin`. The repo folder names can
stay or change separately.

## Other features: along the way or after?

Rule: **build it along the way if it changes tables or code the rename is reshaping anyway; otherwise after.**

| Feature | Status today | When |
|---|---|---|
| Members, roles, invites, password reset (needs an email provider) | Missing; one login per clinic | **Along**: it's decision 2 |
| Contact opt-out + "STOP" keyword | Missing (admin-only toggle) | **Along**: contacts + inbound messages |
| Delete / export a contact's data | Missing | **Along**: contacts |
| Booking reminders (24h / 2h, WhatsApp template per language) | Missing | **Along**: bookings + languages |
| Lead intake auth, signup gate + rate limit, campaign sender | **Fixed since 10 Oct** | — |
| Persist DataProtection keys, encrypt stored tokens | Missing | After (small, independent) |
| Health check + uptime/error alerts, CI build on push | Missing | After |
| Instagram/Messenger, assignment + notes + teams, public API/webhooks | Missing | After, on the new names |

## Order of work, in three milestones

Each milestone merges into `main`, so the branch doesn't live for ten weeks.

| Milestone | Steps | Rough size |
|---|---|---|
| **A: Rename + business types** (`refactor/generic-platform`, app + portal) | New schema and seeds, business types and stages, mechanical rename (namespace, code, routes, JS, tests, SignalR, cache keys, events, config); **every UI string moved into `en.json` with terminology placeholders while it's touched**; users/roles; n8n contracts and workflows; portal rename + business types page; data migration script; tester; docs | ~5–6 weeks |
| **B: Arabic + French** (`feature/i18n`) | Arabic and French files, RTL CSS, language switcher, formatting, AI language rule, native review | ~2 weeks |
| **C: Go-live features** (`feature/go-live`) | Invites + password reset + email provider, opt-out + STOP, contact delete/export, booking reminders | ~2–2.5 weeks |

**Total ~9–11 weeks.** Milestone A is the big one. I'd plan each step on Opus and implement on Sonnet, one session per
step. Feature work on `main` should stay small during Milestone A, because nearly every file changes.

## Risks

- **Long branch, many files**: keep Milestone A focused and merge `main` in often.
- **Missed names**: add a test that fails if `clinic`, `lead`, `procedure`, `appointment` or `staff` appear in tracked code
  outside an allowlist (clinic labels, seeds, migration script, docs history).
- **Arabic sentences built from terms**: review every Arabic string in context, not as a list.
- **n8n cutover**: the AI is down until the workflows match; run a scripted conversation in each language before merging.
- **The ingest key in the n8n export**: rotate it and keep it out of git.

## Final decisions (2026-10-16)

1. **Name: Omni** for code and UI until a trademark is chosen: `Omni.App`, `Omni.App.Tests`, `Omni.Admin`; the UI says
   "Omni". Confirmed 2026-10-16: rename **everything** (solutions, projects, folders, Docker/config names, cookie names,
   docs) to Omni; Mohammad fixes deployment (Render, GitHub repo names, remotes) afterwards. Renaming the GitHub repos
   themselves is his step.
2. **Language**: per user, with a client default.
3. **Arabic digits**: Latin 0-9.
4. **Admin portal**: English only, permanently.
5. **Email: Brevo.** It offers SMTP and an HTTP API. Plan: an `IEmailProvider` with a Brevo API implementation in
   `Business/Providers/Email` + `Business/HttpClients/Brevo` (fits the repo's provider pattern, returns message ids, and
   delivery webhooks can come later). You provide the API key and verify the sending domain (SPF/DKIM) in Brevo.
6. **Roles: `admin`, `client`, `member`**; `reseller` later (a user linked to a group of clients), not now.
7. **Milestones**: three merges, A → B → C.

### One users table for everyone

`admin.admin_users` merges into `identity.users`:

- `identity.users.role`: `admin` | `client` | `member` (later `reseller`). `identity.client_users` links `client` and
  `member` users to their client; admins have no client.
- **Main app login** accepts only `client` and `member`. **Admin portal login** accepts only `admin`. Separate cookies;
  both sides check the role on every request, not only at login.
- The portal keeps its rule of no main-table access: it logs in through a new main-app endpoint
  `POST /api/platform-admin/auth/login` (behind the platform-admin key), which checks the password, lockout and role and
  returns the admin's id, name and email. The portal then issues its own cookie. Admin user management (list, create,
  deactivate, reset password) moves to `/api/platform-admin/admins`.
- `admin.admin_audit_log.admin_user_id` points at `identity.users.id`. `admin.admin_users` is dropped.
- Both apps already use Identity's `PasswordHasher` (PBKDF2), so the data copy moves admin password hashes as they are;
  no admin needs a reset. Lockout (`failed_logins`, `locked_until`) maps onto Identity's lockout fields.
- Suggested follow-up: two-factor sign-in for `admin` users, since they can see every client.

### AI reply language

The AI replies in the language the contact writes in, as the workflow does today. The client's or user's UI language
never changes that. What languages change around the AI:

- **Tool results** (service names, stage labels, client info) are in whatever language the client typed them; the AI
  translates when it answers.
- **Messages sent first, before the contact has written** (booking reminders, campaign templates) use the contact's last
  detected language (`contacts.preferred_language`), and fall back to the client's default language.

## Models and effort

- The detailed step-by-step plan for Milestone A: **Opus, high effort**.
- Schema + data migration, users/roles merge and portal login, n8n contracts: **Opus, high effort**.
- Mechanical rename, page/JS string extraction, portal pages, tester: **Opus, medium effort** (no Sonnet for Milestone A,
  given the "100% clean" bar).
- Every step ends with a build, the full test suite, and the leftover-names test.
