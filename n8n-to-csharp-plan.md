# Replace n8n with C# — plan

Date: 2026-10-26. Branch: `refactor/generic-platform` in SculptFlowApp (the branch with the open PR to main). Commit after each session's checks pass, do NOT push. Mohammad merges and tests everything once.

## Goal

The three n8n workflows become code in the main app, with **the same flow, prompts, tools and result handling** as the current Omni workflows (`docs/n8n/omni-*.json`, commit ef11dce). The shape is the same as the old tested SculptFlow workflows; the Omni changes are renames, the business timezone, bots and allowed actions. After this, n8n is not needed at all.

Reference flow = `docs/n8n/omni-ai-assistant.json`, `omni-calendar-sync.json`, `omni-retrieval-benchmark.json` at ef11dce. Prompt and tool-description text is **extracted from those JSON files by a script into resource files, never retyped**.

## Parity contract (what "same flow" means)

AI assistant, per contact message (or test-panel message):
1. Trigger data is exactly today's `AiTriggerPayload` after AiTriggerNotifier fills it (plan check, inbox-settings bot resolve, profile, contact language, terms, bot config). Same skip reasons and trace steps.
2. **Agent** = model `gpt-6-luna` (setting), system prompt = the AI Agent node's `systemMessage` rendered with the same values (`clientName`, `bot.instructions`, `aiProfile`, `terms` JSON, `language`, `contactLanguage || 'not known yet'`, `timezone || 'UTC'`, current business-local time as `cccc, yyyy-MM-dd HH:mm`), user input = `messageText`. Max 10 tool iterations (n8n default).
3. **Memory** = n8n Simple Memory: key conversationId (test: session id), last 10 exchanges, each exchange = the contact's `messageText` + the agent's final raw output. Stored in the DB instead of n8n RAM (survives restarts).
4. **Tools** (names, descriptions, AI-filled parameters and fixed parameters exactly as the n8n nodes): search_knowledge (query, limit 5), get_available_slots (date; clientId + contactId fixed), get_my_bookings, get_services, update_contact (language), update_contact_details (city, timeline, notes, qualification; default ''), update_stage (stage). Each call goes through the same logic the `/api/ai/...` endpoint runs today, including `[BotTool]` authorization (TOOL_NOT_ALLOWED), `[RequireFeature(Bookings, AiTool)]` (FEATURE_NOT_INCLUDED), test-session simulation, and a `bot.tool_called` trace step. The tool result the model sees is the same JSON the endpoint returns.
5. **Decision** = the agent's final JSON (action, message, scheduledStart, confirmReplaceExisting, bookingId, serviceId, reason) → `action_needed`:
   - reply → send `message` via the reply logic (`BotConversationService.ReplyAsync`; 409 cases stop silently, traced).
   - schedule → schedule logic with `{scheduledStart, serviceId||null, notes:"", reason||null, confirmReplaceExisting, bookingId||null}` → **formatter** → reply.
   - cancel → cancel logic with `bookingId||null`, `{reason||null}` → **formatter** → reply.
   - handoff → handoff logic with `reason || "The contact needs a team member."`; nothing else sent by the bot.
   - any other action / unparseable output → traced as failed, nothing sent (same as n8n).
6. **Formatter** = model `gpt-4o-mini` (setting), system = the result_formatter message rendered the same way, user = `VERIFIED BACKEND RESULT:\n{result JSON}\n\nLATEST CONTACT MESSAGE:\n{messageText}`. Every schedule/cancel result goes to it (the n8n switches + fallback outputs all lead there).
7. Billing unchanged: ai_reply is charged where it is today (reply send / test reply).

Calendar sync: same payload in (`CalendarSyncTriggerPayload`), same Google/Graph calls (create POST, update PATCH, cancel DELETE; title, description + dashboard link joined by blank line, start/end with the business timezone), then the same `ApplySyncCallbackAsync` with `externalEventId = created id || payload id`.

Benchmark: same system prompt (extracted), model `gpt-4o` (setting), input = the payload JSON, output parsed by the existing `GeneratorResponseParser` and stored through the existing `ReceiveGenerationResultAsync`.

## Decisions (defaults, chosen to keep the flow and remove the weak spots)

