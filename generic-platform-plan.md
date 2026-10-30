# Going generic — how to proceed

Follows [generic-business-assessment.md](generic-business-assessment.md). Assumes no real clients or data yet, so the
code and database can be renamed freely. Sizes are my estimates, not measured.

## The short answer

Neither option on its own. Build **one neutral platform with switchable modules**, and offer **business-type presets** on
top as a convenience:

- **Neutral by default.** Every workspace starts with plain, generic words: Contacts, Team, Services, Bookings, Pipeline.
  A technical client or a campaigns-only client never sees "patient" or "procedure", and never has to configure naming.
- **Modules, not business types, decide what exists.** Bookings, Services, Pipeline, AI agent, Knowledge base and
  Campaigns are each switched on or off per workspace. A campaigns-only client has Contacts + Channels + Campaigns and
  nothing else in the menu, the dashboard or the AI tools.
- **Presets are just starting setups.** "Clinic" is a preset that switches on Bookings + Services + Pipeline + AI agent,
  seeds clinic pipeline stages, renames a handful of nouns (Contact → Patient, Service → Procedure) and picks the clinic
  AI prompt. After signup the workspace owns its settings; nothing in the code checks "is this a clinic?".

Terminology overrides are therefore optional and small: a few nouns, not a full translation layer per business.

## Core vs modules

