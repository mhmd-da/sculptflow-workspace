# Making SculptFlow generic for any business — assessment

Checked 2026-10-09 against `SculptFlowApp` `main` (the `refactor/architecture` branch is already merged into it) and
`SculptFlowAdmin` `main`. Counts are `git grep -i` hits, so they're rough (e.g. "lead" also matches words like "leading").

## Verdict

**Medium-to-large job, and the hard part isn't the names.** Keep the internal names (`clinic_id`, `Lead`, `Procedure`…)
and add a per-business-type terminology layer for everything a user or the AI sees. Separately, the lead/appointment
status vocabularies are medical workflow (`consultation_booked`, `surgery_booked`, `medical_question`) and need to become
configurable stages; labels alone can't fix that.

Rough size: **~3–4 weeks** for the recommended approach, versus **~6–8 weeks** plus a risky data migration for a full
rename. These are inferred from the counts below, not measured.

## Where clinic-specific concepts live

| Area | What's there | Size |
|---|---|---|
| DB schema (`Database/schema.sql`) | Tables `core.clinics`, `core.procedures`, `crm.leads`, `scheduling.procedure_bookings`, `identity.clinic_users`, `scheduling.clinic_availability_*`, `clinic_booking_settings`; `clinic_id` on nearly every table (96 refs), `lead_id` (15), `procedure_id`, `assigned_staff_id`; columns `consultation_info`, `consultation_duration`, `default_consultation_duration_minutes` | 46 tables, ~30 named constraints/indexes/triggers with clinic/lead/procedure in the name |
| Status vocabularies (behavior, not text) | `crm.leads.status` check: `consultation_booked`, `consultation_attended`, `surgery_booked`…; `qualification_status`: `medical_question`; `appointments.appointment_type` default `'consultation'`; `procedure_bookings.status` (`quoted`, `deposit_paid`…). Mirrored in `Common/Enums/LeadStatus.cs`, `LeadQualificationStatus.cs`, `StatusBadgeHelper`, `FilterLabelHelper`, `EventTypes`, `inbox.js` | Small number of files, but it's the funnel logic and reporting |
| Entities / services / repos | Namespace `PlasticSurgery.*` everywhere; `Clinic`, `Lead`, `Procedure`, `ProcedureBooking`, `ClinicUser`, `CurrentClinicContext`, `LeadsService`, etc. | "clinic" in ~500 of ~880 tracked files (5.5k hits); "lead" ~180 files; "procedure" ~110; "appointment" ~130 |
| UI text (Razor `Pages/` + `wwwroot/js`) | Hard-coded English, no localization/resx layer at all. "Glow Aesthetic Clinic" signup placeholder; Swagger title "Plastic Surgery Clinic API" | ~950 hits (lead 409, clinic 189, procedure 125, appointment 106, patient 42, consultation 34) |
| n8n AI tools (`Controllers/Integrations/AiController.cs`) | Tool endpoints `api/ai/clinic-info`, `procedures`, `leads/{id}`, `appointments/*`; params `clinicId`; response `ConsultationInfo`; doc comments reference tools like `search_clinic_knowledge` | 12 endpoints; the contract n8n depends on |
| n8n prompts / workflows | **Not in either repo** — they live in n8n itself, so I couldn't read them. They'll certainly be clinic/surgery-specific and need a per-business-type prompt template | Unknown until exported |
| Procedures / appointments | "Procedure" = the service catalog; `procedure_bookings` = quote → deposit → booked → completed (fits generic "service orders"). Appointments are already fairly generic apart from the `consultation` type | Mostly naming |
| Billing | Already generic: keyed by `clinic_id`, no medical concepts. Only the tenant name changes | Low |
| Admin portal | Mirrors main-app shapes; pages `Clinics`, `Leads`, `Procedures`, `Staff`, `Appointments`; own tables `admin.admin_audit_log.clinic_id` | ~150 files mention clinic (944 hits), lead ~54 files |
| Tests | `PlasticSurgery.Tests` | ~56 files mention clinic |

## Option A — full rename in code and DB (client/customer/agent/service)

- Every table, column, constraint, index, trigger, enum, entity, service, route, JSON field, JS file and portal copy changes.
  The repo rules (schema-qualified raw SQL, re-runnable `do $$` renames) make it doable but long.
- Live Supabase data: `alter table … rename` is cheap and keeps rows, but every deployed thing must switch at once: the app,
  the portal, n8n workflows (routes and field names), any external webhook/OAuth callback URLs that contain old names,
  and seed/demo SQL. A missed raw SQL string fails only at runtime.
- No product gain over Option B: a dental clinic still wants to see "patient", a gym wants "member". A rename just swaps
  one hard-coded vocabulary for another.

## Option B — keep internal names, add terminology per business type (recommended)

1. **Business type + terminology**: `core.clinics.business_type` (e.g. `aesthetic_clinic`, `dental`, `salon`, `generic`)
   and a terminology table/config (`tenant`, `customer`, `customers`, `agent`, `service`, `consultation`…), with optional
   per-tenant overrides. Read through a cached `ITerminologyManager` (fits the existing `ICacheManager`/`ConfigManager`
   rules). Existing tenants default to the current clinic wording, so nothing changes for them.
2. **UI extraction**: replace the ~950 hard-coded strings in Razor/JS with terminology lookups (Razor helper + a JSON
   blob for JS). This is the bulk of the work and is mechanical.
3. **Configurable pipeline**: move lead statuses/qualification values and appointment types from check constraints and
   enums into per-business-type stage definitions (keep stable internal keys; map `surgery_booked` → a generic
   `service_booked` key with a migration). This is the only part that touches live data and reporting.
4. **AI contract**: keep the endpoint routes, add `businessType` + terminology to `clinic-info`, and turn the n8n prompts
   into a template filled per business type. Needs the workflows exported from n8n first.
5. **Admin portal**: show/edit business type and terminology through `/api/platform-admin/clinics`; its own labels can stay
   internal ("Clinics" or rename to "Tenants" in the portal only).
6. **Cheap outward fixes**: Swagger title, signup placeholder, product copy. Renaming the `PlasticSurgery` namespace/project is
   optional and purely cosmetic; skip unless you want it.

## Risks

- **Status migration** (step 3) changes stored values in `crm.leads`, and anything that filters or reports on them
  (dashboard counts, campaign audiences, event types, n8n). Needs a guarded migration and a check of every consumer.
- **n8n prompts are outside the repos**: unseen scope; the AI is the most clinic-flavored part ("medical_question"
  handoff, consultation booking).
- **Medical-specific behavior** (medical-question handoff, consent wording) should stay on for clinic types; making it
  generic must not weaken it for clinics.
- **Missed strings**: no localization layer exists, so coverage relies on grep plus a UI pass. The Automation tester can
  run one generic-business clinic per run to catch leftovers.
- **Supabase data**: Option B needs only additive columns plus the one status-value migration; Option A renames every
  table live and needs a coordinated cut-over of app, portal and n8n.
