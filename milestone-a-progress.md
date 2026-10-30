# Milestone A progress log

Each session appends its entry. Read the latest entry before starting the next session (rules: `milestone-a-plan.md` §4).

## S1 · Main app groundwork: Omni names and guardrails (2026-10-16)

Branch `refactor/generic-platform` in `SculptFlowApp`, created from `main` 60a4785. Committed, not pushed.

**Baseline (main, before S1):** build 0 errors / 8 warnings (all pre-existing xUnit/EF analyzer warnings in the test
project); full `dotnet test` 1,626 passed, 0 failed, 0 skipped.

**After S1:** build 0 errors / 8 warnings (same ones); full `dotnet test` **1,654 passed**, 0 failed, 0 skipped
(1,626 + 28 new guard tests). Next session's floor: 1,654.

**Renamed (per the map):** `PlasticSurgery.slnx` / `PlasticSurgery/` / `PlasticSurgery.Tests/` → `Omni.App.slnx` /
`Omni.App/` / `Omni.App.Tests/`; namespaces `Omni.App.*`; Dockerfile runs `Omni.App.dll`; brand text "SculptFlow" /
"MySculptFlow" → "Omni" everywhere (UI text included, so no pending "SculptFlow" scope is left for S8); Identity cookie
now `omni.auth` (was the ASP.NET default), timezone cookie `omni-tz`; `SCULPTFLOW_TEST_DB(_ALLOW_REMOTE)` →
`OMNI_TEST_DB(_ALLOW_REMOTE)`, test databases `omni_test_*`; tester email domain `@omni-automation.test` (S2 still
renames `AutomationClinics` → `AutomationClients`); `appsettings.Development.json` sample DB name `omni`.
`UserSecretsId` unchanged (D9). Untracked `launchSettings.json` moved with the folder, still ignored (`.gitignore` path
updated).

**Renamed beyond the map (decided in S1):**
- Code identifiers meaning "the platform operator" use **Platform**, not the brand: `ChargedBySculptFlow` →
  `ChargedByPlatform` (`ClinicChannelBillingRow`, `ClinicUsageRow`), `ProviderCostPaidBySculptFlow` →
  `ProviderCostPaidByPlatform` (`BillingReport`), locals and test names likewise.
- JS global `window.SculptTime` → `window.OmniTime`.
- Calendar sync n8n payload field `SculptFlowAppointmentLink` → `DashboardLink` (JSON `dashboardLink`).
- Crawler User-Agent `SculptFlowBot/1.0` → `OmniBot/1.0` (robots.txt token `omnibot`).
- Demo seed slug `demo-sculptflow` → `demo-omni`; billing test actor `ops@omni`.
- Swagger title "Plastic Surgery Clinic API" → "Omni API"; `schema.sql` header.
- Removed the tracked coverage report `PlasticSurgery.Tests/TestResults/…/coverage.cobertura.xml` (generated output) and
  ignored `**/TestResults/`.

**Leftover-names test:** `Omni.App.Tests/Guards/LeftoverNamesTests.cs` + `LeftoverNameScanner.cs` +
`leftover-names-allowlist.txt` (format: `path: <glob> [| words]`, `token: <literal>`, line marker `leftover-ok: <reason>`).
Strict now: `sculptflow`, `plasticsurgery`. Pending (word, `**`, closed by): clinic S2; procedure S4; appointment,
consultation S5; lead, patient, surgeon, surgery S6; staff, ai_agent, max_agents S7. Each session narrows or removes its
scopes; a scope with no hits fails the test. Allowed tokens: `sculptflowapp.onrender.com` (live Render host) and
`..\SculptFlowAdmin` (checkout folder, D6). Not yet in the allowlist (add when the scope goes strict): clinic seed rows,
`seed-demo.sql` demo data, `system-design.html` change-log history.

