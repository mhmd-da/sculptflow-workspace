# SculptFlow automation tester

A separate app that checks the whole SculptFlow system through its real HTTP surface: sign-up and sign-in, every
dashboard page, the clinic APIs, the n8n / AI-agent endpoints, webhooks, billing and the admin portal's API. You pick
which suites or single tests to run, watch them live, and get a report.

It never touches real clinics. Each run registers a throwaway clinic through the normal sign-up page
(`[Automation] main <run id>`, user `auto-…@sculptflow-automation.test`), does everything as that clinic, and deletes it
at the end through the main app's `DELETE /api/platform-admin/automation/clinics/{id}`. That endpoint refuses any clinic
whose name doesn't start with `[Automation]` or whose users aren't all on the reserved `.test` domain.

## Run it

```
cd SculptFlowAutomation
dotnet run
```

Open http://localhost:5290, pick an environment and tests, press **Run selected**. Reports are saved under
`SculptFlowAutomation/runs/` (JSON + a standalone HTML page) and listed under **Recent runs**.

Command line (exit code 1 when a test fails):

```
dotnet run -- list
dotnet run -- run --env local --tests all
dotnet run -- run --env production --tests health,auth,leads,ai.booking-flow --allow billing
dotnet run -- cleanup --env production
```

## Configure targets

`appsettings.json` holds only base URLs (`local` = http://localhost:5274, `production` = https://sculptflowapp.onrender.com).
Keys are secrets and go in user-secrets (or env vars like `Automation__Environments__production__IngestKey`):

```
dotnet user-secrets set "Automation:Environments:production:IngestKey" "<main app N8n:IngestApiKey>"
dotnet user-secrets set "Automation:Environments:production:PlatformAdminKey" "<main app PlatformAdmin:ApiKey>"
dotnet user-secrets set "Automation:Environments:production:MetaWebhookVerifyToken" "<main app Meta:WebhookVerifyToken>"
dotnet user-secrets set "Automation:Environments:production:MetaAppSecret" "<main app Meta:AppSecret>"
```

- Without the **Meta app secret**, the signed WhatsApp delivery test is skipped (the "unsigned is refused" test still runs).

- Without the **ingest key**, AI-agent and n8n tests are skipped.
- Without the **platform-admin key**, admin tests are skipped and the test clinic can't be deleted (the report says so).
  Use **Delete leftover test clinics** (or `cleanup`) once the key is set.
- The target must have the `AutomationAdminController` deployed for cleanup to work.

## Safety switches

Tests that could cost money or reach outside the system are skipped unless you tick them for that run:

| Switch | What it allows |
| --- | --- |
| External services | Knowledge entries and search (OpenAI embeddings), benchmark generation and runs, scraping example.com/.org, fake Telegram/Meta credentials sent to the providers |
| Billing writes | Top-ups, adjustments, a plan subscription and a clinic-only rate card, all on the test clinic (no real money moves) |
| Shared-state actions | Clearing the whole cache (harmless to data, but every clinic's cache is rebuilt) |
| Messaging | Anything that could reach a real phone. No test uses it yet. |

Conversations use the `website` channel, so nothing is ever delivered to WhatsApp or Telegram. The webhook tests only
send unknown or invalid deliveries. Global settings and plans are never changed: those tests only check validation.

## Suites

`dotnet run -- list` prints every test. In short: health, auth, security, pages, the clinic APIs (staff, procedures,
leads, opening hours, appointments, bookings, inbox, notifications, dashboard, knowledge, websites, campaigns, WhatsApp,
channels, calendar, billing), the n8n AI tools, the knowledge benchmark, n8n/provider callbacks, OAuth flows, clinic
API edge cases, every admin-portal API (billing, clinics, staff, leads, content, channels, overview, settings, cache),
and the end-to-end patient journey. `COVERAGE.md` maps what each area checks and the bugs the tests found.

## Adding a test

Suites live in `SculptFlowAutomation/Suites`. Add a `.Test("id", "Name", async ctx => { ... })` to a suite, or a new
suite class registered in `Core/TestCatalog.cs`. Use `ctx.ApiAsync()` (signed-in clinic), `ctx.Anonymous`, `ctx.Ingest`,
`ctx.Admin`, and `ctx.Fixtures` for shared data (lead, opening hours, slots, conversation). Mark anything that calls a
paid or outside service with `risk: Risk.External`.
