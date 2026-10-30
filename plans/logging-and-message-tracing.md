# Plan: error log + message tracing

Status (2026-10-15): both parts built, not committed. Part 1 = error log (`activity.app_logs`, portal Errors page);
part 2 = message trace (`activity.message_trace_steps`, `crm.messages.trace_id`, portal Message traces pages). Left for
Mohammad: the n8n header change (see `docs/system-design.html` §7). Applies to `SculptFlowApp` (main app) and `SculptFlowAdmin` (portal).

## The short answer

Build both parts, in one design, as two implementation threads:

1. **Error log**: every `LogWarning` / `LogError` / `LogCritical` (and every unhandled exception, which ASP.NET Core already logs
   as an error) is also saved to a new table `activity.app_logs`, with settings for minimum level and retention. No call site
   changes: it is a logger provider plugged into the normal `ILogger`.
2. **Message trace**: every hop a message takes (webhook in, saved, inbox notified, bot triggered or skipped, bot tool calls,
   reply sent, billing, delivery statuses) writes one small row to `activity.message_trace_steps`, all sharing one
   **trace id**. The portal shows one timeline per message: "what happened, in order, and where it stopped".

The **trace id ties them together**: every error log row carries the trace id of the request it happened in, so the trace
timeline shows the errors inline, and an error links to its whole message story.

Both reuse the pattern already proven by the API timing work: record in memory (microseconds, never blocks or fails the
request), a background job writes batches and purges past retention.

---

## What exists today (and why it isn't enough)

| Thing | What it does | Gap |
|---|---|---|
| `ILogger` (console only, `appsettings.json` `Logging`) | Warnings/errors go to stdout | Lost on restart; no search; no link to clinic/message |
| `activity.events` (`IEventLogger`) | Business facts per clinic (`CustomerMessageReceived`, `AiMessageSent`, `HumanHandoff`...), written in the caller's unit of work | Only written when the save succeeds, so failures never appear; clinic required; no technical steps (n8n call, provider call, signature reject) |
| `activity.api_route_stats` + `RequestTimingMiddleware` + `ApiTimingCollector` + `ApiTimingFlushJob` | Per-route timings, per-minute rows, `Monitoring:RetentionDays` purge | Aggregates only; the right *pattern* to copy |
| `crm.messages.delivery_status` / failure fields | Last status of an outbound message | Only the latest state, not the path |
| ASP.NET Core `Activity` (W3C trace id per request) | Exists already (`Error.cshtml.cs` shows it) | Never stored; n8n hop breaks the chain |

Keep `activity.events` as it is (business audit, kept long). The two new tables are technical and short-lived.

### Overlap check (2026-10-15): what we reuse instead of re-recording

Checked every log/event/status-like table in `Database/schema.sql` and the portal's `admin-schema.sql`. None of them stores
warnings/errors or a per-hop message path, so neither new table repeats existing work. But several already hold part of the
story, and the trace view **reads them instead of writing duplicate steps**:

| Existing data | Already holds | Use in the trace |
|---|---|---|
| `crm.messages` `sent_at`, `delivered_at`, `read_at`, `failed_at`, `failure_code`, `failure_reason`, `delivery_status` | Meta/Telegram delivery lifecycle per message | Timeline shows sent/delivered/read/failed from these columns; **no `status.*` step rows** (saves ~3 rows per outbound message) |
| `billing.usage_records` (has `message_id`) + `billing.ledger_entries` | What each message was charged / released | Timeline shows the charge from here; **no `billing.*` step rows** |
| `activity.events` | `CustomerMessageReceived`, `AiMessageSent`, `StaffMessageSent*`, `HumanHandoff` (with reason), campaign events | Merged into the timeline by `conversation_id` + time; steps only add what events can't (failures, skips, timings, n8n hop) |
| `marketing.campaign_recipients` `status`, `failure_reason`, `skip_reason` | Campaign send outcome | Shown for campaign-origin messages |
| `channels.whatsapp_health_events`, `channel_integrations.last_error` | Connection health | Linked from the trace when the send failed for a connection reason |
| `activity.notifications` (`OUTBOUND_MESSAGE_FAILED`, ...) | What staff were told | Not needed in the trace |
| `activity.api_route_stats` | Per-route timing aggregates | Separate (performance), unchanged |
| `admin.admin_audit_log` (portal) | Who changed what in the portal | Unrelated |
| `last_error` / `error_message` on scrape runs, benchmark runs, calendar syncs | Latest failure of those jobs | Unchanged; their exceptions also reach the new error log automatically |

