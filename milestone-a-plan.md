# Milestone A: execution plan (rename to Omni, business types, users/roles, n8n, portal, tester, data copy)

Written 2026-10-16 from `generic-rename-plan.md` (v2 + "Final decisions"). Checked against `SculptFlowApp` `main` 60a4785,
`SculptFlowAdmin` `main` fa33015 (both clean) and `SculptFlowAutomation` `master` 7cb345e (17 uncommitted changes), plus
the three n8n exports in `docs/n8n/`. Counts below are `git grep -i` hits measured today; sizes are my estimates.

Planning only: nothing has been changed, committed or branched yet.

## 1. What Milestone A delivers

Both apps renamed to **Omni** with generic names in code, DB, routes, JS, tests, config and docs; business types
(`clinic`, `general`) with per-type terminology and pipeline stages; every dashboard UI string moved into
`Resources/i18n/en.json` with terminology placeholders; one `identity.users` table with roles `admin` / `client` /
`member` (portal logs in through the main app); n8n contracts and workflows updated; portal and automation tester in
step; a tested `copy-from-old.sql` that moves the old Supabase data into a fresh project. Arabic/French (B) and go-live
features (C) are out of scope.

### How big the rename is (old words in tracked files, today)

| Word | Main app | Portal | Tester |
|---|---|---|---|
| clinic | 611 files / 10,912 hits | 194 / 1,321 | 19 / 378 |
| lead | 231 / 3,883 | 58 / 393 | 8 / 218 |
| appointment | 161 / 2,323 | 25 / 90 | 7 / 62 |
| procedure | 149 / 1,775 | 24 / 73 | 8 / 98 |
| staff | 147 / 667 | 39 / 204 | 7 / 30 |
| patient | 67 / 214 | 7 / 9 | 4 / 12 |
| PlasticSurgery | 1,009 / 5,792 | 3 / 4 | 0 |
| SculptFlow | 116 / 412 | 364 / 930 | 23 / 68 |

Main app: 1,055 tracked files, 49 tables, ~1,530 test cases (994 `[Fact]`/`[Theory]`). n8n AI Agent workflow: 49
"clinic", 101 "appointment", 61 "patient" mentions.

## 2. Decisions I need from Mohammad before Session 1

**Confirmed 2026-10-16: Mohammad accepted every default below ("defaults ok").** So each session may commit (never
push) on its branch once its checks pass. Later the same day Mohammad said "nevermind on the tester app": D2 is
dropped and the tester is left out of Milestone A (T1 on hold) until he brings it back.

| # | Question | My default |
|---|---|---|
| D1 | May each session **commit (not push)** its work on `refactor/generic-platform` once its checks pass? Twelve sessions of uncommitted work on one branch is fragile | **Yes**, one commit per session, you push |
| D2 | The tester's 17 uncommitted changes touch the same files the rename edits (`Suites/ClinicSuites.cs`, `Core/*`, `wwwroot/app.js`…). Commit them before T1? | **You commit them first**, so the rename diff is separate. Otherwise T1 edits on top and never discards them |
| D3 | `procedure_bookings` → `orders`: which schema? | **`crm.orders`** (a sale, not a calendar slot) |
| D4 | Lead `qualification_status` (unknown/hot/warm/cold/needs_human/medical_question/spam) next to the new stages | Keep as **`contacts.qualification`** with generic values (unknown, hot, warm, cold, needs_human, spam). `medical_question` becomes a clinic **stage** (category `needs_human`) |
| D5 | Who may add/remove members in a client? | Only role **`client`**; `member` users can use everything else |
| D6 | Local checkout folders `SculptFlowApp` / `SculptFlowAdmin` / `SculptFlowAutomation` | **Keep** during A (workspace CLAUDE.md and memory point at them). You rename them with the GitHub repos after merge, then I update CLAUDE.md + memory |
| D7 | Tester project name | Rename to **`Omni.Automation`** (`git mv` keeps your uncommitted edits) |
| D8 | `PROJECT_HANDOFF.md` (old notes, mentions an older "Omni") | **Leave untouched**, allowlisted in the leftover-names test |
| D9 | `UserSecretsId` in the three `.csproj` files | **Keep the same ids**, so your user-secrets keep working |

## 3. Rename map (single source of truth for every session)

Sessions follow this table; any name not listed is decided in the session and added here (and in the progress log).

### Products, solution, config

| Old | New |
|---|---|
| `PlasticSurgery.slnx`, `PlasticSurgery/`, `PlasticSurgery.Tests/`, namespace `PlasticSurgery.*` | `Omni.App.slnx`, `Omni.App/`, `Omni.App.Tests/`, `Omni.App.*` |
| `SculptFlowAdmin.slnx`, `SculptFlowAdmin/`, namespace `SculptFlowAdmin.*` | `Omni.Admin.slnx`, `Omni.Admin/`, `Omni.Admin.*` |
| `SculptFlowAutomation` project + namespace | `Omni.Automation` (D7) |
| UI brand "SculptFlow", page titles, emails, `_comment`s | "Omni" |
| Dockerfiles `PlasticSurgery.dll` / `SculptFlowAdmin.dll` | `Omni.App.dll` / `Omni.Admin.dll` |
| Cookies: Identity default, `sf-tz`, portal auth cookie | `omni.auth`, `omni-tz`, `omni.admin` (`admin.scope` stays) |
| `SCULPTFLOW_TEST_DB`, `SCULPTFLOW_TEST_DB_ALLOW_REMOTE` | `OMNI_TEST_DB`, `OMNI_TEST_DB_ALLOW_REMOTE` |
| Tester email domain `@sculptflow-automation.test`, `AutomationClinics` | `@omni-automation.test`, `AutomationClients` (`[Automation]` prefix stays) |