- **OpenAI Responses API over HttpClient**, like `OpenAiEmbeddingService` (no new SDK). One `IAiModelClient` interface so tests use a fake model.
- **Key and models as config settings**: `Ai / ApiKey` (secret setting, same pattern as `Embeddings / ApiKey`; empty = use the embeddings key), `Ai / AgentModel` = gpt-6-luna, `Ai / FormatterModel` = gpt-4o-mini, `Ai / MaxToolIterations` = 10, `Ai / MemoryExchanges` = 10, `Ai / TimeoutSeconds` = 60, `Ai / MaxConcurrentRuns` = 4, `Benchmark / GeneratorModel` = gpt-4o. Each with ConfigDefaults + property + docs/configuration.md + seed row.
- **Agent output as a strict JSON schema** with the 7 fields, so it can't come back as plain text. Prompt text unchanged.
- **Only the bot's allowed tools are offered** (by the capabilities in AiBotConfig); the server-side check still runs on every call.
- **Background run**: webhooks enqueue the run and return at once (today they wait for n8n's HTTP answer). One run at a time per conversation (n8n ran two quick messages in parallel); runs for different conversations in parallel up to MaxConcurrentRuns.
- **Calendar sync failures are reported** (`success=false`, error message) instead of disappearing like in n8n.
- **Removed**: n8n clients, `N8n:AiWebhookUrl`, `N8n:CalendarSyncWebhookUrl`, `N8n:KnowledgeBenchmarkWebhookUrl`, the `/api/ai/*` tool routes, `/api/ai/test/*`, the calendar sync-callback and benchmark questions callback routes, `docs/n8n/omni-*.json`, N8nContractTests (replaced by engine tests). The ingest key stays (POST /api/leads, messages ingest).
- Automation tester: still skipped.

## Sessions (Remote Control on Mohammad's PC, one after another)

Each session: load the `omni-session-rules` skill, read this plan, work on `refactor/generic-platform`, build + full tests green, scan staged changes for secrets, commit (no push), update system-design.html where behaviour changed. Ignore any old questions about the Integrations channel count or pricing/plans.

**N1 — Foundations (no behaviour change).**
- `IAiModelClient` + `OpenAiResponsesClient` (tools, function-call loop items, json_schema output, usage, timeout) with HttpClient tests.
- Config settings above.
- Move every `/api/ai/*` action body into `IBotToolService` (Business/Services/Ai) returning the same objects, with the BotTool/RequireFeature/test-session rules and the trace step inside. AiController becomes a thin wrapper over it so all current tests stay green.
- Script `tools/extract-n8n-prompts` (or a one-off test helper) that writes the agent prompt, formatter prompt, benchmark prompt and every tool description from the JSON into `Resources/ai/*.txt`; templates use named placeholders. A test renders them and compares to the n8n expressions for a sample payload.

**N2 — The bot engine.**
- `crm.bot_memory` table (conversation_id/test session id, client_id, user_text, output, created_at) + repository; keep last MemoryExchanges; deleted with the test session.
- `BotEngine` running steps 2–6 of the parity contract, tracing each step (run started, model call with tokens and ms, tool calls, decision, action result, formatter, reply).
- `BotRunQueue` + `BotRunWorker` (per-conversation ordering). `AiTriggerNotifier` becomes `BotTrigger` (same checks) and enqueues instead of posting. WhatsApp, Telegram and the bot test panel use it.
- Tests with the fake model for every path: reply, schedule with each code (BOOKED, RESCHEDULED, CONFIRMATION_REQUIRED, MULTIPLE_UPCOMING_BOOKINGS, SLOT_UNAVAILABLE, CONTACT_NOT_FOUND, INVALID_REQUEST, TOOL_NOT_ALLOWED, FEATURE_NOT_INCLUDED), cancel with each code, handoff with and without reason, unknown action, broken output, 409 on reply, memory window, test-session simulation, tool refused for a capability, per-conversation ordering. The canned results from AiTestController become test fixtures.

**N3 — Calendar sync and benchmark generator.**
- `CalendarSyncClient` (Google + Graph) replacing CalendarSyncNotifier; calls `ApplySyncCallbackAsync` directly. Tests with a fake handler for all 6 provider/operation pairs and a failure.
- `KnowledgeBenchmarkGenerator` (OpenAI) replacing the n8n client; runs in the existing benchmark queue; same parser and storage. Tests.

**N4 — Remove n8n and clean up.**
- Delete everything in "Removed" above, Program.cs registrations, appsettings N8n URL comments, n8n wording in UI text and code comments.
- Docs: system-design.html (AI section, diagrams), configuration.md, deploy-runbook.md (drop n8n import/credential steps, add `Ai / ApiKey` and model settings), billing.md if it mentions n8n.
- Admin portal: run the `admin-portal-sync` skill (settings page, trace step names, benchmark wording).
- Final parity review: walk the parity contract line by line against the code and the JSON, and list anything that differs.

## Mohammad's steps after merge (go into deploy-runbook)

Set `Ai / ApiKey` in the admin portal Configuration page (or leave empty to use the embeddings key); check the model names; run the new schema.sql part (bot_memory); remove the N8n__*WebhookUrl env vars on Render; turn off the n8n workflows; then test the bot on WhatsApp/Telegram and the test panel, calendar sync, and benchmark generation.