So the trace table only stores the steps nothing else records: webhook received/rejected, clinic unresolved, deduplicated,
inbox notified, bot skipped (reason) / triggered / failed / tool calls, send requested / rejected / provider accepted or
failed, with timings.

---

## Part 1: error log

### How it captures

- `Business/Providers/Logging/DatabaseLoggerProvider` (+ `DatabaseLogger`), registered in `Program.cs` with
  `builder.Logging.AddProvider(...)`. It sees every log call from every class, including framework ones.
- `Log(...)` does the minimum on the calling thread: level check, build a small `AppLogEntry` (rendered message, template,
  exception type/message/stack, category, scope values), and `TryWrite` into a bounded in-memory channel owned by a singleton
  `Business/Managers/AppLogCollector`. If the buffer is full the entry is dropped and a drop counter goes up (the next flushed
  batch records "N entries dropped"). Logging can never slow down or break a webhook.
- `Business/Jobs/AppLogFlushJob` (like `ApiTimingFlushJob`): every few seconds writes the buffered rows in one batch through
  `IAppLogService` → `IAppLogRepository`; every hour deletes rows past retention (in chunks). Flushes once more on shutdown.
- Context comes from **logging scopes** plus `Activity.Current`: trace id, clinic id, conversation id, message id, route and
  method. A small middleware (or the existing `RequestTimingMiddleware`) opens a scope with route/method; the message flow
  opens scopes with clinic/conversation/message ids (Part 2 does this anyway).

### Guards (the parts that usually go wrong)

- **No recursion**: ignore categories `PlasticSurgery.Business.Jobs.AppLogFlushJob`, the log repository, and
  `Microsoft.EntityFrameworkCore.*` (a failing log insert must not log an EF error that tries to insert again). Plus an
  `AsyncLocal` "inside log flush" flag.
- **Floods**: a fingerprint (hash of category + message template + exception type + top stack frame) per entry. At most
  `ErrorLog:MaxPerFingerprintPerMinute` rows per fingerprint per minute; the rest become a `repeat_count` on the last row. A
  Meta outage then gives a few rows saying "x 4,000", not 4,000 rows.
- **Secrets / PII**: store the template and the rendered message capped at 2,000 chars; stack capped at 8,000; scope and
  structured properties pass through a redactor (keys containing `token`, `secret`, `key`, `password`, `authorization`,
  `signature` become `***`). Existing call sites already avoid logging message content; keep that rule in `CLAUDE.md`.
- **Start-up**: the provider is created before DI is built, so it resolves `IConfigManager` lazily; entries logged before
  the job starts just wait in the buffer.

### Table (`activity.app_logs`)

```sql
create table if not exists activity.app_logs (
  id                 bigint generated always as identity primary key,
  occurred_at        timestamptz not null,
  level              varchar(12) not null,          -- Warning / Error / Critical
  category           varchar(300) not null,         -- logger name, e.g. PlasticSurgery.Business.HttpClients.N8n.AiTriggerNotifier
  message            text not null,                 -- rendered, capped
  message_template   varchar(1000),                 -- groups the same problem across values
  exception_type     varchar(300),
  exception_message  text,
  stack_trace        text,
  fingerprint        char(16) not null,
  repeat_count       int not null default 1,
  trace_id           varchar(32),                   -- W3C trace id, joins activity.message_trace_steps
  clinic_id          uuid,                          -- no FK: logs must survive a deleted clinic and inserts stay cheap
  conversation_id    uuid,
  message_id         uuid,
  route              varchar(300),
  method             varchar(10),
  instance           varchar(100),                  -- machine/container name when several app instances run
  properties         jsonb not null default '{}'::jsonb
);
create index if not exists ix_app_logs_occurred_at on activity.app_logs(occurred_at);
create index if not exists ix_app_logs_fingerprint on activity.app_logs(fingerprint, occurred_at desc);
create index if not exists ix_app_logs_trace_id on activity.app_logs(trace_id) where trace_id is not null;
create index if not exists ix_app_logs_clinic on activity.app_logs(clinic_id, occurred_at desc) where clinic_id is not null;
```