### Tables and columns (schemas stay as they are)

| Old | New |
|---|---|
| `core.clinics` | `core.clients` (+ `business_type_code`, `default_language`) |
| — | `core.business_types`, `crm.pipeline_stages` (new) |
| `core.procedures` | `core.services` |
| `crm.leads` | `crm.contacts` (`status` → `stage_key`, `qualification_status` → `qualification`, + `preferred_language`) |
| `scheduling.appointments` | `scheduling.bookings` |
| `scheduling.procedure_bookings` | `crm.orders` (D3) |
| `scheduling.clinic_availability_rules` / `_exceptions` | `scheduling.availability_rules` / `availability_exceptions` |
| `scheduling.clinic_booking_settings` | `scheduling.booking_settings` |
| `scheduling.appointment_calendar_syncs` | `scheduling.booking_calendar_syncs` |
| `identity.identity_users` / `_claims` / `_logins` / `_tokens` | `identity.users` (+ `role`, `full_name`, `language`, `is_active`, `last_login_at`, `created_at`, `updated_at`) / `user_claims` / `user_logins` / `user_tokens` |
| `identity.clinic_users` | `identity.client_users` (one active row per user stays a **rule in code**, not the unique index) |
| `admin.admin_users` | dropped, rows move into `identity.users` with role `admin` |
| `admin.admin_audit_log.admin_user_id` (uuid), `clinic_id` | `user_id` (text → `identity.users.id`), `client_id` |
| Columns `clinic_id`, `lead_id`, `procedure_id`, `appointment_id`, `procedure_booking_id` | `client_id`, `contact_id`, `service_id`, `booking_id`, `order_id` |
| Constraint/index names containing old words | renamed the same way |

### Code (feature folders in every layer)

| Old | New |
|---|---|
| `Clinic`, `ClinicUser`, `ClinicContext` / `CurrentClinicContext`, folder `Clinics` | `Client`, `ClientUser`, `ClientContext` / `CurrentClientContext`, `Clients` |
| `Lead`, folder `Leads` | `Contact`, `Contacts` |
| `Procedure`, folder `Procedures` | `Service`, folder **`Catalog`** (`CatalogService : ICatalogService`, `ServiceRepository`, `ServicesController`) |
| `ProcedureBooking`, folder in Appointments/Procedures | `Order`, `Orders` |
| `Appointment`, folder `Appointments` | `Booking`, `Bookings` |
| `IdentityUser` (plain) | `AppUser : IdentityUser` (avoids clashing with `ControllerBase.User`) |
| Staff (`StaffController`, `StaffClaims`, folder `Staff`) | Members (`MembersController`, `MemberClaims`, `Members`) |

### Routes, events, vocabularies