**Standard checks:** build ✓; full tests ✓; leftover test ✓; `schema.sql` + `seed.sql` + `seed-config.sql` +
`seed-demo.sql` applied twice on an empty local DB ✓ (local server has no pgvector: schema and demo seed used the
`real[]` swap the tests use); app published and run in Staging against the scratch DB: registered a clinic, signed in,
opened all 14 sidebar pages + Error, no JS errors (only Facebook's "login button needs https" notice on http) ✓.
Note: `dotnet run` in Staging serves empty compressed static files (no CSS/JS); run the published output instead.

**Config / env changes for Render (Mohammad):** none required. No appsettings key or env var name changed. The
`config.settings` row `WebScraping/UserAgent` in existing databases keeps the old value (seed is insert-if-missing);
the S10 copy script should map it, or edit it in the portal's Configuration page.
Everyone is signed out once when this ships (new cookie name).

**For later sessions:**
- P1/P2 (portal): its copies of `ClinicChannelBillingRow` / `ClinicUsageRow` (`ChargedBySculptFlow`) and `BillingReport`
  (`ProviderCostPaidBySculptFlow`, "Provider cost paid by SculptFlow" on `Pages/Billing/Report.cshtml`) must follow the
  Platform names, or those values read as false/0 against this branch.
- S9: the n8n calendar workflow must read `dashboardLink`.
- T1: tester email domain is now `@omni-automation.test` on the main app side.
- `localStorage` fallback key `sf-theme` in the layouts' theme script was left as is (legacy read only).

## S2 · clinic → client (2026-10-16)

Branch `refactor/generic-platform`, on top of S1 `dbbb066`. Committed, not pushed.

**Result:** build 0 errors / 8 warnings (same as S1); full `dotnet test` **1,654 passed**, 0 failed, 0 skipped (no test
lost; renamed only). Next session's floor: 1,654.

**Renamed (per the map):** `core.clients`, `identity.client_users`, `scheduling.availability_rules` /
`availability_exceptions` / `booking_settings`, every `clinic_id` → `client_id`, index/constraint/trigger names; all
"Clinic" identifiers in code, Razor, JS, tests and docs (`Client`, `ClientUser`, `ClientContext`,
`CurrentClientContext`, folders `Clients`, `ClientsAdminController`, `AutomationClients`, `PageKit.ClientContext`…);
routes `/api/platform-admin/clients`, `/automation/clients`, billing admin `clients/{clientId}/…`,
`knowledge-benchmark/clients/{clientId}`, overview `recent-clients`, AI `/api/ai/client-info?clientId=`; JSON fields
`clientId`; SignalR group `client:{id}`; cache prefix `core:client:`, `CacheArea.Clients`. Display text was renamed
blindly too ("Client time", "Client name"…); S8 rewrites it.

**Renamed beyond the map (decided in S2):**
- Availability entities drop the prefix like their tables: `AvailabilityRule`, `AvailabilityException`, `BookingSettings`
  (cache key `scheduling:availability:booking:` left for S5).
- Settings page Clinic Info → **Business Info**: `/settings/business-info`, `Pages/Settings/BusinessInfo`,
  `BusinessInfoModel`, `business-info.js`, `BusinessInfoPageTests` (map said `/Settings/BusinessInfo`).
- Tool names in comments/docs: `search_clinic_knowledge` → `search_knowledge`, `get_clinic_info` → `get_client_info`
  (S9 renames them in the workflow). Placeholders `yourclinic` → `yourbusiness`. `_StatusHelp` "clinical/clinician"
  wording → "medical question / expert".
- `Controllers/Client` (dashboard APIs) kept its name; no clash with the `Client` entity.
- Settings keys (DB rows, not env): `WebScraping/MaxSourcesPerClinic` → `MaxSourcesPerClient`, `Cache/ClinicsMinutes`
  → `ClientsMinutes`.
- Stored vocabulary values: `billing.usage_records.rate_source` `clinic` → `client` (`RateSource.Client`); trace step
  `webhook.clinic_unresolved` → `webhook.client_unresolved`.
- `schema.sql` bug fixed (found while applying twice; also on `main`): the first `ck_messages_origin` rewrite left out
  `telegram_customer`, so re-running the schema over data with Telegram messages failed. It now lists every origin.

**Leftover-names test:** scope `clinic` removed from `PendingScopes` (strict everywhere). Allowlist adds
`Database/seed-demo.sql | clinic` (the demo is a populated clinic). Line markers: `seed.sql` demo name/slug lines,
16+1 change-log rows in `system-design.html`, the old-word list in `CLAUDE.md`.

**Standard checks:** build ✓; full tests ✓; leftover test ✓; `schema.sql` + `seed.sql` + `seed-config.sql` +
`seed-demo.sql` applied twice in that order on an empty DB ✓ (no pgvector: schema `vector(1536)` → `real[]`; demo seed
`dummy_embedding()::vector` → `('{' || trim(both '[]' from …) || '}')::real[]`); published app in Staging against the
scratch DB: registered a client, landed on the dashboard, opened all 14 sidebar pages + the three Business Info tabs +
Error: all 200, no JS errors (only Facebook's http notice); server log only the https-port warning ✓.

**Config / env changes for Render (Mohammad):** none (no appsettings key or env var renamed).

**For later sessions:**
- S9: every n8n payload/query uses `clientId` now, and `/api/ai/client-info`; the AI and calendar workflows are broken
  against this branch until S9.
- S10 copy script: map table/column names above, settings keys `MaxSourcesPerClinic` / `ClinicsMinutes`,
  `rate_source` `clinic` → `client`, trace step `webhook.clinic_unresolved`. Old JSON metadata rows keep `clinicId` keys.
- P2 (portal): routes `clients`, `recent-clients`, `automation/clients`, billing `clients/{id}`,
  `knowledge-benchmark/clients/{id}` and every `clinicId` JSON field → `clientId`; `PlatformAdminChange { clientId }`.
- T1 (tester): `/api/platform-admin/automation/clients`, `AutomationClients`.
- Browsers that saved "Clinic time" keep working (anything but `mine` means the client's time).

## S3 · Business types, stages, terminology, i18n plumbing (2026-10-16)

Branch `refactor/generic-platform`, on top of S2 `3771021`. Committed `63a971c`, not pushed.

**Result:** build 0 errors / same 8 old test warnings; full `dotnet test` **1,742 passed**, 0 failed, 0 skipped (+88 new).
Next session's floor: 1,742.

**Added:** `core.business_types` (code, name/terminology jsonb per language, ai_profile, is_active, sort_order) and
`crm.pipeline_stages` (pk type+key, category, labels jsonb, sort_order), seeded once in `schema.sql` (a type that already
exists is never touched again; the FK on clients needs the rows before the column, so they can't live in a seed file):
`clinic` = old lead statuses + `medical_question`, `general` = new, contacted, qualified, booked, attended, no_show, customer,
not_interested, lost, needs_human. English words only. `core.clients.business_type_code` (FK, no default; existing rows
`clinic`) + `default_language` (en/ar/fr). `ck_leads_status` dropped: staff/admin status changes are checked against the
client's stages in code. `BusinessTypeService`, cache `core:business-type:all` (area `BusinessTypes`, setting
`Cache:BusinessTypesMinutes`). Register + Google completion have a required business-type picker; the client's default
language = sign-up language. Platform admin: `GET/POST /business-types`, `GET/PUT /business-types/{code}`,
`PUT /business-types/{code}/active`, `GET /clients/{id}/business-type/preview?to=`, `PUT /clients/{id}/business-type`;
`ClientDetail` gains `businessTypeCode`, `defaultLanguage`. Leads list, lead page, campaign audience show stage labels
(badge colour by category, `StageDisplay`). i18n: `Resources/i18n/en.json` (embedded), `JsonStringLocalizer` (`@T` via
`_ViewImports`), `ITerminology`, `ITextContext` + `RequestTextMiddleware`, `<html lang dir>`, `/i18n/{lang}.js` +
`omni-text.js`, keyed exceptions (`TextError.WithArgs`) localized by `[ApiErrors]`. Number/date culture unchanged.

**Beyond the map:** stage key move rule on a type change = same key+category if it exists, else first stage of the category,
else first open stage. Leftover guard: block markers `leftover-ok-begin: <reason>` / `leftover-ok-end`, token
`TextKit.Clinic` (tests' constant for the built-in type). New guard `Guards/UiTextKeysTests` (missing/unused keys, unknown
placeholders; `terms.*` exempt). Repo `CLAUDE.md` has a new "UI text and business types" standing rule.

**Checks:** schema + seed + seed-config + seed-demo applied twice on an empty DB ✓, and schema over the S2 schema with data ✓;
published app in Staging: registered a `general` and a `clinic` client; Register picker, Interested People filter/badges,
lead page (saved a stage change), campaign audience filter all show the right labels; no JS errors. Google completion only
covered by tests (needs Google).

**For later:** S6 must move the `LeadStatus` writes (booking, attended, no-show, order, contacted) to categories: until then a
`general` client's contacts get clinic keys from automatic moves (shown with a fallback label). P2: portal business-types page,
client type change with preview, `businessTypeCode` on client detail. T1: Register now needs `BusinessTypeCode`. S9: AI payload
can read `ai_profile`/terminology from `core.business_types`. S10: copy script sets `business_type_code = 'clinic'`.
**Render:** no env var changes; `seed-config.sql` adds `Cache:BusinessTypesMinutes` (insert-if-missing).

## S4 · procedure → service (Catalog), procedure booking → order (2026-10-17)

Branch `refactor/generic-platform`, on top of S3 `63a971c`. Committed `7a624c0`, not pushed.

**Result:** build 0 errors / same 8 old test warnings; full `dotnet test` **1,742 passed**, 0 failed, 0 skipped (renamed only,
none lost). Next session's floor: 1,742.

**Renamed (per the map):** `scheduling.procedure_bookings` → **`crm.orders`** (D3), `core.procedures` → `core.services`,
columns `procedure_id` → `service_id`, `procedure_date` → `service_date`, index/trigger/check names; order status `booked` →
**`confirmed`** (`OrderStatus.Confirmed`); event `procedure_booked` → `order_confirmed`. Code: folders `Catalog`
(`CatalogService : ICatalogService`, `ServiceRepository`, `CatalogMapper`, `CreateServiceRequest`, `ServiceResponse`…) and
`Orders` (`OrderService`, `OrderRepository`, `OrderMapper`, `CreateOrderRequest`, `OrderResponse`); entities `Service`,
`Order`; `ServicesController` `/api/services`, `OrdersController` `/api/orders`; AI `/api/ai/services`; dashboard
`GET /api/dashboard/services`; platform-admin `content/services`, `content/services/{id}/active`; page `/Services`,
`/Services/Edit/{id?}`, `services-page.js` (element ids `svc-*`); cache `core:services:` (`CacheArea.Services`); tests
renamed (`CatalogOrderAndNotificationServiceTests.cs`, `OrderRepositoryTests`, `ServiceRepositoryTests`…). Display text
renamed blindly ("Services", "Add service"); S8 swaps in `{Services}` so a clinic sees Procedures again.

**Renamed beyond the map (decided in S4):**
- Orders got their own `Mappers/Orders/OrderMapper` (the old `ProcedureMapper` mapped both).
- `DashboardServiceRow.BookingCount` → `OrderCount` (JSON `orderCount`); order locals/fields `booking` → `order`.
- Settings key `Cache:ProceduresMinutes` → **`Cache:ServicesMinutes`** (`seed-config.sql` row; insert-if-missing).
- Knowledge category value `procedure` → **`service`** ("Service Information"); campaign audience filter JSON
  `procedureId` → **`serviceId`**; AI tool name in docs `get_procedures` → `get_services`.
- Test locals `proc` → `svc`.

**Leftover-names test:** scope `procedure` removed from `PendingScopes` (strict everywhere). Allowlist: `seed-demo.sql`
now `| clinic,procedure` (its knowledge text, WhatsApp template variable `procedure` and demo URLs are clinic data; its
table/column names, category values and order status were renamed). Clinic business-type seed rows already sit in a
`leftover-ok` block. Two more change-log rows marked.

**Standard checks:** build ✓; full tests ✓; leftover + UI-text guards ✓; `schema.sql` + `seed.sql` + `seed-config.sql` +
`seed-demo.sql` applied twice on an empty DB ✓ (same `real[]` swap); published app in Staging on the scratch DB: registered
a `general` client, added a service through `/Services/Edit` (code suggestion works), `/Services` search/no-match works,
`POST /api/orders` → `considering`, `PATCH` to `confirmed` logs `order_confirmed`, `booked` is refused (400), dashboard
`services` stats show `orderCount`; opened Services, Services/Edit, Dashboard, Interested People, lead page, Appointments
(new-appointment service list loads from `/api/services`), Inbox, Campaigns/Create: all 200, no new JS errors. Campaign
audience service filter only covered by page tests (needs an approved WhatsApp template to render).

**Config / env changes for Render (Mohammad):** none (no appsettings key or env var renamed).

**For later sessions:**
- S6: `OrderService` still sets `LeadStatus.SurgeryBooked` on confirm; move to the `converted` category.
  `DashboardSummaryCounts.SurgeriesBooked` and `_StatusHelp` "Surgery booked" are S6 words.
- S9: n8n AI tool URL `/api/ai/procedures` → `/api/ai/services` (tool `get_services`); knowledge category `service`.
- S10 copy script: `scheduling.procedure_bookings` → `crm.orders` with status `booked` → `confirmed`; `core.procedures` →
  `core.services`; `procedure_id`/`procedure_date` columns; event `procedure_booked` → `order_confirmed`;
  `config.settings` `Cache/ProceduresMinutes` → `ServicesMinutes`; knowledge categories `procedure` → `service`
  (documents and website sources); `marketing.campaigns.audience_filters` key `procedureId` → `serviceId`.
- P2 (portal): routes `content/services`, `content/services/{id}/active`; `ServiceRow` shape; dashboard/order shapes if
  shown; cache area `Services`.
- T1 (tester): `/api/services`, `/api/orders` (status `confirmed`), `/api/ai/services`, page `/Services`.

## S5 · appointment → booking (2026-10-17)

Branch `refactor/generic-platform`, on top of S4 `7a624c0`. Committed `de497d4`, not pushed.

**Result:** build 0 errors / same 8 old test warnings; full `dotnet test` **1,742 passed**, 0 failed, 0 skipped (renamed only,
none lost). Next session's floor: 1,742.

**Renamed (per the map):** `scheduling.appointments` → `scheduling.bookings`, `appointment_calendar_syncs` →
`booking_calendar_syncs` (index `ux_booking_calendar_syncs_booking_integration`), every `appointment_id` → `booking_id`,
index/trigger/check names; code folder `Bookings` in every layer (`BookingService`, `IBookingService`, `BookingRepository`,
`BookingMapper`, `BookingsController` at `/api/bookings`, `Booking`, `BookingStatus`, `BookingCalendarSync`,
`BookingCalendarSyncStatus`, `CreateBookingRequest`, `BookingResponse`, `UpcomingBookingResponse`, `CalendarBookingResponse`,
`BusyBookingRow`, `MultipleUpcomingBookingsException`…); pages `/dashboard/bookings`, `/dashboard/bookings/{id}`
(`Bookings.cshtml`, `BookingDetail.cshtml`), `bookings-calendar.js`; SignalR `BookingChanged {bookingId, change}`;
AI `/api/ai/bookings/{available,schedule,book,upcoming,reschedule,cancel}`; `AiTestController` results; calendar sync
payload/callback `bookingId` (`CalendarSyncNotifier`); tests renamed (`BookingServiceTests`…). Display text renamed blindly
("Bookings", "Booking rules and other info"); S8 swaps in `{Bookings}`.

**Renamed beyond the map (decided in S5):**
- Events: `consultation_booked` → `booking_created`, `consultation_attended` → `booking_attended`, `consultation_no_show` →
  `booking_no_show`, `consultation_rescheduled` → `booking_rescheduled`, `consultation_cancelled` → `booking_canceled`
  (`EventTypes.BookingCreated`…).
- Notification types `APPOINTMENT_BOOKED/RESCHEDULED/CANCELLED` → `BOOKING_CREATED/RESCHEDULED/CANCELED`; notification title
  "Booking made"; message without a service says "a slot booked".
- AI tools (docs/comments; workflow in S9): `schedule_consultation` → `schedule_booking`, `book_consultation` → `book_slot`
  (`BookSlotRequest`/`BookSlotResult`, action `BookSlot`), `reschedule_consultation` → `reschedule_booking`,
  `cancel_consultation` → `cancel_booking`, `get_my_appointments` → `get_my_bookings`; `ScheduleBookingRequest/Result`,
  `CancelBookingRequest/Result`, `RescheduleBookingRequest`, `CancelBookingAsync`. Result code
  `EXISTING_UPCOMING_APPOINTMENT` → `EXISTING_UPCOMING_BOOKING`; JSON fields `booking`, `existingUpcomingBookings`,
  `bookingId`, `bookingType`.
- Columns: `appointment_type` → `booking_type`, default `'consultation'` → **`'standard'`** (code default too);
  `core.clients.consultation_info` → `booking_info` (`BookingInfo`); `core.services.consultation_duration` →
  `booking_duration` (`BookingDuration`); `booking_settings.default_consultation_duration_minutes` →
  `default_booking_duration_minutes`.
- Campaign audience `reactivation_no_consultation` → `reactivation_no_booking` (`ReactivationNoBooking`; check constraint).
- Knowledge category `consultation` → `booking` ("Booking").
- Dashboard counts `ConsultationsBooked/Attended` → `BookingsMade` / `BookingsAttended` (JSON `bookingsMade`, `bookingsAttended`).
- `LeadStatus.ConsultationBooked/Attended` → `LeadStatus.Booked` / `Attended`; values stay the clinic stage keys
  `consultation_booked` / `consultation_attended` (S6 replaces them with categories). Fallback labels "Booked"/"Attended".
- Availability internals: `Ctx.Booking` → `Ctx.Settings`, `SaveAsync(... BookingRulesDto bookingRules)`, so "booking" in
  `AvailabilityService` means bookings only. Cache key `scheduling:availability:booking:` →
  `scheduling:availability:booking-settings:` (kept under the availability prefix so the area still drops together).

**Leftover-names test:** scopes `appointment` and `consultation` removed from `PendingScopes` (strict everywhere). Allowlist:
`seed-demo.sql` now `| clinic,procedure,consultation,appointment` (lead statuses, knowledge text, template names
`appointment_reminder`… and its bookings' `booking_type 'consultation'` are clinic demo data; its tables, columns, events,
notification types and links were renamed); tokens `consultation_booked`, `consultation_attended` (clinic stage keys code still
writes until S6; **S6 removes both tokens**). S5 change-log row marked; the old-word list in `CLAUDE.md` marked.

**Standard checks:** build ✓; full tests ✓; leftover + UI-text guards ✓; `schema.sql` + `seed.sql` + `seed-config.sql` +
`seed-demo.sql` applied twice on an empty DB ✓ (same `real[]` swap; demo shows 16 bookings, 4 calendar syncs, `booking_*`
events, `BOOKING_*` notifications); published app in Staging on the scratch DB: registered a `clinic` client, opened
Dashboard, Bookings calendar, a booking's detail page, Interested People, Inbox, Services, Services/Edit, Business Info,
Calendar Integrations, Campaigns, Campaigns/Create, WhatsApp Templates, Knowledge Base: all 200, no JS errors;
`POST /api/bookings` 201 → event `booking_created`, notification `BOOKING_CREATED` linking `/dashboard/bookings/{id}`,
calendar month lists it; `PATCH …/status attended` → `booking_attended`, lead moved to the clinic `consultation_attended` stage.

**Config / env changes for Render (Mohammad):** none (no appsettings key or env var renamed).

**For later sessions:**
- S6: `LeadStatus.Booked/Attended` and the token allowlist entries; `FilterLabelHelper`/`StatusBadgeHelper`/`inbox.js` still map
  the clinic keys; `_StatusHelp` lead status list; `PatientBookingContext` (patient word).
- S9: n8n AI tools → `/api/ai/bookings/*`, tool names above, result code `EXISTING_UPCOMING_BOOKING`, JSON `booking`,
  `existingUpcomingBookings`, `bookingId`, `bookingType`; `get_client_info` answers `bookingInfo`; services answer
  `bookingDuration`; calendar workflow reads `bookingId` (with `dashboardLink` from S1) and calls back with `bookingId`.
- S10 copy script: `scheduling.appointments` → `scheduling.bookings` (`appointment_type` → `booking_type`),
  `appointment_calendar_syncs` → `booking_calendar_syncs`, every `appointment_id` → `booking_id` (orders, events,
  notifications, campaign recipients, syncs), `consultation_info` → `booking_info`, `consultation_duration` →
  `booking_duration`, `default_consultation_duration_minutes` → `default_booking_duration_minutes`; event types above;
  notification types above and links `/dashboard/appointments/` → `/dashboard/bookings/`; campaigns `audience_type`
  `reactivation_no_consultation` → `reactivation_no_booking`; knowledge category `consultation` → `booking`.
- P2 (portal): `ClientDetail.bookingInfo`, `ClientUpdate.BookingInfo`, `ServiceRow.bookingDuration`, `AppointmentRow` →
  `BookingRow`, lead admin `bookings`, overview/system totals booking counts, event row `bookingId`.
- T1 (tester): `/api/bookings` (`bookingType`), `/dashboard/bookings`, AI `/api/ai/bookings/*`, events `booking_*`,
  audience `reactivation_no_booking`.

## S6 · lead → contact, pipeline stages by category (2026-10-17)

Branch `refactor/generic-platform`, on top of S5 `de497d4`. Committed `0af7e8d`, not pushed.

**Result:** build 0 errors / same 8 old test warnings; full `dotnet test` **1,777 passed**, 0 failed, 0 skipped (+35 new).
Next session's floor: 1,777.

**Renamed (per the map):** `crm.leads` → `crm.contacts` (`status` → `stage_key`, **no default**; `qualification_status` →
`qualification`), every `lead_id` → `contact_id`, index/trigger/check names (`ix_contacts_client_stage_key`); code folder
`Contacts` in every layer (`ContactService`, `IContactService`, `ContactRepository`, `ContactMapper`, `ContactsController`
`/api/contacts`, `ContactAdminService`, `ContactsAdminController` `/api/platform-admin/contacts`, `Contact`, `ContactResponse`,
`ContactRow`, `ContactDetail`…); pages `/dashboard/contacts`, `/dashboard/contacts/{id}` (`Contacts.cshtml`,
`ContactDetail.cshtml`), `contacts.js`; AI `GET/PATCH /api/ai/contacts/{id}`; `MessageSenderType` `contact`; events
`contact_*`, `contact_message_received`; patient → contact everywhere outside the demo seed. Display text renamed blindly;
S8 swaps in `{contact}`.

**Stages by category (the S6 core):** `IBusinessTypeService.GetPipelineAsync` → `ClientPipeline` (`EntryKey` = first open
stage, `MoveTo(current, category)` keeps a stage already in that category, `Resolve(keyOrCategory)`, `NextAfterEntry`).
`LeadStatus` is deleted; no code names a stage key. New contact → first open stage; first outbound reply → the open stage after
the entry stage (a type with one open stage stays); booking made / attended / no-show → booked / attended / no_show; order
confirmed → converted; **AI hand-off → needs_human** (new; staff take-over leaves the stage alone). Dashboard overdue follow-ups
skip closed_lost/converted stages and the reactivation audience's "past new" test uses the type's first open stage (both by
SQL join on `crm.pipeline_stages`). Business-type change endpoint now moves `contacts.stage_key`.

**Renamed or changed beyond the map (decided in S6):**
- `UpdateContactStageRequest {stageKey, qualification, source}`, `POST /api/contacts/{id}/stage` (was `{id}/status`); list
  filters `?stage=` on `/api/contacts`, `/api/dashboard/contacts`, platform-admin `contacts`; responses/rows field `stageKey`;
  platform-admin `PUT contacts/{id}` takes `stageKey`. `UpdateContactContextRequest` (AI) gains `stage` = stage key **or**
  category (unknown → 400 `contacts.errors.unknownStage`).
- Manual/AI stage change events by the new stage's category (booked/attended/no_show/needs_human as before); everything else
  logs **`contact_stage_changed`** `{from, to}` (replaces `lead_contacted` / `lead_qualified`).
- Qualification values (D4): unknown, hot, warm, cold, needs_human, spam (`ContactQualification`); `medical_question` is only a
  clinic stage now (demo seed's Grace moved to stage `medical_question`, qualification `needs_human`).
- `external_lead_id` → `external_contact_id`; `PatientBookingContext` → `ContactBookingContext`; notification `NEW_CONTACT`;
  AI result `CONTACT_NOT_FOUND`; tools in docs `get_contact_context`, `update_contact`.
- Config key `Dashboard:LeadsPageSize` → `Dashboard:ContactsPageSize` (DB row, seed-config insert-if-missing).
- Campaign audience filter JSON `leadStatuses` → `stageKeys`, `qualificationStatuses` → `qualifications`.
- Dashboard summary `SurgeriesBooked` → `OrdersMade` (JSON `ordersMade`, label "Orders made"); `DashboardServiceRow.ContactCount`.
- `FilterLabelHelper` / `StatusBadgeHelper` lost the clinic stage keys; `StageDisplay` shows an unknown key readable, in gray.
  Inbox contact panel: stage badge by category, journey = first stage label of open/booked/attended/converted (from the page's
  pipeline, no clinic words). `_StatusHelp` lists the client's own stages with per-category help (new `stages.help.*` keys in
  `en.json`).
- Leftover guard: the `lead` regex now also catches `ILeadService`-style names (it missed them before); `surgery` also matches
  "surgeries".

**Leftover-names test:** scopes `lead`, `patient`, `surgeon`, `surgery` removed from `PendingScopes` (left: staff, ai_agent,
max_agents for S7). Tokens `consultation_booked` / `consultation_attended` removed. Allowlist: `seed-demo.sql` now also allows
`patient,surgeon,surgery` (clinic demo data). Two clinic stage keys in `seed.sql` carry `leftover-ok` markers; S6 change-log row
marked.

**Standard checks:** build ✓; full tests ✓ (incl. `ContactStageWalkTests`: open → booked → attended → converted through the real
services on Postgres for both `clinic` and `general`); leftover + UI-text guards ✓; `schema.sql` + `seed.sql` + `seed-config.sql` +
`seed-demo.sql` applied twice on an empty DB ✓ (same `real[]` swap; every stored stage key maps to a category); published app in
Staging on the scratch DB: registered a `general` and a `clinic` client, opened Dashboard, Inbox (contact panel), Interested People,
contact page (saved a stage change), Bookings, Campaigns, Campaigns/Create, WhatsApp Templates/Health, Services, Services/Edit,
Knowledge Base (+Settings), Business Info, Staff, Integrations, Calendar Integrations: all 200, no JS errors; each client sees its
own stage labels; `POST /api/contacts/{id}/stage` refuses the other type's key (400). Campaign audience stage filter only covered by
page tests (needs an approved template).

**Config / env changes for Render (Mohammad):** none (no appsettings key or env var renamed).

**For later sessions:**
- S7: `contacts.assigned_staff_id` / `Contact.AssignedStaffId`, `MessageSenderType.Staff`, `staff_message_sent_*`.
- S8: blind display text ("Interested People", "Contact source", "Contact since"…); stage labels/journey already come from types.
- S9: n8n → `/api/ai/contacts/{id}` (tools `get_contact_context`, `update_contact`), `stage` (key or category) instead of
  writing statuses, `qualification` (was `qualificationStatus`; no `medical_question`, hand-off moves the stage itself), every
  `leadId` → `contactId` in payloads/queries, sender type `contact`, result code `CONTACT_NOT_FOUND`.
- S10 copy script: `crm.leads` → `crm.contacts` (`status` → `stage_key`, `qualification_status` → `qualification`;
  `medical_question` → qualification `needs_human` and, for clinic contacts in an open stage, stage `medical_question`),
  `external_lead_id` → `external_contact_id`, every `lead_id` → `contact_id`; `messages.sender_type` `lead` → `contact`; events
  `lead_created` → `contact_created`, `lead_contacted`/`lead_qualified` → `contact_stage_changed`, `customer_message_received` →
  `contact_message_received`; notifications `NEW_LEAD` → `NEW_CONTACT`, links `/dashboard/leads/` → `/dashboard/contacts/`;
  `config.settings` `Dashboard/LeadsPageSize` → `ContactsPageSize`; `marketing.campaigns.audience_filters` keys `leadStatuses` →
  `stageKeys`, `qualificationStatuses` → `qualifications`.
- P2 (portal): `/api/platform-admin/contacts` (`?stage=`), `ContactRow`/`ContactDetail` `stageKey` + `qualification`,
  `ContactUpdate {stageKey, qualification, marketingOptIn, notes}`, every `leadId` → `contactId`, client counts/system totals
  contact names, dashboard `ordersMade`.
- T1 (tester): `/api/contacts` (`POST {id}/stage`), `/dashboard/contacts`, AI `/api/ai/contacts/{id}`.

## S7 · Users, roles, members (one users table) (2026-10-17)

Branch `refactor/generic-platform`, on top of S6 `0af7e8d`. Committed `d5735ad`, not pushed.

**Result:** build 0 errors / same 8 old test warnings; full `dotnet test` **1,841 passed**, 0 failed, 0 skipped (+64 new).
Next session's floor: 1,841.

**Done (per the plan):** `identity.users` (was `identity_users`; + `role` check admin/client/member, `full_name`, `language`
en/ar/fr, `is_active`, `last_login_at`, `created_at`, `updated_at`, trigger) with `user_claims` / `user_logins` / `user_tokens`;
`AppUser : IdentityUser`; `client_users` lost `ux_client_users_user_active` (one active membership per user is checked in
`MemberService.SetMembershipActiveAsync`, with a Postgres test). Identity via `Common/Extensions/IdentityModule.AddOmniIdentity`
(shared by Program and tests): lockout 5 failures / 15 minutes (`AccountRules`, the portal's rule). `AppSignInManager`
(`CanSignInAsync`): only active client/member users with an active membership, password and Google; refused = `NotAllowed`,
shown as the wrong-password text; every sign-in stamps `last_login_at`. Per request: cookie `OnValidatePrincipal` =
`CurrentClientContext.ValidatePrincipalAsync` (no `UserAccess` any more, or a changed security stamp → rejected and signed out).
`ICurrentClientContext.GetAccessAsync` → `UserAccess(UserId, ClientId, Role, Language, FullName, SecurityStamp)`, cached under the
membership key (cleared by saves of the user or a membership). `users.language` now goes before the client default.
Signup creates the `client` user. Members: `/Members` (add with password, deactivate, reactivate; client role only, a member
sees the list read-only), `/api/members` (`GET`, `POST`, `POST {userId}/deactivate|reactivate`; 403 for a member via
`UnauthorizedAccessException`, now mapped by `[ApiErrors]`), seats `max_users` = active client + member users. Entitlements
`max_users` ("Seats", now enforced) and `ai_assistant` ("AI assistant"). Platform admin: `POST auth/login` (generic 401 for
every failure), `GET auth/admins/{id}`, `admins` (list, create ≥ 10 chars, `PUT {id}/active {active, actingAdminId}` (not
yourself, not the last active admin), `PUT {id}/password`), `members` (was `staff`; client/member users only, search by name too,
membership toggle goes through `MemberService`). CLI `dotnet Omni.App.dll create-admin <email> "<name>"` (`ADMIN_PASSWORD`).

**Renamed or changed beyond the map (decided in S7):**
- Full name moved from the `full_name` claim to `users.full_name`; `MemberClaims.FullName` / `MemberClaims.Role` are cookie claims
  only (`AppUserClaimsPrincipalFactory`).
- `contacts.assigned_staff_id` / `bookings.assigned_staff_id` (uuid, unused) → `assigned_user_id` **text** (no FK, nothing sets it).
- Setting `Cache:StaffLookupMinutes` → **`Cache:MembershipsMinutes`** (`CacheArea.Memberships`; seed-config insert-if-missing).
- Mode/trace reasons: `reopened_to_staff` → `reopened_to_human`, `conversation_with_staff` → `conversation_in_human_mode`,
  `staff_replied_from_business_app` → `member_replied_from_business_app`, `plan_without_ai_agent` → `plan_without_ai_assistant`;
  send-trace `kind` `staff` → `member`. Event `source` in seeds `staff` → `member`.
- Dashboard attention `ConversationsNeedingStaff` → `ConversationsNeedingHuman` (JSON `conversationsNeedingHuman`).
- Platform-admin shapes: `MemberRow` (was `StaffRow`) + `Role`, `UserActive`, `LastLoginAt`; `ClientCounts.Members`,
  `ClientRow.MemberCount`, `DailyCount.Members`, `SystemTotals.Users`.
- Inbox labels "Staff" → "Team" (blind; S8 moves them to `en.json`); CSS `inbox-msg-member(-app)`; notification text "AI handed off
  to your team". Prose "staff" → members/the team everywhere.
- `Common/Helpers/EmailAddress.IsValid` shared by signup, members and admins.
- Repo `CLAUDE.md` has a new "Users and roles" standing rule.

**Not done here (portal side, P3):** `admin.admin_audit_log.admin_user_id` → `user_id` and dropping `admin.admin_users` live in the
portal's `admin-schema.sql`, so they belong to P3 (portal login through `auth/login`, admins page on `/admins`). The main app no
longer needs the portal's table.

**Leftover-names test:** `PendingScopes` is now **empty** (staff, ai_agent, max_agents strict). One more change-log row marked
(2026-10-10 conversations row); S7 change-log row marked.

**Standard checks:** build ✓; full tests ✓ (incl. `AccountSignInTests` on Postgres: main-app matrix role × active × membership,
admin-portal matrix, lockout after 5 for member and admin, `last_login_at`, cookie claims, and portal hash compatibility:
a `PasswordHasher<PortalAdminUser>` hash and the demo seed's older v3 hash both verify as `AppUser`, the older one is rehashed);
leftover + UI-text guards ✓; `schema.sql` + `seed.sql` + `seed-config.sql` + `seed-demo.sql` applied twice on an empty DB ✓
(same `real[]` swap; demo has 1 client + 2 member users, plan entitlements `max_users`/`ai_assistant`, sender type `member`);
published app in Staging on the scratch DB: `create-admin` made an admin (duplicate and short password refused); platform-admin
`auth/login` 200 / wrong password 401 / client user 401, `auth/admins/{id}` 200 / 404 for a client user, last-admin deactivate
400, `members` search, old `/staff` 404, no key 401. Registered a `general` client, adding a member without a plan was refused
("subscription isn't active"), with the demo plan (3 seats) added a member (2 of 3 seats), deactivated → that member's sign-in
refused, reactivated → signs in; the admin's sign-in on the main app refused with the wrong-password text; as the member opened
Dashboard, Members (read-only, no add form, `POST /api/members` 403), Inbox, Interested People, Bookings, Services, Services/Edit,
Knowledge Base, Campaigns, Campaigns/Create, WhatsApp Templates/Health, Business Info (+AI tab), Integrations, Calendar
Integrations, Billing: all 200, no console errors, no server errors; demo `nurse@` (member) signs in. Deactivating the member
through the platform-admin API while signed in: the next request was redirected to sign-in and `/api/members` answered 401.

**Config / env changes for Render (Mohammad):** none (no appsettings key or env var renamed). DB setting row
`Cache/StaffLookupMinutes` → `Cache/MembershipsMinutes` (the old row is simply unused; the new one is insert-if-missing).
**Note:** seats are now enforced: with billing on, a plan without `max_users` allows 0 seats, so no member can be added
until each plan gets a `max_users` value (portal Billing → Plans).

**For later sessions:**
- S8: Inbox "Team" labels, notification texts, Members page already uses `en.json` (`members.*`, `nav.members`).
- S9: n8n `sender: "member"` never comes from n8n; nothing else for n8n here.
- S10 copy script: `identity.identity_users` → `identity.users` with `role` = `client` for every old user (old data has no
  members; or `member` for all but each client's first user, if Mohammad prefers), `full_name` from the `full_name` claim (then
  drop those claims), `created_at`/`updated_at` from the membership's `created_at`; `identity_user_claims/_logins/_tokens` →
  `user_*`; `admin.admin_users` rows → `identity.users` (`role` admin, `id` = uuid as text, `user_name`/`email` = email,
  normalized upper-case, `password_hash` as is, `security_stamp` new guid, `lockout_enabled` true, `access_failed_count` =
  `failed_logins`, `lockout_end` = `locked_until`, `last_login_at`, `email_confirmed` true); `admin_audit_log.admin_user_id` →
  `user_id` text; `messages.sender_type` `staff` → `member`; events `staff_message_sent_*` → `member_message_sent_*` and event
  `source` `staff` → `member`; `plan_entitlements` keys `max_agents` → `max_users`, `ai_agent` → `ai_assistant`;
  `contacts/bookings.assigned_staff_id` → `assigned_user_id` (null); `config.settings` `Cache/StaffLookupMinutes` →
  `MembershipsMinutes`; event metadata reasons `reopened_to_staff` / `plan_without_ai_agent` (history only).
- P2 (portal): `/api/platform-admin/members` (was `staff`) and `MemberRow` (+ `role`, `userActive`, `lastLoginAt`),
  `ClientCounts.members`, `ClientRow.memberCount`, `DailyCount.members`, `SystemTotals.users`, entitlement keys `max_users` /
  `ai_assistant` on the plan pages, dashboard `conversationsNeedingHuman`, message sender `member`.
- P3 (portal): sign in through `POST /api/platform-admin/auth/login` (keep the portal's lockout message generic: the API answers
  one 401), per-request `GET auth/admins/{id}` (404 → sign out), Admins page on `/admins` (`AdminRow`; pass `actingAdminId`),
  drop the portal's `create-admin`, `IPasswordHasher<AdminUser>` and `admin.admin_users`; audit `user_id` = the admin's string id.
- T1 (tester): `/api/members`, `/Members`, sender type `member`, entitlement keys.

## S8a · UI strings into en.json: Shared, Account, Dashboard, Inbox, Settings (2026-10-17)

Branch `refactor/generic-platform`, on top of S7 `d5735ad`. Committed `bced45f`, not pushed.

**Result:** build 0 errors / same 8 old test warnings; full `dotnet test` **1,842 passed**, 0 failed, 0 skipped (+1 new).
Next session's floor: 1,842. `en.json` now has 574 keys (was ~100).

**Done (per the plan):** every visible string, `title`, `placeholder`, `aria-*`, `data-au-confirm*` and `data-busy-label` in
`Pages/Shared` (`_AuthLayout`, `_Header`, `_Sidebar`, `_GoogleButton`, `_Messages`, `_NoClient`, `_StatusHelp`,
`_TimeViewSwitch` → "`{Client}` time" / "My time"), `Pages/Account` (Login, Register, CompleteGoogleSignup), `Dashboard/Index`,
`Inbox/Index` and all of `Pages/Settings` (BusinessInfo with its three tabs, Integrations, CalendarIntegrations, Billing) reads
from `en.json` with term placeholders. Their page models' status/error messages are keys too (`…Key` constants + injected
`IStringLocalizer`; keyed exceptions shown with `_text.Localize(ex)`). Checked in the browser: a `general` client sees Contacts /
Services / Bookings / Orders / "Business info"; a `clinic` client sees Patients / Procedures / Consultations / "Clinic info" /
"Team members" on Dashboard, sidebar, Inbox, Business info tabs, Integrations, Calendar, Billing, Members.

**Decided in S8a (beyond the plan):**
- New `Common/Helpers/TextHtml` (`@T.Html("key", args)`): text encoded, `IHtmlContent` arguments (a `LocalTimeHelper.Time` tag,
  `<strong>`/`<code>`) kept as HTML. `UiTextKeysTests` also scans `T.Html("key"`. Repo `CLAUDE.md` UI-text rule mentions it.
- `GoogleLoginSettings.ErrorMessage(code)` → `ErrorKey(code)` (returns a key; Login localizes it).
- `Dashboard.IndexModel.Periods` is a list of values; labels live in the page (`dashboard.period.*`). `RangeLabel` "All time" from
  the localizer. Dashboard/Login/Billing… page models gained an `IStringLocalizer` constructor parameter (tests pass `TextKit.Localizer()`).
- Wording made generic where it named the old product idea: sidebar "Interested People" → `{Contacts}`, "Client setup" →
  `{Client} setup`, "Business Info" → `{Client} info`, brand sub "Client Dashboard" → `{Client} dashboard`, Register "Create your
  client account" → "Create your account", "Client name" → `{Client} name`, Dashboard by-service column "Bookings" (it counted orders)
  → `{Orders}`, "Contact since", "Customer info" → `{Contact} info`, "Client details saved." → "`{Client}` details saved.".
- Day names on Business info shown through `days.*`; the rows' `data-day` stays English (business-info.js finds Monday by it).
- WhatsApp PIN Show/Hide button text comes from `data-show-label` / `data-hide-label`.

**Left for S8b / S8c (still English in code):** every `wwwroot/js` file (inbox.js thread/list rendering, auth.js "Hide/Show
password", notifications.js, local-time.js, business-info.js summary line "Contacts can book…", whatsapp-health.js labels);
labels built by services and shown on these pages (billing entitlement labels, usage/transaction labels, channel "paid by" text,
Identity's own register errors, data-annotation messages). Dates still use `MMM d` invariant formats (Milestone B).

**Standard checks:** build ✓; full tests ✓ (on the throwaway Postgres, port 55433); leftover + UI-text guards ✓ (no pending scopes;
`Pages/**` strict); `schema.sql` + `seed.sql` + `seed-config.sql` + `seed-demo.sql` applied twice on an empty DB ✓ (same `real[]`
swap; demo's `dummy_embedding()::vector` cast swapped to a `real[]` literal); published app in Staging on the scratch DB: registered
a `general` and a `clinic` client, opened Dashboard, Inbox, Business info (details, hours), Integrations, Calendar Integrations,
Billing, Members: all 200, no console errors except Facebook's known "login button needs https" notice, no server failures; saving
Business info shows the localized toast; wrong password shows the localized error. Google completion only covered by tests.

**Config / env changes for Render (Mohammad):** none.

**For later sessions:**
- S8b: reuse the S8a keys where the JS shows the same thing (`inbox.mode.*`, `inbox.unknownName`, `dashboard.whatsApp.*`,
  `integrations.state.*`, `statusHelp.booking.*` labels, `common.*`). `_StatusHelp` booking/qualification labels are local to the
  help card (`statusHelp.*`); `FilterLabelHelper` still has its own English labels.
- S8c: billing/entitlement labels and channel "paid by" strings are built server-side; Register's "already taken" check reads
  Identity's English error text (`Register.cshtml`), so keep that check working when Identity errors move to keys.
- P2 (portal): nothing new (no platform-admin shape changed).

## S8b · UI strings into en.json: Contacts, Bookings, Services, Campaigns, Knowledge, WhatsApp, Status/Error, all JS (2026-10-18)

Branch `refactor/generic-platform`, on top of S8a `bced45f`. Committed `33d9131`, not pushed.

**Result:** build 0 errors / no new warnings; full `dotnet test` **1,849 passed**, 0 failed, 0 skipped (+7: value-label key
cases). Next session's floor: 1,849. `en.json` now has 1,339 keys (was 574).

**Done (per the plan):** every visible string, `title`, `placeholder`, `aria-*`, `data-label`, `data-au-confirm*` in
`Dashboard/Contacts`, `ContactDetail`, `Bookings`, `BookingDetail`, `Services/*`, `Campaigns/*`, `KnowledgeBase/*` (incl.
Websites/Details, Settings), `WhatsApp/Templates`, `WhatsApp/Health`, `Status`, `Error` reads from `en.json` with term
placeholders; their page models' status/error messages are `…Key` constants (keyed exceptions via `_text.Localize`). All 20
`wwwroot/js` files use `omniText.t('key')` (aurora kit, auth, business-info, bookings calendar, campaign create/audience,
multi-select, inbox, inbox-voice, knowledge, meta-connect, notifications bell UI, templates, whatsapp-health/templates;
contacts/sidebar/status-help/local-time had no text). `Pages/**` and `wwwroot/js/**` are fully strict (no pending scopes).

**Decided in S8b (beyond the plan):**
- `FilterLabelHelper` has no English map: `T.ValueLabel(value)` reads `values.{value in camelCase}` (made readable when no
  key); `StatusBadgeHelper.CampaignStatusLabel` / `TemplateStatusLabel` and `KnowledgeCategory.CategoryLabel` are
  `IStringLocalizer` extensions (`knowledge.category.*`; `KnowledgeCategory.All` is now a list of values). `UiTextKeysTests`
  treats `values.*` and `knowledge.category.*` as read by name (like `terms.*`). Repo `CLAUDE.md` UI-text rule says so.
- `LocalTimeHelper.Ago(T, when, …)` takes the localizer (`time.ago.*`). Browser dates in JS use `omniText.lang` (same output in en).
- Wording made generic: "Interested People" → `{Contacts}`; knowledge categories "Doctor" → "Team", "Recovery" →
  "Aftercare" (stored values unchanged); clinic examples in placeholders ("Rhinoplasty…") → neutral ones; WhatsApp Health
  "Meta {status} your account" → "Meta marked your WhatsApp account as "{0}"" (no glued grammar); template status in the
  saved/refreshed messages is the label ("Pending"), not the raw value.
- Notification titles/messages are built and stored by services → S8c (only the bell UI's own text moved here).

**Left for S8c:** notification text built in services (BookingService, MessageService, ConversationService, health
services), billing/entitlement labels, Identity/data-annotation errors, `KnowledgeSimilarityMethod`/`VectorIndexType` labels
(technical, KB Settings read-only panel), calendar `ProviderLabel` (product names).

**Standard checks:** build ✓; full tests ✓ (throwaway Postgres, port 55433); leftover + UI-text guards ✓; `schema.sql` +
`seed.sql` + `seed-config.sql` + `seed-demo.sql` applied twice on an empty DB ✓ (same `real[]` swaps); published app in
Staging on the scratch DB: registered a `general` client (every S8b page 200, no clinic word, no raw key; calendar shows
"Today: no bookings", "business time (UTC)"), signed in as the demo `clinic` client (every page + contact, booking, campaign,
website, KB entry and service detail pages 200; Patients / Procedures / Consultations throughout; calendar, inbox thread,
notifications bell, template side panel + new-template counters, campaign custom/manual audience summary, KB Q&A/counter,
availability summary and auth password hints all render localized text); no server failures, no JS errors (only the two
404s from deliberately fetched missing pages).

**Config / env changes for Render (Mohammad):** none.

**For later sessions:**
- S8c: keep `values.*` for stored values; service-built notifications need keys + args (stored as text today in
  `activity.notifications.title/message`, so they render in the language at creation time unless stored as keys).
- P2 (portal): nothing new (no platform-admin shape changed).

## S8c · Server messages into en.json: exceptions, notifications, billing labels, Identity (2026-10-18)

Branch `refactor/generic-platform`, on top of S8b `33d9131`. Committed `88eee47`, not pushed.

**Result:** build 0 errors / no new warnings; full `dotnet test` **1,866 passed**, 0 failed, 0 skipped (+17). Next session's
floor: 1,866. `en.json` 1,556 keys (was 1,339).

**Done:** every exception a client or member can read in the client-facing services, channel/media providers and the document
engine is thrown with a key (`…errors.*`, `.WithArgs`); `nameof` param names dropped on keyed `ArgumentException`s (they'd append
"(Parameter …)"). Controllers that answer an exception's message themselves (calendar, channels, knowledge, websites) use
`ApiErrorsAttribute.Localize` / new `ApiErrorsAttribute.Text`; calendar + TikTok OAuth redirects use keys (`oauth.*`); sign-up
refusals are keys (`signup.errors.*`, page passes `MaxNameLength` as `{0}`); Identity errors through `LocalizedIdentityErrorDescriber`
(`identity.errors.*`); Register's "email taken" offer now reads `ClientRegistrationResult.EmailTaken` (Identity error code), not
English text. Entitlement refusals keyed (`EntitlementDeniedException.*Key`); `DuplicateRecordException`/`OverlappingRecordException`
default messages keyed. KB Settings "None (exact scan)" keyed.

**Decided in S8c (beyond the plan):**
- `TextArg` (`Entities/Dtos/Ui`): an argument that is itself text: `TextArg.Of("key")` (fallback words, day names) or
  `TextArg.Value(status)` (stored value's label); `JsonStringLocalizer` resolves it. Stored as `[[key:…]]` / `[[value:…]]`.
- Notifications: `INotificationService.CreateAsync(clientId, type, NotificationText text, …)`; `title`/`message` hold keys, new
  column `activity.notifications.text_args text[] not null default '{}'` (in the create table + `alter … add column if not exists`);
  worded per reader in `ListAsync`. Rows written before keys show as stored. Booking titles use terms ("Consultation canceled").
- Billing page: `BillingTextHelper` (`billing.feature.*`, `billing.eventType.*`, `billing.ledger.*`, `billing.paidBy.*`);
  services keep English labels for the portal. `ClientChannelBillingRow` + `providerBilling`, `ProviderDirectUsageRow` +
  `providerBilling`, `channel` (additive JSON fields). `FilterLabelHelper.KeyPart` added.
- Text stored outside a request is worded before saving: campaign recipient `FailureReason`, the AI's book/cancel refusals
  (`BookingService`, `AiController` CONTACT_NOT_FOUND now matches the key).
- New guard `Guards/ServerMessageKeysTests`: an English sentence thrown in client-facing services/providers/document engine fails
  unless its line has `text-ok: <reason>` (13 lines: n8n ingest contracts, setup errors, impossible states). `UiTextKeysTests` also
  reads `TextArg.Of("…")`, `NotificationText.Of("…", "…")`, `text["…"]`, and treats the billing prefixes as read by name.
- Left English on purpose: platform-admin, billing-admin (plans, rate cards, top-ups, provider billing, subscriptions), cache, config
  and benchmark services (portal/n8n readers); provider error text and stored `LastProblemMessage`s; website crawl errors (incl.
  `SsrfGuard`, stored by the crawler); `SlotCheck` / `ContactAlreadyBooked` / `MultipleUpcomingBookings` messages (AI/n8n contract, S9).

**Standard checks:** build ✓; full tests ✓ (throwaway Postgres, port 55433); leftover + UI-text + server-message guards ✓;
`schema.sql` + seeds applied twice on an empty DB ✓ (same `real[]` swaps); published app in Staging on the scratch DB: Register with a
taken email shows "An account with this email already exists. Sign in instead", password mismatch shows the keyed text; registered a
`general` client: Billing page features list localized, Services shows "The booking length must be between 1 and 480 minutes." and
"A service named 'Haircut' already exists."; signed in as the demo `clinic` client: canceling a booking created a notification stored as
keys + text_args and listed as "Consultation canceled / Noah Bennett — Tummy Tuck canceled for …"; KB Settings, Billing, Calendar,
Integrations, Dashboard, Inbox 200; no server errors (only the expected n8n-not-configured warnings).

**Config / env changes for Render (Mohammad):** none. **Schema:** `activity.notifications.text_args` (added by `schema.sql`).

**For later sessions:**
- S9: n8n contract texts (slot errors, AI instructions) and `text-ok` ingest messages; AI reads English with default words today.
- S10: copy script: old notifications keep plain title/message, `text_args` = `'{}'`.
- P2 (portal): billing summary rows gained `providerBilling` (+ `channel` on provider-direct rows), additive; portal copies can ignore.
- Milestone B: `ar.json`/`fr.json` need the new key groups (`*.errors.*`, `notifications.*`, `billing.*`, `identity.errors.*`, `oauth.*`).


## S9 · n8n contracts and workflows (2026-10-19)

Branch `refactor/generic-platform`, on top of S8c `88eee47`. Committed `e80c293`, not pushed.

**Result:** build 0 errors / no new warnings (8, same as before); full `dotnet test` **1,884 passed**, 0 failed, 0 skipped (+18).
Next session's floor: 1,884.

**Done (per the plan):**
- `AiTriggerPayload` gains `clientName`, `businessType`, `language` (client default), `timezone`, `aiProfile`
  (`core.business_types.ai_profile`) and `terms` (the type's six words in the client's default language, defaults filled in).
  `AiTriggerNotifier` adds them from the new `IAiProfileService` (`Business/Services/Ai`) after the plan check; a missing
  client is skipped (trace reason `client_not_found`). Calendar payload unchanged (already `clientId`/`bookingId`/`dashboardLink`);
  the benchmark payload is now a record `BenchmarkGeneratorPayload` (same JSON).
- Workflows in `SculptFlowApp/docs/n8n/`: `omni-ai-assistant.json`, `omni-calendar-sync.json`, `omni-retrieval-benchmark.json`,
  generated from the workspace-root exports by a scratch script. Routes `/api/ai/bookings/*`, query/body `clientId`,
  `contactId`, `bookingId`, `serviceId`; result codes `MULTIPLE_UPCOMING_BOOKINGS`, `CONTACT_NOT_FOUND`, `NO_UPCOMING_BOOKING`.
  System prompt generic: inserts `aiProfile`, `terms` (JSON), timezone (was hard-coded Asia/Beirut), default language; always
  replies in the contact's language. The clinic receptionist + medical-safety text moved to the clinic `ai_profile`; a
  general profile written too (both in `schema.sql`, written only over the placeholder so portal edits survive re-runs).
- Ingest key: every app call uses an n8n **Header Auth credential named "Omni ingest key"** (genericCredentialType /
  httpHeaderAuth); the literal header is gone. Key check (old header value counted in every new and staged file): **0**.
- Contracts in `system-design.html` §7 (7.1–7.4, `data-contract` field lists, import steps). New guard
  `Guards/N8nContractTests`: payload records = documented field lists; each workflow reads only its payload's fields;
  every app call hits a real route + verb with the credential and no `X-Ingest-Key` text; AI tool names and the result
  switches = `AiTestController.ListCodes()`; node references/connections exist; exported inactive, no pinData, no id.
- Tests: `AiProfileServiceTests`, notifier tests check the profile fields, `BotTraceTests` the `client_not_found` skip,
  `BusinessTypeRepositoryTests` that both built-in profiles are filled.

**Decided in S9 (beyond the plan):**
- n8n node names: `search_knowledge`, `get_available_slots`, `get_my_bookings`, `schedule_booking` (kept the S5 name instead of
  the plan's `create_booking`: the endpoint books **or** moves), `cancel_booking`, `handoff_to_human`, `send_message`,
  `result_formatter` (was Basic LLM Chain), webhook nodes `user_message`, `booking_change`, `receive_chunks`, callbacks
  `sync_callback`, `send_questions`. Agent decision JSON: `bookingId`, `serviceId` (were appointment/procedure ids).
- Workflow fixes made while renaming: `get_available_slots` now passes `contactId` (the prompt relied on the booking context
  it returns); `cancel_booking` passes the AI's `bookingId`; `handoff_to_human` takes the reason from the agent's decision
  (`$fromAI` doesn't work outside a tool node); JSON bodies built as objects (quotes in text no longer break them); the
  callbacks send `traceparent` + `X-N8n-Execution-Id` (closes the logging plan's n8n item); calendar: timezone from the
  payload, description = `eventDescription` + `dashboardLink`, Outlook uses `subject`/`body`, Outlook cancel URL fixed
  (had "DELETE " inside it).
- New webhook paths, no workflow `id`/`versionId`/`instanceId`, exported inactive, benchmark `pinData` dropped: imports
  create new workflows next to the old ones instead of overwriting them.
- AI-facing texts (`AiResultInstructions`, slot errors, `text-ok` ingest messages) stay English with generic words; the AI
  rewords them for the contact using `terms`.
- Fixed a leftover hit S8c committed: `ServerTextTests.ClinicWords` → `FirstTypeWords`.

**Standard checks:** build ✓; full tests ✓ (throwaway Postgres, port 55433); leftover + UI-text + server-message + n8n contract
guards ✓; `schema.sql` + `seed.sql` + `seed-config.sql` + `seed-demo.sql` applied twice on an empty DB ✓ (same `real[]` swaps;
an admin-edited `ai_profile` survived a third `schema.sql` run); published app in Staging on the scratch DB with
`N8n:AiWebhookUrl` pointed at a local listener: a signed WhatsApp message to the demo client reached it with
`clientName`, `businessType: clinic`, `timezone`, the clinic `aiProfile` and clinic `terms`; registered a `general` client
and opened dashboard, inbox, contacts, services, knowledge (200, no JS errors); no server errors.

**Config / env changes for Render (Mohammad):** no key names change. At import (plan §7 step 9): create the "Omni ingest key"
Header Auth credential, set the base URL, and set `N8n:AiWebhookUrl`, `N8n:CalendarSyncWebhookUrl`,
`N8n:KnowledgeBenchmarkWebhookUrl` to the **new** webhook URLs (new paths). Then rotate the ingest key and clean the
workspace-root `docs/n8n/` exports (they still hold the old key).

**For later sessions:**
- S10: `core.business_types.ai_profile` in the new project comes from `schema.sql`; don't copy old placeholder profiles over it.
- S11: `docs/n8n/*.json` carry the Render host `sculptflowapp.onrender.com` (allowlisted token) until the services are renamed.
- P2 (portal): nothing new (business type `aiProfile` was already editable there).
- Milestone B: the prompt already replies in the contact's language; `terms` follow the client's default language, so
  `ar`/`fr` business-type words feed straight into the prompt once filled.
## P1 · Portal groundwork: Omni names (2026-10-19)

Branch `refactor/generic-platform` in `SculptFlowAdmin`, created from `main` fa33015. Commit `f9d5434`, not pushed.

**Result:** `dotnet build Omni.Admin.slnx` 0 errors / 0 warnings. The portal has no test project, so there are no tests to count.
Published and run in Staging against a scratch Postgres (port 55434, `admin-schema.sql` applied, admin made with `create-admin`
and `ADMIN_PASSWORD`): sign-in works, cookie is `omni.admin`, title "Omni Admin". Pages that read the main app redirect to
Unavailable because no main app / `MainApp:PlatformAdminApiKey` was configured (S10 was running in the main app); Billing pages
and Login open 200. **Not done:** opening the pages against a running P1-era main app; do that in P2.

**Renamed:** `SculptFlowAdmin.slnx` / `SculptFlowAdmin/` / `.csproj` -> `Omni.Admin.*`; namespaces `Omni.Admin.*`; Dockerfile runs
`Omni.Admin.dll`; brand text "SculptFlow Admin" / "SculptFlow" / "MySculptFlow" -> "Omni Admin" / "Omni"; auth cookie
`.SculptFlowAdmin.Auth` -> `omni.admin`; README, CLAUDE.md (plus a rename-guard section), MAIN_APP_SYNC.md, UI_GUIDE.md, `.gitignore`.
`UserSecretsId` unchanged. Beyond the plan: `ChargedBySculptFlow` -> `ChargedByPlatform` (`ClinicChannelBillingRow`) and
`ProviderCostPaidBySculptFlow` -> `ProviderCostPaidByPlatform` (`BillingReport`), as S1 asked, so billing values do not read false/0;
`AppLogDisplay` doc and placeholder email text changed. Checkout-folder names `SculptFlowApp` kept (D6).

**Guard:** `scripts/check-leftover-names.ps1` + `scripts/leftover-names-allowlist.txt` (run from the repo root with
`powershell -File scripts/check-leftover-names.ps1`). Strict: sculptflow, plasticsurgery, surgeon, max_agents. Pending (hits now):
clinic 953, lead 244, procedure 50, appointment 66, staff 158, patient 9, surgery 3, consultation 23, ai_agent 1. A pending word with
no hits fails the script. Allowed tokens: `SculptFlowApp` (checkout folder), `sculptflowapp.onrender.com` (live host).

**Config for Render (Mohammad):** the portal's Dockerfile now runs `Omni.Admin.dll` (same Dockerfile path, nothing to change unless Render
overrides the start command). Admins are signed out once (new cookie name). No appsettings key or env var changed.

**For P2/P3:** `MAIN_APP_SYNC.md` still records the old last-synced commit (P2 updates it). Routes, shapes and `admin-schema.sql` untouched.

## S10 · Data copy script (2026-10-19)

Branch `refactor/generic-platform`, on top of S9 `e80c293`. Committed `bb95a8c`, not pushed. No schema or app code changed.

**Result:** build 0 errors / same 8 warnings; full `dotnet test` **1,886 passed**, 0 failed, 0 skipped (+2: `Guards/CopyScriptCoverageTests`).
Next session's floor: 1,886. `Database/migrations/2026-10-generic/test-copy.ps1` passed **twice from scratch** (about 20 s each).

**Files** (all in `SculptFlowApp/Database/migrations/2026-10-generic/`): `copy-from-old.sql` (the script), `test-copy.ps1` (the test),
`test-copy-assert.sql` (39 assertions), `test-old-extras.sql` (old-side cases the demo seed lacks). Plus `CopyScriptCoverageTests`,
a "Database" rule in the repo `CLAUDE.md`, and `system-design.html` §8.5 + change-log row.

**How the script works:** run with `psql` against the NEW database (after `schema.sql` + `seed-config.sql`; not the demo seeds):
`psql "<new conn string>" -v old_host=... -v old_password=... [-v old_dbname=postgres -v old_user=postgres -v old_port=5432
-v old_sslmode=require] [-v force=1] -f copy-from-old.sql`. One transaction: postgres_fdw server + user mapping, old schemas
imported as `old_*`, checks, 47 `insert … select` (FK order) + users/claims/settings/audit log, identity sequences moved past the
copied ids, row-count report (old / expected / new per table, 51 rows) and `Result: ok|mismatch`. Any error or mismatch rolls
everything back; the foreign server, its password and the `old_*` schemas are dropped before the commit. Refuses to run when a
target table has rows; `-v force=1` truncates the copied tables first. The file header documents all of this.

**Mappings beyond the plan (decided here):**
- Cycle `crm.messages.campaign_recipient_id` ↔ `campaign_recipients.message_id`: messages first, link set by an `update` afterwards.
- Event `lead_contacted` / `lead_qualified` → `contact_stage_changed` with metadata `{to: contacted|qualified}` merged into the old metadata
  (no `from`: the old log did not keep it). `consultation_cancelled` → `booking_canceled`.
- `medical_question` qualification → `needs_human` always; the stage becomes `medical_question` only when the old status is an **open**
  stage of the clinic pipeline (read from `crm.pipeline_stages`, not hard-coded).
- Roles: earliest membership of each clinic (by `created_at`, then id) = `client`, everyone else `member`; a user with no membership is
  `member` (listed in the report). `users.is_active` true, `language` null, `last_login_at` null, `created_at` = earliest membership.
  Only `full_name` claims are dropped (they become `users.full_name`); other claims are copied.
- Admins: `id` = uuid as text, `user_name`/`email` as typed, normalized = upper-case, same hash, new security/concurrency stamps,
  `lockout_end` = `locked_until`, `access_failed_count` = `failed_logins`, `is_active` kept. Aborts if an admin email equals a client user name.
- `admin.admin_audit_log`: the script creates it in the P3 final shape (`user_id text` → `identity.users`, `client_id`) if missing; P3's
  `admin-schema.sql` must define the same shape (the script refuses a database that still has `admin_user_id`).
- Config: only settings changed by hand (`updated_by` is not `seed`) are copied, under the new key names
  (`ClinicsMinutes`→`ClientsMinutes`, `ProceduresMinutes`→`ServicesMinutes`, `StaffLookupMinutes`→`MembershipsMinutes`,
  `LeadsPageSize`→`ContactsPageSize`, `MaxSourcesPerClinic`→`MaxSourcesPerClient`), overwriting the seeded default; keys that no longer
  exist are listed and skipped. `WebScraping/UserAgent` keeps the new `OmniBot` seed unless it was changed by hand.
- Also mapped: `app_logs.category` prefix `PlasticSurgery.` → `Omni.App.`, trace step `webhook.clinic_unresolved`, usage `rate_source`
  `clinic` → `client`, knowledge website sources' category, notification links `/dashboard/appointments|leads/` → `bookings|contacts/`.
  Old notifications keep their plain title/message (`text_args` default `{}`); `assigned_staff_id` is dropped (nothing set it).
- Not copied: `core.business_types`, `crm.pipeline_stages` (and the AI profiles in them): they come from the new `schema.sql`.

**Test (`test-copy.ps1`, needs a local PostgreSQL install; not part of `dotnet test`):** fresh cluster on port 55444 in a temp folder;
database `old` = `main`'s `schema.sql`, `seed-config`, `seed`, `seed-demo` (vector → `real[]` swap) + the portal's `admin-schema.sql`
(read from the `SculptFlowAdmin` checkout) + `test-old-extras.sql`; database `new` = this branch's schema + `seed-config`. It checks:
missing variables refused; a run into a database without `seed-config` fails and leaves no foreign server/schema/rows; run 1 passes
(51 tables ok, 39 assertions); run 2 without force is refused and changes nothing; run 3 with `force=1` passes the same assertions;
then publishes `Omni.App`, runs it in Staging on `new` and signs in the migrated owner (dashboard opens) and a member, refuses a wrong
password and an admin on the client app, logs the migrated admin in through `/api/platform-admin/auth/login` and gets 401 for a
locked admin, an inactive admin, a wrong password and a client user. Test password = the demo seed's documented placeholder.

**Standard checks:** build ✓; full tests ✓; leftover-names guard ✓ (the folder is allowlisted; one design-doc paragraph reworded);
`schema.sql` and the seeds are unchanged by this session, so the apply-twice check from S9 still holds; the app boot/sign-in check is
part of `test-copy.ps1`. Staged diff scanned for secrets: only placeholders (`db.<old-ref>.supabase.co`, a random per-run test API key).

**What Mohammad must do himself (when he really migrates):**
1. Create the new Supabase project, run `Database/schema.sql` then `Database/seed-config.sql` (not the demo seeds) in its SQL editor.
2. From a machine with `psql`, run `copy-from-old.sql` with the **old** project's direct-connection host, user and password as `-v`
   variables (Supabase: Settings → Database → Connection string, **not** the pooler). Read the owner list and the settings list in the
   output, check `Result: ok`; then run `analyze;` in the new project.
3. Pause writes on the old project first (the copy is a snapshot), sign in with one real user per client afterwards, and rotate the
   old project's keys when retiring it.
4. Supabase has pgvector, so the `vector` columns go through postgres_fdw as text; only the local test swaps them to `real[]`.
   This was not tested against real Supabase (no access from here); if `create extension postgres_fdw` or the connection is refused,
   the error stops the run with nothing saved.

**For later sessions:**
- Any `schema.sql` change to a copied table (S11 included) must update `copy-from-old.sql`, `test-copy-assert.sql`, `test-old-extras.sql`
  and run `test-copy.ps1`; the coverage guard only catches added/removed tables.
- P3: the portal's `admin-schema.sql` must create `admin.admin_audit_log` as `(id, user_id text null → identity.users, admin_email,
  action, entity_type, entity_id, client_id, details, created_at)` and drop `admin.admin_users`; the portal's login can be tested against
  the data `test-copy.ps1` builds (admin `admin@omni-test.test`, password `Demo-ChangeMe-2026`; use `-Keep` to keep the cluster).
- S11: `copy-from-old.sql` mentions the old names on purpose (allowlisted folder); the CLAUDE.md "Database" rule and §8.5 describe it.
- Noticed, not touched: `docs/system-design.html` has an S8c change-log row sitting inside the §8.4 vocabulary table (line ~1146).

## P2 · Portal sync: shapes, pages, business types (2026-10-19)

Branch `refactor/generic-platform` in `SculptFlowAdmin` (`Omni.Admin`), one commit on top of `f9d5434`, not pushed. Synced to main app `bb95a8c`.

**Result:** `dotnet build Omni.Admin.slnx` 0 errors / 0 warnings (the portal has no test project). Leftover guard: every old word is now strict and
passes (the guard script is allowlisted as the one file that may hold them). Published the portal and the main app (from a `git archive` of
`bb95a8c`, so S10's files were untouched) in Staging on a scratch Postgres (port 55441, main-app schema + seeds, `admin-schema.sql` applied twice):
all 47 portal pages open (200; detail pages with real ids), no JS console errors. Writes checked in the browser: create a business type
(duplicate of `clinic`, plus an added stage), refused save keeps the form with the main app's message, change a client's business type
(preview, confirm: stages moved, audit row written), edit a contact's stage.

**Renamed (mechanical, then checked against the main app's `PlatformAdmin` DTOs):** folders/pages/services/clients/DTOs/enums
`Clinics→Clients`, `Leads→Contacts`, `Staff→Members`, `Procedures→Services`, `Appointments→Bookings`; routes `/api/platform-admin/{clients,members,contacts,content/services}`;
benchmark route `knowledge-benchmark/clients/`; audit `client_id` (entity, schema); cookie scope lookup `ClientScopeLookup`.
Shapes updated to match the main app: `ContactRow/Detail/Update` (`StageKey`, `Qualification`), `ClientDetail` (+`BusinessTypeCode`, `DefaultLanguage`),
`MemberRow` (+`Role`, `UserActive`, `LastLoginAt`), `ClientCounts.Members`, `DailyCount.Members`, `SystemTotals.Users`. Contact list filter is `?stage=`.
Vocabularies copied from the main app: `EventTypes`, `OrderStatus`, `NotificationType`, `ContactQualification`; `ContactStatus`/`LeadStatus` removed (stages are per business type).

**New:** `/BusinessTypes` (list, duplicate, activate), `/BusinessTypes/Edit` (names, terminology per language, stages, AI profile), `/Clients/BusinessType`
(pick, preview counts, confirm), stage pickers on Contacts (filter from all types) and Contact details (the client's type). Nav: Setup > Business types.
**Not done:** a business-type picker "on client create": the portal has no client-create page (clients sign up in the main app), so there is nothing to add it to.

**Mohammad must do:** run `Database/admin-schema.sql` on the real database (it renames `admin.admin_audit_log.clinic_id` to `client_id` and its index;
tested on an old-state DB and on a fresh one). No appsettings keys or env vars change.

**Left as is:** pre-existing drift in `ClientChannelBillingRow` (main adds `ProviderBilling`), `ProviderDirectUsageRow` (`ProviderBilling`, `Channel`),
`EntitlementDefinition.Kind` (enum in main): not part of the rename, UI doesn't use them. `docs/UI_GUIDE.md`, README, `MAIN_APP_SYNC.md` updated.
For P3: `admin.admin_users` and the portal login are untouched.

## P3 · Portal login and admins through the main app (2026-10-20)

Branch `refactor/generic-platform` in `SculptFlowAdmin` (`Omni.Admin`), on top of P2 `788e6ad`. Commit `df82088`, not pushed.
Main app untouched (still `bb95a8c`).

**Result:** `dotnet build Omni.Admin.slnx` 0 errors / 0 warnings. New test project `Omni.Admin.Tests` (xUnit + Moq, no database):
**51 passed**, 0 failed, 0 skipped. Next portal session's floor: 51. Leftover guard OK (already fully strict since P2).

**Done (per the plan):** sign-in posts to `POST /api/platform-admin/auth/login` (`AuthApiClient`, `AdminAuthService.SignInAsync`); the portal
issues its own `omni.admin` cookie (id = the `identity.users` id as text, name, email); every request re-checks `GET /auth/admins/{id}`
(`ValidatePrincipalAsync`, a "yes" cached 1 minute in `IMemoryCache`; a "no" rejects and signs out; a changed name/email renews the cookie).
Admins page → `/api/platform-admin/admins` (`AdminsApiClient`, `AdminAccountService`, which audits and, on deactivate, drops the cache so the
admin is out on their next click). Removed `AdminUser`, `AdminUserRepository`, `IPasswordHasher<AdminUser>` (and the Identity EF package),
the `create-admin` engine; `IAdminUnitOfWork`/`AdminDbContext` keep only the audit log; audit `user_id` is text.

**Decided here:**
- Main app down or misconfigured at sign-in: one inline message, no URL or key detail on the anonymous page (logged instead). Every refusal
  (wrong password, locked, inactive, client/member) shows the main app's one generic text.
- Main app unreachable during the per-request check: the cookie is **kept** (every page but the audit log needs the main app anyway and
  shows Unavailable); a "no" or the API being off signs out.
- The login 401 is told apart from a wrong portal key by the key filter's message (it names `X-Platform-Admin-Key`); a wrong key reads as a
  configuration error, not a wrong password.
- `admin-schema.sql` rewritten: requires `identity.users` (run after the main app's `schema.sql`); old-shape blocks first (`clinic_id` rename,
  then `admin_user_id` uuid → `user_id` text and `drop table admin.admin_users`, **refused** while any admin is missing from `identity.users`
  with role admin, so no account is lost); then the final `create table` identical to `copy-from-old.sql`'s, the FK for a moved table,
  indexes, RLS.

**Schema checks (throwaway cluster from `test-copy.ps1 -Keep`, which passed):** the copy-created `new` DB is unchanged by running
`admin-schema.sql` twice; a fresh DB (branch `schema.sql` + `admin-schema.sql` twice) and an old-shape DB migrated by it give the **identical**
`admin.admin_audit_log` (columns, defaults, PK, FK `→ identity.users(id) on delete set null`, both indexes, RLS) to the copy's; the migration
refuses while one admin is missing and keeps the old table; without `identity.users` it stops with a clear error.

**Browser check (published main app + portal on the migrated `new` DB):** migrated `admin@omni-test.test` signs in (placeholder password from
the copy test); wrong password, locked admin, client user all show the generic message; Admins page lists the 3 migrated admins; add an admin,
short password refused with the main app's message, password reset unlocks the locked admin, activate/deactivate; audit rows carry text
`user_id`; Dashboard, Clients, Audit, Members, Admins open 200, no console errors. Deactivating the signed-in admin through the main app's API →
after the minute, the next click lands on sign-in. Main app stopped → sign-in shows the inline "isn't available" message.

**Mohammad must do:**
1. **Do not run the portal's `admin-schema.sql` on the old (live) database.** P2's entry said to; if you already did, the live old portal
   (which still maps `clinic_id`) and the S10 copy (which reads `old_admin.admin_audit_log.clinic_id`) both break, so tell the next main-app
   session to make `copy-from-old.sql` accept `client_id` too. On the new project the copy already creates the table; running the file there
   afterwards is harmless (no-op).
2. On Render: the portal now **needs** `MainApp__PlatformAdminApiKey` and `MainApp__PublicBaseUrl` (or `ApiBaseUrl`) to sign anyone in
   (before, only Billing/Configuration needed them). Admins are signed out once if their cookie id isn't in `identity.users`. No new keys.
3. First admin on a fresh project: the main app's `create-admin` (the portal's is gone).

**Left / for later:** `docs/system-design.html` (main app repo) not touched for the portal login change: S11's sweep. Pre-existing, not changed:
after a POST the Admins page URL keeps `?handler=…` (the PRG redirect copies the query); harmless. T1: the portal's sign-in can now be tested
end to end against the main app (auth + admins endpoints).

## S11 · Docs sweep and final verification (2026-10-20)

Main app `refactor/generic-platform` commit `2b72d25` (on `bb95a8c`); portal `refactor/generic-platform` commit `7fed4f0`
(on `df82088`). Not pushed. No code changed in either repo. Neither `main` had new commits, so nothing was merged.

**Checks:** main app build 0 errors / same 8 warnings; full `dotnet test` **1,886 passed**, 0 failed, 0 skipped (before and
after the doc edits; includes every guard, the old-names pending list is empty). `test-copy.ps1` passed **twice** from scratch.
Portal build 0 errors / 0 warnings; `Omni.Admin.Tests` **51 passed**; leftover guard OK. Tester (T1) skipped by Mohammad's
decision: it still calls the old routes. Not re-run here: the schema apply-twice and browser checks (no schema, seed or code
change since S9/S10/P3, which ran them).

**Docs:** new `SculptFlowApp/docs/deploy-runbook.md` = the one ordered list for both apps (never run `admin-schema.sql` on the
old project; never demo seeds on the new one; new project `schema.sql` → `seed-config.sql` → copy → `analyze` →
`admin-schema.sql` no-op; staging; cutover with `force=1`; after). `system-design.html`: removed four stray S8c change-log copies
(in §6, §8.4, §9, §10), fixed two broken rows (`billing.ledger_entries`, `Billing:WhatsApp:*`), §2.4 deploy, §4.6 testing,
§5.10 portal sign-in, §7.4 new ingest key, §8.5 order, §9 79 settings, §10 tests row, §11 note resolved, change-log row.
`configuration.md` count 60 → 79 + renamed-settings table; `billing.md` go-live uses settings, not `Billing__*` env vars, and
seats need `max_users`; README (roles, seeds, tests); both `CLAUDE.md`. Workspace `milestone-a-plan.md` §7 steps 6–7 marked
superseded (they listed `seed.sql` and an edited copy file).

**Open for Mohammad:** everything in the runbook. Milestone A is complete except T1.