### Settings (per the configuration rule: `ConfigDefaults` + `IConfigManager` + `docs/configuration.md` + `seed-config.sql`)

| Setting | Default | Meaning |
|---|---|---|
| `ErrorLog:Enabled` | `true` | Switch the DB sink off without a deploy |
| `ErrorLog:MinimumLevel` | `Warning` | `Warning`, `Error` or `Critical` |
| `ErrorLog:WarningRetentionDays` | `14` | Warnings are noisier, keep them shorter |
| `ErrorLog:ErrorRetentionDays` | `90` | Errors and criticals |
| `ErrorLog:MaxPerFingerprintPerMinute` | `20` | Flood cap (rest counted in `repeat_count`) |
| `ErrorLog:ExcludedCategories` | `""` | Comma list of category prefixes to ignore (e.g. a noisy framework warning) |

Buffer size, batch size and flush interval stay internal constants in `Common/Statics/AppLogLimits`, the same way
`ApiTimingLimits` holds the timing job's.

---

## Part 2: message tracing

### The trace id, and how it survives each hop

Use the W3C trace id ASP.NET Core already creates per request (`Activity.Current.TraceId`, 32 hex chars). It is the
OpenTelemetry standard, so a later move to an external tracing tool needs no redesign.

| Hop | How the trace id carries over |
|---|---|
| Meta / Telegram webhook in | Starts a new trace (the webhook request's own trace id) |
| Webhook → `IngestAsync` → SignalR | Same request, same trace |
| App → n8n (`AiTriggerNotifier`) | `HttpClient` already sends a `traceparent` header automatically; also add `TraceId` to `AiTriggerPayload` |
| n8n → app (AI tool endpoints in `AiController`, `POST .../messages/send` with `sender=ai`, handoff) | **n8n change (outside the repo):** each HTTP node sends `traceparent: {{$json.traceparent}}` back, plus `X-N8n-Execution-Id: {{$execution.id}}`. ASP.NET Core adopts an incoming `traceparent` automatically, so the callback request has the same trace id with no app code |
| Staff reply from the Inbox | New trace from the dashboard request |
| Template / campaign send (`CampaignSenderWorker`) | Worker starts an `Activity` per recipient |
| Meta delivery statuses (sent/delivered/read/failed), hours later | Meta can't carry it. New column **`crm.messages.trace_id`**, set when the message row is created; the status handler finds the message by `external_message_id` (it already does) and records its steps under that message's trace id |
| Customer's inbound message → AI reply message | The reply row gets the inbound message's trace id (same trace via n8n), so "customer asked → bot answered" is one story |

If n8n doesn't send the header (old workflow), the app still works: the callback just gets its own trace, and the timeline
falls back to joining by `conversation_id` + time, so the view degrades instead of breaking.

### What gets recorded (step vocabulary in `Common/Statics/TraceSteps`)

| Area | Steps |
|---|---|
| Webhook | `webhook.received` (channel, event kinds, batch size), `webhook.rejected` (bad signature / bad JSON), `webhook.clinic_unresolved` |
| Ingest / inbox | `message.saved`, `message.deduplicated` (Meta retry), `conversation.reopened`, `conversation.mode_changed` (reason), `inbox.notified` (SignalR) |
| Bot | `bot.skipped` (reason: `human_mode`, `plan_without_ai_agent`, `reopened_to_staff`, `n8n_url_missing`, `bot_off`), `bot.triggered` (n8n status code, ms), `bot.trigger_failed`, `bot.tool_called` (endpoint, ms, outcome: knowledge search, availability, booking...), `bot.handoff` |
| Send | `send.requested` (staff / ai / template / campaign), `send.rejected_mode` (409 human mode), `send.window_closed`, `send.provider_accepted` (external id, ms), `send.provider_failed` (provider error code) |
| Billing, delivery | not stored as steps: read from `billing.usage_records` and `crm.messages` status columns (see overlap check) |
| Errors | not a step: error log rows with the same trace id are merged into the timeline |

Each step is one call: `_trace.Step(TraceSteps.BotTriggered, outcome: TraceOutcome.Ok, durationMs, detail: new { status })`.
The tracer reads trace id and clinic/conversation/message ids from the current scope, so call sites stay one line. Steps
never carry message text, phone numbers or tokens (ids, codes, counts and timings only).

### How it writes

Same as the error log and API timings: `Business/Managers/MessageTracer` (singleton, `IMessageTracer`) → bounded channel →
`Business/Jobs/MessageTraceFlushJob` batch insert + purge. Deliberately **not** through the request's unit of work: a step
must be stored even when the request's save rolls back, because the failed ones are the ones worth seeing.

### Table (`activity.message_trace_steps`) + one column

```sql
create table if not exists activity.message_trace_steps (
  id                   bigint generated always as identity primary key,
  trace_id             varchar(32) not null,
  occurred_at          timestamptz not null,
  step                 varchar(60) not null,
  outcome              varchar(12) not null,       -- ok / skipped / failed
  duration_ms          double precision,
  clinic_id            uuid,
  conversation_id      uuid,
  message_id           uuid,
  external_message_id  varchar(200),
  channel              varchar(50),
  n8n_execution_id     varchar(100),
  detail               jsonb not null default '{}'::jsonb,
  instance             varchar(100)
);
create index if not exists ix_trace_steps_trace on activity.message_trace_steps(trace_id, occurred_at);
create index if not exists ix_trace_steps_message on activity.message_trace_steps(message_id) where message_id is not null;
create index if not exists ix_trace_steps_conversation on activity.message_trace_steps(conversation_id, occurred_at desc);
create index if not exists ix_trace_steps_failed on activity.message_trace_steps(occurred_at desc) where outcome = 'failed';
create index if not exists ix_trace_steps_occurred_at on activity.message_trace_steps(occurred_at);

alter table crm.messages add column if not exists trace_id varchar(32);
```

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `MessageTrace:Enabled` | `true` | Switch off without a deploy |
| `MessageTrace:RetentionDays` | `14` | Normal traces |
| `MessageTrace:FailedRetentionDays` | `60` | Traces with any failed step are kept longer (purge skips trace ids that have a failed step until this age) |

---

## How you'd view it (portal)

Main app first (`/api/platform-admin/{domain}`, read-only, `[RequirePlatformAdminKey]`, repositories in
`Persistence/Repositories/PlatformAdmin`), then the portal's client, service, page and copied shapes, and a
`MAIN_APP_SYNC.md` entry.

| Endpoint | Returns |
|---|---|
| `GET /api/platform-admin/logs/groups?since&level&clinicId&search` | One row per fingerprint: level, template, count, first/last seen, clinics affected |
| `GET /api/platform-admin/logs?fingerprint&since&clinicId&traceId` | Occurrences (paged) |
| `GET /api/platform-admin/logs/{id}` | Full row incl. stack |
| `GET /api/platform-admin/message-traces/{traceId}` | Timeline: steps + error log rows + the message rows, ordered |
| `GET /api/platform-admin/message-traces/by-message/{messageId}` (also by `externalMessageId`) | Same, resolved via `crm.messages.trace_id` |
| `GET /api/platform-admin/message-traces?clinicId&conversationId&failedOnly&since` | Recent traces with a one-line summary ("WhatsApp in → bot skipped: human_mode", "AI reply → failed at Meta 131047") |

Portal pages:

- **Errors** (new): grouped list with counts and a 24h sparkline, filter by level/clinic; open a group to see occurrences
  and the stack; "View message trace" button when the row has a trace id.
- **Message trace** (new): vertical timeline, one line per step with time, duration, outcome colour, and errors inline;
  link to the n8n execution when `n8n_execution_id` is present. Entry points: a "Trace" link on each message in the existing
  **Conversations → Details** and **Messages** pages, a search box (message id / wamid / trace id), and a **Failed flows**
  tab (traces with a failed step, last 24h).
- **Overview**: two tiles, "errors last 24h" and "failed message flows last 24h".

Typical use: a clinic says "the bot didn't answer Ahmad". Open the conversation in the portal, click Trace on his message,
see `webhook.received → message.saved → bot.skipped (plan_without_ai_agent)`. Done in one screen.

---

## Cost and performance

- **Request latency**: one level check plus a non-blocking channel write per log/step (well under a microsecond to a few
  microseconds). No extra DB round trip on the message path. The flush jobs write one multi-row insert every few seconds.
- **DB size** (the real cost). Roughly 250–350 bytes per row including indexes.
  - Trace steps: ~6–9 per inbound message with a bot reply, ~4 per staff reply (statuses and billing are read from
    existing tables, not stored). At 2,000 messages/day ≈ 15k rows/day ≈ 5 MB/day ≈ **65 MB at 14 days**. At 10k
    messages/day ≈ 300 MB, so lower `MessageTrace:RetentionDays` then.
  - The timeline's delivery and billing lines live as long as `crm.messages` / `billing.usage_records` do, so they stay
    visible even after the trace steps are purged.
  - Error log: small when the app is healthy (tens to hundreds of rows/day); the fingerprint cap stops outages filling it.
  - Check the Supabase plan's disk limit before go-live; retention is a setting, so it can be tuned from the portal.
- **Purge**: hourly chunked deletes on an `occurred_at` index; cheap. (If volume ever gets large, switch the trace table to
  daily partitions and drop old partitions; not needed now.)
- **Money**: no new services or licences.

### Alternatives considered

| Option | Verdict |
|---|---|
| OpenTelemetry + Grafana Tempo/Loki, Jaeger, Seq, App Insights | The industry standard and great at scale, but another service to host/pay for, and you'd leave the portal to use it. Because this plan uses W3C `Activity` trace ids, adding an OTel exporter later is a few lines in `Program.cs` |
| Sentry (free tier) for errors only | Good error grouping and alerts with little work, but no message-flow view, and the data leaves your DB. A reasonable add-on later for alerts |
| Serilog with a Postgres sink | Gets you Part 1 faster, but adds a dependency and you still need the flood cap, redaction, scopes and Part 2 yourself |
| Reuse `activity.events` for steps | No: it is a business audit table tied to the unit of work (failures never land) and clinic-required |

---

## Recommendation and effort

Do it in two threads, Part 1 first (smaller, and the trace view shows its errors).

| Phase | Work | Rough effort |
|---|---|---|
| 1. Error log | Provider + collector + flush/purge job, table, 6 settings, redaction/fingerprint/flood cap, platform-admin `logs` endpoints, portal **Errors** page + Overview tile, unit tests (collector, fingerprint, redactor, flood cap) and a throwaway-Postgres repository/purge test, `system-design.html` + `configuration.md` updates | ~1.5–2 days |
| 2. Message trace | Tracer + flush job, table + `crm.messages.trace_id`, scopes in webhook/ingest/send/campaign paths, `TraceId` on `AiTriggerPayload`, step calls across ~12 places, platform-admin `message-traces` endpoints, portal **Message trace** page + links from Conversations/Messages, tests (step sequence for inbound→bot→reply, staff send failure, status join) | ~3–4 days |
| 2b. n8n (you, outside the repo) | Add `traceparent` and `X-N8n-Execution-Id` headers to the HTTP nodes that call back into the app | ~30–60 min |
| 3. Later, optional | Alerts (new fingerprint or error spike → email/Telegram to you), OTel exporter, a clinic-facing "why didn't the bot reply" hint | separate |

Model: this plan is the expensive design part; both phases are routine implementation for **Sonnet** at medium effort. Use
Opus only if Phase 2's n8n hop or the purge rule for failed traces turns tricky.

## Decisions

1. Retention defaults agreed (2026-10-15): warnings 14 days, errors 90, traces 14, failed traces 60.
2. Delivery statuses in the trace: agreed yes (2026-10-15); shown from `crm.messages` columns, not new rows.
3. Start only when Mohammad says so.
4. Coordination: the AI bot on/off work in progress touches `MessageService.IngestAsync` and `ConversationService`; Phase 2
   adds step calls there, so start Phase 2 after that lands (Phase 1 doesn't touch those files and can start now).