| Old | New |
|---|---|
| `/api/leads`, `/api/procedures`, `/api/procedure-bookings`, `/api/appointments`, `/api/staff` | `/api/contacts`, `/api/services`, `/api/orders`, `/api/bookings`, `/api/members` |
| Pages `/Procedures`, `/Staff`, `/Settings/ClinicInfo` | `/Services`, `/Members`, `/Settings/BusinessInfo` |
| `/api/platform-admin/clinics`, `/staff`, `/leads`, `/automation/clinics` | `/clients`, `/members`, `/contacts`, `/automation/clients` |
| `/api/ai/clinic-info`, `/procedures`, `/leads/{id}`, `/appointments/*` | `/api/ai/client-info`, `/services`, `/contacts/{id}`, `/bookings/*` |
| Webhook / OAuth paths without old words (`/api/integrations/*`, `/calendar-oauth`, `/tiktok-oauth`, `/signin-google`) | **unchanged** (provider consoles keep working) |
| `MessageSenderType` `lead`, `staff` | `contact`, `member` |
| Lead status values | clinic stage keys keep the old values (`consultation_booked`…) as **data** in the clinic seed; code only uses **categories** |
| Event types `lead_*`, `consultation_*`, `procedure_booked`, `customer_message_received`, `staff_message_sent_*` | `contact_*`, `booking_*` (spelling `canceled`), `order_confirmed`, `contact_message_received`, `member_message_sent_*` |
| SignalR `AppointmentChanged`, groups `clinic:{id}` | `BookingChanged`, `client:{id}` |
| Cache keys `core:clinic:*`, `booking:*`… | `core:client:*`, `scheduling:booking-settings:*`… (all in `CacheKeys`) |
| Entitlements `max_agents`, `ai_agent` | `max_users` ("Seats"), `ai_assistant` |
| Config keys / sections / env vars with old words | renamed; S1 lists every one for Render in the progress log |
| S5: events `consultation_booked/attended/no_show/rescheduled/cancelled`, notifications `APPOINTMENT_*` | `booking_created/attended/no_show/rescheduled/canceled`, `BOOKING_CREATED/RESCHEDULED/CANCELED` |
| S5: AI tools `schedule_/book_/reschedule_/cancel_consultation`, `get_my_appointments` | `schedule_booking`, `book_slot`, `reschedule_booking`, `cancel_booking`, `get_my_bookings` |
| S5: `appointment_type` (default `consultation`), `consultation_info`, `consultation_duration`, `default_consultation_duration_minutes`, audience `reactivation_no_consultation`, knowledge category `consultation` | `booking_type` (default `standard`), `booking_info`, `booking_duration`, `default_booking_duration_minutes`, `reactivation_no_booking`, `booking` |
| S6: `external_lead_id`, `LeadQualificationStatus`, `UpdateLeadStatusRequest`, `POST /api/leads/{id}/status`, list filter `?status=`, `PatientBookingContext` | `external_contact_id`, `ContactQualification`, `UpdateContactStageRequest {stageKey, qualification, source}`, `POST /api/contacts/{id}/stage`, `?stage=`, `ContactBookingContext` |
| S6: events `lead_created`, `lead_contacted` + `lead_qualified`, `customer_message_received`; notification `NEW_LEAD`; AI result `LEAD_NOT_FOUND`; tools `get_lead_context`, `update_lead` | `contact_created`, `contact_stage_changed` (metadata `{from, to}`), `contact_message_received`; `NEW_CONTACT`; `CONTACT_NOT_FOUND`; `get_contact_context`, `update_contact` (+ `stage`: key or category) |
| S6: config `Dashboard:LeadsPageSize`; audience filter JSON `leadStatuses`, `qualificationStatuses`; dashboard `SurgeriesBooked` | `Dashboard:ContactsPageSize`; `stageKeys`, `qualifications`; `OrdersMade` (JSON `ordersMade`) |
| S9: n8n nodes `search_clinic_knowledge`, `get_my_appointments`, `schedule_consultation`, `cancel_consultation`, `new_appointment`, `sculptflow_event_handler`; AI decision `appointmentId`, `procedureId` | `search_knowledge`, `get_my_bookings`, `schedule_booking` (not `create_booking`: it books or moves), `cancel_booking`, `booking_change`, `sync_callback`; `bookingId`, `serviceId` |

## 4. Rules for every session

- **Repo, branch**: main app and portal on `refactor/generic-platform` (created in S1 / P1 from `main`); tester on
  `master`. Read the repo's `CLAUDE.md` first. Never read `PROJECT_HANDOFF.md` or `omni-*` / `new-omni-*` HTML files.
- **Commits**: only per D1. Never stage `launchSettings.json`; never push.
- **One writer per repo**: sessions in the same repo run one after another. Only different repos run in parallel.
- **Progress log**: each session appends to `milestone-a-progress.md` (workspace root, not in git): test count, what it
  renamed beyond the map, leftover-test scopes it closed, config keys Mohammad must change on Render, anything deferred.
  The next session reads it first.
- **System design**: per the standing rule, each session updates `docs/system-design.html` for the areas it touched
  (sections, "Last verified", change-log row). S11 does the final sweep.
- **New code is born with new names**; no compatibility shims or old-name aliases (the branch is not deployed until merge).
- **i18n**: from S3 on, any UI string a session writes or edits goes into `en.json` with terminology placeholders.
  Sessions before S3 rename identifiers only and leave display text for S8.

### Standard checks (end of every main-app session)

1. `dotnet build Omni.App.slnx` (or the old `.slnx` before S1): 0 errors, no new warnings. Output to scratchpad; read
   only failures and the summary.
2. Throwaway Postgres (own port, per memory recipe), `OMNI_TEST_DB` set, **full** `dotnet test`: 0 failures; passed count
   ≥ the previous session's count in the progress log (tests renamed, not deleted). Only existing `BUG-nn` skips.
3. **Leftover-names test** green (see below) with this session's scopes moved from "pending" to "strict".
4. `schema.sql`, `seed.sql`, `seed-config.sql`, `seed-demo.sql` apply **twice** on an empty local DB without error.
5. App boots against the scratch DB (`ASPNETCORE_ENVIRONMENT=Staging`, env connection string, blank n8n URLs): register a
   client, sign in, open every page the session touched (200, no JS console errors).
6. Docs updated for the area; progress log written; `git status` shows no secrets and no `launchSettings.json`.

Portal sessions: same, with the portal's build and tests, and the pages it touched opened against a locally running
main app. Tester: its own build plus a full local run (section 7).

### Leftover-names test (added in S1)