| Always on (core) | Optional modules (per workspace, gated by plan) |
|---|---|
| Workspace (the tenant), team members, roles | **Pipeline**: configurable stages for contacts (replaces lead status / qualification) |
| Contacts: name, phone, email, tags, custom fields, opt-in/out, source | **Services**: catalog of what the business sells (today's procedures) |
| Channels (WhatsApp, Telegram, TikTok…), inbox, conversations, messages | **Bookings**: availability, appointments, calendar sync (needs Services optional, not required) |
| Activity log, notifications | **Deals / orders**: quote → deposit → completed (today's procedure_bookings) |
| Billing, usage, API keys | **AI agent** + **Knowledge base** (tools offered match the modules switched on) |
| | **Campaigns**: lists/segments of contacts, templates, sends |

The plan entitlements already work this way (`EntitlementKeys`: `campaigns`, `ai_agent`, `api_access`,
`advanced_reporting`, `max_agents`…), so modules extend an existing mechanism: a module is on when the plan allows it and
the workspace has enabled it.

## Internal naming (rename now, while there's no data)

| Today | Proposed internal name | UI default | Note |
|---|---|---|---|
| clinic, `clinic_id` | **workspace**, `workspace_id` | Workspace / Business | Avoid "client" (clashes with `Controllers/Client` and reads like "customer") and "account" (clashes with `billing.accounts`) |
| lead, patient | **contact** | Contact | One table; "lead" becomes a pipeline stage, not a table |
| staff, clinic_users | **member** (`workspace_members`) | Team member | Avoid "agent": it collides with the AI agent and the `max_agents` entitlement. Use "agent" only as a UI label if wanted |
| procedure | **offering** (`catalog.offerings`) | Service | Avoid "Service" internally: it clashes with the `Business/Services` layer (`ServiceService`) |
| appointment | **booking** | Booking / Appointment | |
| procedure_booking | **deal** (or order) | Deal | |
| lead status / qualification | **pipeline_stages** table per workspace + contact `stage_id`; qualification → tags or score | Pipeline | Removes `surgery_booked`, `medical_question` from code |
| consultation_info, consultation_duration | `booking_info`, `default_duration_minutes` | | |
| `PlasticSurgery` namespace/project | e.g. `SculptFlow.App` | | Also worth deciding if "SculptFlow" stays as the brand, since it reads as aesthetics |

## Contacts for campaigns-only clients

Campaigns today target leads (`marketing.campaign_recipients.lead_id`). After the rename they target contacts, and to
serve campaign-only clients contacts need what any campaign tool has:

- Import (CSV) and API create/update, deduped on phone/email
- Tags, custom fields (field definitions per workspace + `jsonb` values), lists and saved segments as campaign audiences
- Opt-in/out per channel (exists today as `marketing_opt_in` / `opted_out_at`)
- No pipeline, booking or service fields required

## Other areas

- **UI**: the menu, dashboard cards and filters come from enabled modules. A small terminology helper (Razor + one JSON
  blob for JS) supplies the few overridable nouns; everything else is plain neutral text.
- **AI (n8n)**: one base prompt + preset add-ons (clinic: medical-question handoff, consultation booking) + the
  workspace's own instructions. Tool list depends on modules (no booking tools if Bookings is off). The workflows must be
  exported from n8n first, since they aren't in either repo.
- **Admin portal**: workspaces page shows preset, modules and term overrides; pages renamed to match; the
  `/api/platform-admin` shapes follow the rename.
- **Automation tester**: run both a "Clinic" preset workspace and a campaigns-only workspace per run to catch leftovers.

## Order of work

| # | Step | Rough size |
|---|---|---|
| 0 | Decide the names below | — |
| 1 | Mechanical rename in code, DB, routes, portal, tests, design doc (own branch, no behavior change) | 1.5–2 weeks |
| 2 | Contacts: tags, custom fields, lists/segments, CSV import; campaigns target contacts | 1–1.5 weeks |
| 3 | Modules: per-workspace switches on top of entitlements; menu, dashboard, APIs and AI tools respect them | ~1 week |
| 4 | Pipeline: stages table, contact stage, migrate the old status values into the clinic preset | ~1 week |
| 5 | Presets + terminology overrides; signup picks a preset (Clinic, Campaigns only, General) | ~1 week |
| 6 | AI prompt templating per preset + module-aware tools | ~1 week + unknown n8n scope |
| 7 | Admin portal: presets/modules/terms editing, renamed pages | ~1 week |

**Total: roughly 7–9 weeks.** Steps 1 and 2 are the foundation; steps 3–7 can be split across sessions.

## Beyond generic: gaps to a full omnichannel platform

The steps above make the app generic, not a full omnichannel product. Checked against `Database/schema.sql` and
`Business/Providers` (2026-10-10):

| Gap | Today | Rough size |
|---|---|---|
| Channels | Sending works for WhatsApp (Meta + Infobip) and Telegram; TikTok is lead ingest. Instagram, Messenger, email, SMS and website appear only in check constraints | Instagram + Messenger DMs ~1.5–2 weeks (one Meta app); email ~1–1.5 weeks; SMS ~0.5–1 week (Infobip already there); web chat widget ~1.5–2 weeks |
| One contact across channels | Contacts are matched by phone; no merge or per-channel identities | ~1 week (contact_identities table + merge) |
| Team inbox | No conversation assignment, internal notes, saved replies, snooze, or unread per member | ~1.5–2 weeks |
| Roles and permissions | Deliberately none (schema comment: "MVP scope decision: ignore roles/permissions") | ~1 week (owner / admin / member, plus "only my conversations") |
| Many workspaces per user | `ux_clinic_users_user_active` allows one active workspace per user | ~0.5–1 week (workspace switcher; matters for agencies) |
| Automation | Logic is hard-wired, plus the n8n AI flow. No user-built rules (auto-assign, auto-tag, follow-ups, business hours, away messages) | ~2–3 weeks for a rules engine; a visual builder is much more |
| Public API and outgoing webhooks | `api_access` entitlement exists, but there are no API keys per workspace and no event webhooks | ~1–1.5 weeks |
| Reporting | Dashboard counts only: no response time, resolution or per-member stats | ~1–1.5 weeks |
| Integrations | Google Calendar only. No CRM, Shopify or Zapier/Make | ~1 week per integration; Zapier via webhooks is cheapest |

**Gap total: roughly 12–17 more weeks** on top of the 7–9 weeks above. For a first version that sells, I'd add
Instagram + Messenger, assignment + notes + saved replies, basic roles, and the API/webhooks (~5–6 weeks). The rest can
wait for customer demand.

## Decisions needed

1. Tenant name: **workspace** (recommended) or business/organization?
2. Team name: **member** internally, with "Team" in the UI (recommended), or "agent"?
3. Is a campaigns-only plan something you want to sell? If yes, step 2 comes before steps 3–7.
4. Keep the SculptFlow brand and project name, or rename the product as part of step 1?
