# Omni automation tester (Omni.Automation)

A separate app that checks the whole Omni system through its real surface: sign-up and sign-in, every client API and
page, the inbox and channels (Telegram, WhatsApp through signed Meta deliveries), AI bots and the AI tool endpoints,
billing, bookings and campaigns, every platform-admin API, and the pages in English, Arabic and French on desktop and
mobile. You pick suites, single tests or QA findings, watch them run, and get a report.

It never touches real clients. Each run registers throwaway clients through the normal sign-up page
(`[Automation] <label> <run tag>`, users `auto-…@omni-automation.test`), does everything as them, and deletes them at the
end through the main app's `DELETE /api/platform-admin/automation/clients/{id}`. That endpoint refuses any client whose
name doesn't start with `[Automation]` or whose users aren't all on that reserved `.test` domain.

## QA findings as tests

The full QA pass of 30 Oct 2026 (`docs/qa-report-2026-10-30.html` in the workspace, kept out of git) is built in:

- A test **without** a finding id checks behaviour QA confirmed works — the regression net.
- A test **with** a finding id (e.g. `QA t4-ai-01`) checks the **correct** behaviour for a bug QA found. It fails today
  and turns green once the bug is fixed. Findings the document review called by-design are asserted as documented.
- Pick them with the **QA findings** button, `--tests findings`, or one finding id: `--tests t5-ops-01`.
  The report lists how many findings still reproduce and how many now pass.

`COVERAGE.md` lists every suite, what it checks, and which QA checks could not be automated (and why).

## Run it

```
cd SculptFlowAutomation/Omni.Automation
dotnet run
```

Open http://localhost:5290, pick an environment and tests, tick what's allowed, press **Run selected**. Reports are saved
under `Omni.Automation/runs/` (JSON + a standalone HTML page, screenshots in `runs/files/`) and listed under
**Recent runs**. `runs/` is git-ignored: reports can show test-client logins and data.

Command line (exit code 1 when a test fails):

```
dotnet run -- list                      # every suite and test
dotnet run -- list --findings           # only the tests that reproduce QA findings
dotnet run -- run --env staging --tests health,signup,signin     # suite ids and/or test ids
dotnet run -- run --env staging --tests findings --allow billing,ai,external,shared
dotnet run -- run --env staging --tests t5-ops-01,t4-ai-02
dotnet run -- cleanup --env staging     # delete test clients left by a crashed run
```

## Configure targets

`appsettings.json` holds only base URLs (`local` = http://localhost:5274, `staging` = https://sculptflowapp.onrender.com).
Secrets go in user-secrets (or env vars like `Automation__Environments__staging__IngestKey`):

```
dotnet user-secrets set "Automation:Environments:staging:IngestKey" "<main app N8n:IngestApiKey>"
dotnet user-secrets set "Automation:Environments:staging:PlatformAdminKey" "<main app PlatformAdmin:ApiKey>"
dotnet user-secrets set "Automation:Environments:staging:MetaWebhookVerifyToken" "<main app Meta:WebhookVerifyToken>"
dotnet user-secrets set "Automation:Environments:staging:MetaAppSecret" "<main app Meta:AppSecret>"
dotnet user-secrets set "Automation:Environments:staging:Database" "Host=…;Port=5432;Database=postgres;Username=…;Password=…;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=3"
```

What each one unlocks (tests that need a missing one are skipped with the reason):

| Secret | Unlocks |
| --- | --- |
| IngestKey | AI tool endpoints, message ingest, contact intake, provider callbacks |
| PlatformAdminKey | every admin test, plan changes for test clients, **deleting the test clients after a run**, and getting a run's many sign-ups and sign-ins past the target's per-IP account limits (only those form posts carry it) |
| MetaAppSecret | signed WhatsApp webhook deliveries (new contacts, AI on WhatsApp, statuses, template/account events) |
| MetaWebhookVerifyToken | the WhatsApp webhook handshake check |
| Database | setup the API can't do (fake Telegram/WhatsApp connections, moving a 24-hour window or trial end into the past) and checks of rows the API doesn't show (usage records, traces). Writes are refused unless the client is an `[Automation]` one. `Gss Encryption Mode=Disable` is added automatically (Windows). |

Browser (UI suites): after the first build, install Chromium once:
`pwsh bin/Debug/net10.0/playwright.ps1 install chromium`. If the build fails with MSB3021 (path too long), the project
sits too deep in the file system for Playwright's files; build from a shorter path.

## Safety switches

Tests that cost money, reach outside the system or touch shared state are skipped unless ticked for that run:

| Switch | What it allows |
| --- | --- |
| External services | OpenAI embeddings (knowledge entries, search), scraping example.com, n8n, fake credentials sent to Telegram/Meta |
| AI | bot replies (test chat and live over a fake Telegram connection) and benchmark generation — a full run makes about 25 model calls |
| Billing writes | plan changes, top-ups, adjustments, rate cards — on test clients only, no real money moves |
| Shared-state actions | switching a global setting (AI engine, sign-up switch) for seconds and restoring it, clearing the cache, the 15-message burst test — felt by every client while it runs |
| Messaging | anything that could reach a real phone. **No test uses it**: channels are fake connections or the website channel, so nothing is ever delivered to a person. |

Never run two full runs against the same target at once: staging's database pool is small and the burst test is meant
to stress it.

## Adding a test

Suites live in `Omni.Automation/Suites`, one static class per suite marked `[Suite(order)]` with
`public static TestSuite Build()` — they are discovered automatically. Add `.Test("id", "Name", async ctx => { … })`.
Use `ctx.ApiAsync()` (main test client), `ctx.OtherClientAsync()` (isolation), `ctx.RegisterClientAsync(label, type)`
(a private client), `ctx.Anonymous`, `ctx.Ingest`, `ctx.Admin`, `ctx.Fixtures`, `ctx.Webhooks`, `ctx.Db`, `ctx.Browser`.
Declare `needs:` and `risk:` honestly; tag a QA bug with `finding: "t4-ai-01"`; slow tests can set `timeout:`.