`Omni.App.Tests/Guards/LeftoverNamesTests.cs` (and a copy in the portal tests, or a script for the portal if it has no
test project) scans `git ls-files` for `clinic`, `lead` (as a word or identifier part: `Lead`, `lead_`, `leadId`, not
"leading"/"mislead"), `procedure`, `appointment`, `staff`, `patient`, `surgeon`, `surgery`, `consultation`,
`sculptflow`, `plasticsurgery`, `ai_agent`, `AiAgent`, `max_agents`.

- **Allowlist file** (`Guards/leftover-names-allowlist.txt`): `Database/migrations/**`, `PROJECT_HANDOFF.md`, the clinic
  business type's seed rows (labels "Patients", "Procedure", stage keys like `consultation_booked`, the clinic
  `ai_profile`), demo data in `seed-demo.sql`, change-log history rows in `system-design.html`, `wwwroot/lib/**`.
- **Line marker** `leftover-ok: <reason>` for the rare legitimate line (e.g. "stored procedure" in a SQL comment).
- **Ratchet**: a `PendingScopes` list (word × path glob) that each session shrinks. The test fails on any hit outside
  the allowlist and pending scopes, and also fails if a pending scope has **no** hits left (so the list can't go stale).
  At the end of A the pending list is empty.

## 5. Sessions

Order: **S1 → S2 → S3 → S4 → S5 → S6 → S7 → S8a → S8b → S8c → S9 → S10 → S11** in the main app. **P1** runs any time
(separate repo); **P2** and **P3** after S7; **T1** after S9 and P3; **S11** last. Sizes are rough estimates.

### S1 · Main app groundwork: Omni names and guardrails
Opus, medium effort · size M · depends on: D1–D9 answered.

- Create `refactor/generic-platform` from `main`. Record the baseline: build warnings, test count, in the progress log.
- `git mv` solution, projects, folders → `Omni.App.slnx`, `Omni.App/`, `Omni.App.Tests/`; namespace
  `PlasticSurgery.*` → `Omni.App.*` everywhere (`.cs`, `.cshtml` `@using`/`@namespace`, `_ViewImports`, `GlobalUsings`,
  `InternalsVisibleTo`, Dockerfile, `.slnx`). Keep `UserSecretsId` (D9). Move the untracked
  `Properties/launchSettings.json` with a plain file move; never stage it.
- Brand: "SculptFlow" → "Omni" in config names, `_comment`s, cookie names (`omni.auth`, `omni-tz`), README, CLAUDE.md
  (repo) folder/namespace examples, test env vars (`OMNI_TEST_DB`). UI display text may be left for S8 (pending scope).
- List every config key / env var whose name changes (none expected beyond brand) in the progress log.
- Add the leftover-names test with all clinic/lead/procedure/appointment/staff/patient scopes **pending** and
  `PlasticSurgery`/`SculptFlow` **strict** (except UI text scope `Pages/**`, `wwwroot/js/**` for "SculptFlow", closed in S8).
- **Acceptance**: standard checks; zero `PlasticSurgery` hits; Docker build arguments name `Omni.App.dll`.

### P1 · Portal groundwork: Omni names
Opus, medium effort · size S · parallel with S1–S6.

- Branch `refactor/generic-platform` in `SculptFlowAdmin`. `git mv` → `Omni.Admin.slnx`, `Omni.Admin/`; namespace
  `SculptFlowAdmin.*` → `Omni.Admin.*`; Dockerfile; brand "SculptFlow Admin" → "Omni Admin"; portal cookie `omni.admin`;
  README, CLAUDE.md, `MAIN_APP_SYNC.md` wording. Keep `UserSecretsId`.
- Add the portal's leftover-names guard (test or script) with clinic/lead/… scopes pending (closed in P2/P3).
- Do **not** touch copied API shapes, routes or `admin-schema.sql` yet (they follow the main app in P2/P3).
- **Acceptance**: build, portal tests (if any), portal boots and signs in against current `main` main app.

### S2 · clinic → client
Opus, medium effort · size XL (may split into S2a backend+DB, S2b pages/JS/tests if context runs long) · after S1.

- `schema.sql` + all seeds: `core.clinics` → `core.clients`, every `clinic_id` → `client_id`, `identity.clinic_users` →
  `identity.client_users`, `scheduling.clinic_*` tables per map, constraint/index names, triggers, comments. Re-runnable.
- Entities, `ToTable`, raw SQL (`Common/Statics` SQL names), repositories, services, `ClientContext`,
  `CurrentClientContext`, mappers, requests/responses/DTOs, controllers and routes (platform-admin `clients`,
  `automation/clients`), PageModels, Razor identifiers, JS identifiers and API URLs, SignalR groups, cache keys
  (`CacheKeys` + `EvictionsFor`), `CacheArea` names, config keys, `AutomationClients` guard (new email domain), tests and
  test kits (`PageKit.ClientContext`, `RepoKit` seeding).
- "Clinic time" switch keeps working (identifier rename only; its label is done in S8).
- **Acceptance**: standard checks; leftover scope `clinic` strict everywhere except UI display text (`Pages/**`,
  `wwwroot/js/**` text) and the clinic seed.

### S3 · Business types, stages, terminology, i18n plumbing
Opus, **high** effort (design) · size L · after S2.

- Tables: `core.business_types` (`code` pk, `name` jsonb per language, `is_active`, `terminology` jsonb per language with
  singular/plural for contact, member, service, booking, order, client, `ai_profile`, `sort_order`), `crm.pipeline_stages`
  (`business_type_code`, `key`, `category`, `labels` jsonb en/ar/fr, `sort_order`, unique (type, key)),
  `core.clients.business_type_code` (FK, not null) + `default_language`. Category check constraint: `open`, `booked`,
  `attended`, `no_show`, `converted`, `closed_lost`, `needs_human`.
- Seed `clinic` and `general` with the stage table from the plan (clinic keys = old lead status values, plus
  `medical_question` per D4), terminology (en filled; ar/fr left for Milestone B) and the clinic `ai_profile` (moved from
  the n8n prompt in S9; placeholder text until then).
- Repository + cache (`core:business-type:*`, settings-based lifetime), `IBusinessTypeService`, `ITerminology` (current
  client's terms, singular/plural, per language with en fallback).
- i18n plumbing: `Resources/i18n/en.json`, a JSON-backed `IStringLocalizer`, terminology placeholders (`{contact}`,
  `{contacts}`, `{Contact}`…) resolved per request, a Razor helper, `window.omniText` for JS from the same JSON, and the
  rule for server messages: services throw with an i18n key + args; `[ApiErrors]` localizes at the edge (falls back to
  the raw text). Language resolution: `users.language` (added in S7) → `clients.default_language` → browser → `en`;
  `<html lang dir>` set per request (RTL itself is Milestone B).
- Register + Google signup completion get a required business-type picker; existing clients in seeds/tests default to
  `clinic`.
- Platform-admin: `GET/POST/PUT /api/platform-admin/business-types` (list, create, edit names/terminology/ai_profile/stages,
  activate), `GET /api/platform-admin/clients/{id}/business-type/preview?to=` (contact counts per stage and their target
  stage), `PUT /api/platform-admin/clients/{id}/business-type` (moves contacts to the first stage with the same category;
  one transaction; audit answer `PlatformAdminChange`). Contacts move only after S6 renames them; until then the
  endpoint moves `leads.status` and S6 renames it.
- Tests: business-type service/repo/endpoints, terminology resolution, localizer (missing key, placeholders, fallback),
  a guard test that every key used in code/Razor/JS exists in `en.json` and every placeholder is known.
- **Acceptance**: standard checks; a `general` client and a `clinic` client both register and see their stage labels.

### S4 · procedure → service (Catalog), procedure booking → order
Opus, medium effort · size L · after S3.

- Orders first (to free the word "booking"): `scheduling.procedure_bookings` → `crm.orders`, `ProcedureBooking` →
  `Order`, `ProcedureBookingStatus` → `OrderStatus` (values unchanged: considering, quoted, deposit_paid, booked → rename
  to `confirmed`, completed, canceled, lost), `/api/orders`, event `procedure_booked` → `order_confirmed`.
- Then `core.procedures` → `core.services`, `Procedure` → `Service`, folder `Catalog` in every layer
  (`CatalogService`, `ICatalogService`, `ServiceRepository`, `ServicesController` at `/api/services`, `CatalogMapper`),
  `/Services` page and `procedures-page.js` → `services-page.js`, AI `/api/ai/services`, cache keys, tests.
- **Acceptance**: standard checks; scopes `procedure` strict (except UI text and clinic seed).

### S5 · appointment → booking
Opus, medium effort · size L · after S4.

- `scheduling.appointments` → `scheduling.bookings`, `appointment_calendar_syncs` → `booking_calendar_syncs`,
  `clinic_booking_settings` (already `booking_settings`), `Appointment*` types/enums → `Booking*`, `/api/bookings`,
  `appointments-calendar.js` → `bookings-calendar.js`, SignalR `BookingChanged`, calendar sync payloads (`CalendarSyncNotifier`;
  workflow side in S9), AI `/api/ai/bookings/{available,schedule,book,upcoming,reschedule,cancel}` and `AiTestController`,
  event types `consultation_*` → `booking_*`, cache keys, tests.
- Check every remaining "booking" identifier is unambiguous (booking settings vs bookings vs orders).
- **Acceptance**: standard checks; scope `appointment` strict; scope `consultation` strict outside the clinic seed.

### S6 · lead → contact, pipeline stages
Opus, **high** effort (semantics) · size L · after S5.

- `crm.leads` → `crm.contacts`; `status` → `stage_key` (FK-like check against the client's type, validated in code);
  `qualification_status` → `qualification` (D4); `preferred_language`; `Lead*` → `Contact*`; `/api/contacts`, `leads.js` →
  `contacts.js`; `MessageSenderType` `lead` → `contact`; events `lead_*` → `contact_*`,
  `customer_message_received` → `contact_message_received`; AI `GET/PATCH /api/ai/contacts/{id}`.
- Replace every hard-coded status in code with **categories**: booking created → first stage of `booked`; attended →
  `attended`; no-show → `no_show`; order confirmed → `converted`; hand-off → `needs_human`. The AI PATCH accepts a
  category (server picks the stage) or a valid stage key. Dashboard funnel/reports group by category.
- Finish the business-type change endpoint from S3 on contacts.
- **Acceptance**: standard checks; scopes `lead`, `patient` strict (except UI text, clinic seed); a contact walks
  open → booked → attended → converted on both business types in a test.

### S7 · Users, roles, members (one users table)
Opus, **high** effort · size L · after S6.

- Schema: `identity.users` (+ `role` check `admin|client|member`, `full_name`, `language`, `is_active`,
  `last_login_at`, `created_at`, `updated_at`), `user_claims`/`user_logins`/`user_tokens`, `client_users` without the
  unique active index (rule moves into code, with a test). `AppUser : IdentityUser`, EF mapping, Identity lockout
  options matching the portal's current rules (failed attempts, lock minutes).
- Main app login (password + Google) accepts only `client` / `member`; the role and active membership are checked on
  **every request** (in `CurrentClientContext` / cookie validation), not only at login; an `admin` user is refused.
- Signup creates the client + user (role `client`) + `client_users` row. Members: `/api/members` (list, add with
  password, deactivate, reactivate) restricted to role `client` (D5); seat limit `max_users` counts active client+member
  users. Staff → Members everywhere (`MemberClaims`, pages `/Members`, sender type `member`, events
  `member_message_sent_*`).
- Entitlements `max_agents` → `max_users` ("Seats"), `ai_agent` → `ai_assistant` (plans, seeds, `EntitlementCatalog`,
  billing tests). AI wording in code: "AI assistant".
- Platform-admin: `POST /api/platform-admin/auth/login` (email + password → checks hash, lockout, role `admin`, active;
  updates `last_login_at`; returns id, name, email; generic error text, no user enumeration),
  `GET /api/platform-admin/auth/admins/{id}` (still active + admin, for the portal's per-request check),
  `/api/platform-admin/admins` (list, create, deactivate, reactivate, reset password; can't deactivate the last active
  admin), `/api/platform-admin/members` (was `/staff`). Main-app CLI `create-admin` (replaces the portal's).
- Tests: login matrix (role × app × active × locked), per-request role check, membership rule, seats, admin endpoints,
  password hash compatibility (a hash produced by `PasswordHasher<AdminUser>` verifies as `AppUser`).
- **Acceptance**: standard checks; scopes `staff`, `ai_agent`, `max_agents` strict.

### S8a / S8b / S8c · UI strings into `en.json` with terminology
Opus, medium effort · each size M–L · after S7, in order.

- **S8a**: `Pages/Shared` (layout, sidebar, partials), Account, Dashboard, Inbox, Settings (incl. "Clinic time" →
  "`{Client}` time"/"My time").
- **S8b**: Contacts, Services, Bookings/calendar, Orders, Members, Campaigns, KnowledgeBase, WhatsApp, Status, Error;
  all 20 JS files (`window.omniText`); `title`/`placeholder`/`aria-*` texts; notifications text.
- **S8c**: the ~190 user-facing server messages (exceptions, validation, notifications, emails) → keys + args.
- Rule: no sentence glues a term into grammar Arabic can't follow; every term comes with singular/plural keys. Keys are
  grouped `area.element.state` (`inbox.empty.title`).
- **Acceptance**: standard checks; the en.json guard test (no missing/unused keys, known placeholders); the leftover test
  fully strict on `Pages/**` and `wwwroot/js/**`; a `general` client sees no clinic word on any page, a `clinic` client
  sees Patients / Procedures / Consultation (checked page by page in the browser).

### S9 · n8n contracts and workflows
Opus, **high** effort · size M · after S8c (all routes final).

- Main app side: payloads of `AiTriggerNotifier`, `CalendarSyncNotifier`, `N8nKnowledgeBenchmarkGeneratorClient` use
  `clientId`, `contactId`, `bookingId`…; the AI trigger payload carries `businessType`, `aiProfile` (from
  `core.business_types`), terminology and the client's default language; contracts documented in `system-design.html`.
- Workflows: copy the three exports into `SculptFlowApp/docs/n8n/` as `omni-ai-assistant.json`,
  `omni-calendar-sync.json`, `omni-retrieval-benchmark.json`; rename tools (`search_knowledge`, `get_available_slots`,
  `get_my_bookings`, `create_booking`, `cancel_booking`, `handoff_to_human`), URLs, body fields, node names; the system
  prompt becomes generic and reads `aiProfile` + terminology from the trigger payload; the clinic prompt text moves into
  the clinic `ai_profile` seed. AI keeps replying in the contact's language.
- **Ingest key**: the AI Agent export holds the real `X-Ingest-Key` in a header. Replace the literal with an n8n
  **Header Auth credential reference** in the copies; never print, quote or commit the value. Before staging, a script
  checks that the old file's header value appears 0 times in every new file (prints only the count).
- Base URL stays the literal Render URL for now (n8n variables need a paid plan); Mohammad updates it at import.
- Tests: contract tests that serialize each payload and compare to the documented JSON field list; AI endpoint tests.
- **Acceptance**: standard checks; workflow JSON files valid and free of old words (allowlist: none); key check passes.

### S10 · Data copy script
Opus, **high** effort · size L · after S9 (schema final). Any later `schema.sql` change must update this script and its test.

- `Database/migrations/2026-10-generic/copy-from-old.sql` for the **new** project: `create extension postgres_fdw`,
  a foreign server + user mapping for the old project (placeholders `:old_host`, `:old_password`… filled by Mohammad
  locally, never committed), `import foreign schema` of each old schema into `old_*` local schemas, then one
  `insert … select` per table in FK order with the column renames, then admin users, then
  `setval` for identity columns, then a row-count report (old vs new per table, and a final `ok`/`mismatch`).
- Mappings: lead `status` → clinic `stage_key` (same values); qualification per D4 (`medical_question` → stage
  `medical_question` when the contact is in an `open` stage); `procedure_bookings.status` `booked` → `confirmed`; message
  sender `lead`/`staff` → `contact`/`member`; event types per map; config keys and cache-area settings per map;
  entitlements `max_agents`/`ai_agent` → `max_users`/`ai_assistant`; every client → business type `clinic`, default
  language `en`; clinic owners → role `client`, other staff → `member` (rule: the earliest membership per clinic is the
  owner; listed in the report for Mohammad to check); `admin.admin_users` → `identity.users` role `admin` (id = uuid as
  text, `user_name`/`email` + normalized uppercase, same `password_hash`, `failed_logins` → `access_failed_count`,
  `locked_until` → `lockout_end`, new security/concurrency stamps); `admin_audit_log` copied with `user_id`, `client_id`.
- Runs once into an empty schema; refuses to run if target tables already have rows (unless `--force` variable set).
- Local test (scripted, in `Database/migrations/2026-10-generic/test-copy.ps1`): throwaway cluster (has `postgres_fdw`),
  DB `old` from `git show main:Database/schema.sql` + old seeds + `seed-demo.sql` + old `admin-schema.sql` + a seeded
  admin; DB `new` from the branch's schema + seeds + new admin schema; run the copy; assert counts match, a migrated
  client user and a migrated admin can log in (through the app against `new`), stages and events mapped. The vector
  column uses the same `real[]` swap as the tests on both sides.
- **Acceptance**: the script test passes twice from scratch; standard checks.

### P2 · Portal sync: shapes, pages, business types
Opus, medium effort · size L · after S7 (S9 not needed).

- Diff main-app commits since `MAIN_APP_SYNC.md`'s last check; rename copied requests/responses/DTOs/enums, API clients
  (`/clients`, `/contacts`, `/members`, `/services`, `/bookings`, `/orders`), services, pages and folders (`Clinics` →
  `Clients`, `Leads` → `Contacts`, `Staff` → `Members`, `Procedures` → `Services`, `Appointments` → `Bookings`), audit
  `client_id`, scope cookie lookups.
- New **Business types** page (list, edit names/terminology/ai_profile/stages, activate) and a business-type picker on
  client create; **Change business type** on the client page shows the preview counts, then confirms.
- Update `MAIN_APP_SYNC.md` with the new last-checked commit.
- **Acceptance**: build, tests, leftover guard with all scopes except `admin_users` strict; every portal page opens
  against the local main app.

### P3 · Portal login and admins through the main app
Opus, **high** effort · size M · after P2.

- Login posts to `POST /api/platform-admin/auth/login`; the portal issues its own `omni.admin` cookie with the admin
  id/name/email; every request re-checks `GET /auth/admins/{id}` (cached ≤ 1 minute in the portal) and signs out a
  deactivated or demoted admin. Admins page → `/api/platform-admin/admins`.
- Remove `AdminUser`, its repository, `IPasswordHasher<AdminUser>`, the `create-admin` engine; `IAdminUnitOfWork` keeps
  only the audit log. `admin-schema.sql`: new block that drops `admin.admin_users` after the FK moves to
  `identity.users(id)` (`user_id text`), renames `clinic_id` → `client_id`. Fresh projects get the final shape.
- Tests: login success, wrong password, locked, non-admin role, main app down (inline message), per-request revoke.
- **Acceptance**: build, tests, leftover guard fully strict; sign in to the portal with an admin migrated by the S10
  local test.

### T1 · Automation tester
Opus, medium effort · size M · after S9 and P3. On `master`, on top of the 17 uncommitted changes (D2).

- Rename to `Omni.Automation` (D7), namespaces, brand, `[Automation]` client naming, `@omni-automation.test`,
  `AutomationClients`; every route and payload per the map; suites renamed (`ClinicSuites` → `ClientSuites`…); new tests:
  register with each business type, stage moves by category, members (client vs member permissions, seats), main-app
  login refuses admin, portal auth endpoints, admins endpoints, business-type endpoints and change preview, AI tools on
  new routes, en.json-driven pages render without missing keys.
- Update `README.md`, `COVERAGE.md`.
- **Acceptance**: tester builds; full local run (`dotnet run -- run --env local --tests all`, local main app on a
  scratch DB per memory recipe): all green except the bugs already listed in `COVERAGE.md`; every route has a test.

### S11 · Docs sweep and final verification
Opus, medium effort · size M · last.

- `docs/system-design.html` (all sections, contracts, schema, change log), `docs/configuration.md` (renamed keys),
  `docs/billing.md` (seats, entitlements), `docs/UI_GUIDE.md` (i18n rules, placeholders), READMEs, both repo
  `CLAUDE.md` files (schema table, folders, namespaces, i18n standing rule, users/roles rule).
- Merge `main` into the branch in both repos if anything landed; re-run every check in all three repos; the S10 script
  test; the tester's full local run.
- Write the Milestone A summary (what changed, config/env changes for Render, Mohammad's step list with exact commands).
- **Acceptance**: every check green; leftover tests fully strict in both apps (pending lists empty).

## 6. Dependencies at a glance

| Session | Repo | Needs | Model, effort |
|---|---|---|---|
| S1 | main app | D1–D9 | Opus, medium |
| P1 | portal | D1–D9 (runs alongside S1–S6) | Opus, medium |
| S2 → S3 → S4 → S5 → S6 → S7 | main app | the one before | S2/S4/S5 medium; S3/S6/S7 **high** |
| S8a → S8b → S8c | main app | S7 | Opus, medium |
| S9 | main app | S8c | Opus, **high** |
| S10 | main app | S9 | Opus, **high** |
| P2 → P3 | portal | S7 and P1 (runs alongside S8–S10) | P2 medium; P3 **high** |
| T1 | tester | S9 and P3 | Opus, medium |
| S11 | all three | S10 and T1 | Opus, medium |

Parallel only across repos: P1 alongside S1–S6; P2/P3 alongside S8a–S10.

## 7. What Mohammad does himself

**Before S1**
1. Answer D1–D9 (or accept the defaults).
2. Commit the tester's 17 changes on `master` (D2).
3. Freeze feature work on `main` in both repos during A (small fixes only; they get merged into the branch).

**Any time (Milestone C needs it, DNS takes time)**
4. **Brevo**: create the account, verify the sending domain (SPF + DKIM records at the DNS host), create an API key and
   keep it for user-secrets / Render env (`Brevo:ApiKey`, set in Milestone C).

**After S9 (n8n)**
5. **Rotate the ingest key**: generate a new value; set it as the main app's `N8n:IngestApiKey` (user-secrets + Render
   env), in the n8n Header Auth credential, and in the tester's user-secrets. Delete or clean the workspace-root
   `docs/n8n/` copy that holds the old key.

**After S10 (for the staging check and the cutover)**

> Superseded 2026-10-20 (S11): steps 5–12 are now one ordered list in `SculptFlowApp/docs/deploy-runbook.md`. The old
> text below had two errors: `seed.sql` is demo data (never on the new project) and the copy takes the old host as `psql`
> variables (direct connection; session pooler only as a fallback), so no file is edited. `admin-schema.sql` runs **after**
> the copy and never on the old project.

6. **Fresh Supabase project**: `schema.sql`, then `seed-config.sql` (no demo seeds).
7. **Run the data copy** with `psql -v old_host=… -v old_password=…` (see the runbook), check the report (all `ok`,
   owner/member list right), `analyze;`, then the portal's `admin-schema.sql` (no-op). The script drops its own foreign
   server.
8. **Staging**: deploy the branch to a second Render service (Dockerfile now builds `Omni.App.dll`) pointed at the new
   project, with the renamed env vars from the progress log.
9. **Import the n8n workflows** from `docs/n8n/`: attach the Header Auth (ingest key) and OpenAI credentials, set the base
   URL to the staging service, set the main app's `N8n:*WebhookUrl` values to the new webhook URLs, deactivate the old
   workflows. Run a scripted conversation in English, Arabic and French (ask, book, reschedule, cancel, hand-off) and
   check the reply language and the stage changes.
10. Sign in to the portal with your existing admin password (migrated hash) and to the dashboard with a migrated client.

**After everything is green**
11. Merge `refactor/generic-platform` into `main` in both repos and deploy; point production at the new Supabase project.
12. Rename the GitHub repos and remotes, the Render services/domain; if the public domain changes, update the Google
    OAuth redirect URIs, Meta webhook URL, TikTok redirect, and re-register Telegram webhooks.
13. Rename the local folders if you want (D6), then ask me to update the workspace `CLAUDE.md` and memory paths.
14. Retire the old Supabase project once production has run cleanly on the new one.

## 8. Risks specific to this execution

- **S2 is the largest session** (10.9k hits): run it as S2a/S2b if the context grows; never leave the build red between them.
- **Display text renamed blindly** before S8: acceptable on the unreleased branch; S8 owns every visible string.
- **Copy script drift**: any schema change after S10 must update the script and rerun its test (S11 re-checks).
- **n8n downtime**: the AI is down from merge until the new workflows are live; import and test on staging first (step 9).
- **Ingest key exposure**: only counts are ever printed; the workspace-root export is cleaned after rotation.
- **Uncommitted tester work**: if D2 is declined, T1's diff mixes with yours; nothing is ever discarded.
