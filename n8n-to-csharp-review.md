# Replace n8n with C#: parity review

Date: 2026-10-26. Code: SculptFlowApp `refactor/generic-platform` (N1 `b82fbac`, N2 `429c2c8`, N3 `1a93b5f`, N4 commit).
Reference: `docs/n8n/omni-ai-assistant.json`, `omni-calendar-sync.json`, `omni-retrieval-benchmark.json` (as at `ef11dce`),
checked node by node against the old tested workflows in `MySculptFlow/docs/n8n/Sculptflow *.json`.

Each line of the plan's parity contract: **same**, or **differs** with the reason. No real mismatch was found that needed a
code fix in N4; the differences below are deliberate (decided in the plan or by Mohammad) or edge cases where both engines end
the same way.

## Old tested workflows vs the Omni workflows

- **AI assistant: same flow.** Nodes and edges match. The only differences are renames, three more agent tools
  (`get_services`, `update_contact_details`, `update_stage`) and more switch outputs, all leading to the formatter.
- **Calendar sync: same flow, three bugs fixed in Omni.** The old Outlook create/update sent `summary` instead of `subject`.
  The old timezone was hardcoded to `Asia/Beirut`. The old Outlook cancel URL began with `DELETE `, so it could never work.
  The code follows the Omni version, so only Google was really tested on the old one.
- **Benchmark: same flow.**

## The switch

- `Ai / BotEngine`, `Calendars / SyncEngine`, `Benchmark / GeneratorEngine`: `n8n` (default) or `code`, read per call,
  dropdowns on the portal. **On `n8n` every path is unchanged** (AiTriggerNotifier, CalendarSyncNotifier,
  N8nKnowledgeBenchmarkGeneratorClient, the `/api/ai` tools, the callbacks). N8nContractTests still pass.

## AI assistant (parity contract 1-7)

| # | Contract line | Result |
|---|---|---|
| 1 | Trigger data = `AiTriggerPayload` after the notifier fills it; same skip reasons and trace steps | **Same.** BotTrigger makes AiTriggerNotifier's checks in the same order (plan has `ai_assistant` → `bot.skipped plan_without_ai_assistant`; inbox-settings bot → `bot.skipped <block reason or no_bot>`; profile → `bot.skipped client_not_found`) and fills the same fields (profile, contact language only for a live contact, traceparent). Differs only where the transport differs: no `n8n_not_configured` skip on code, and `bot.trigger_failed queue_closed` while the app stops. `bot.triggered` carries `engine`. |
| 2 | Agent: model (setting), system prompt rendered with the same values, input = messageText, max 10 tool iterations | **Same.** Model `Ai / BotModel` = gpt-6-luna. The prompt is extracted from the JSON by script, and AiPromptTemplateTests renders it for two sample triggers against the n8n expressions evaluated independently (incl. `contactLanguage \|\| 'not known yet'`, `timezone \|\| 'UTC'`, the `cccc, yyyy-MM-dd HH:mm` business time, `JSON.stringify(terms)` byte for byte). At most `Ai / MaxToolIterations` = 10 model calls, as LangChain's default; at the limit the output is "Agent stopped due to max iterations." as in n8n. **Differs (decided):** the final answer is a strict JSON schema of the 7 fields (prompt text unchanged), so it can't come back as plain text. No temperature is sent in either engine. |
| 3 | Memory: key conversationId (test: session id), last 10 exchanges, exchange = messageText + raw final output | **Same**, stored in `crm.bot_memory`. Saved as soon as the agent finishes (also for unreadable output and the max-iterations text), not when the model call fails, like Simple Memory. The trigger's conversationId already is the test session id for a test. **Differs:** after a switch to code, memory starts empty (n8n's was in n8n's RAM), and it now survives restarts. |
| 4 | Tools: names, descriptions, AI-filled and fixed parameters exactly as the n8n nodes; same logic as `/api/ai` incl. TOOL_NOT_ALLOWED, FEATURE_NOT_INCLUDED, test simulation, `bot.tool_called` | **Same.** Descriptions and `$fromAI` schemas are extracted by script and tested against the nodes. Fixed values match: search limit 5, clientId, contactId, `update_contact_details` defaults `''`, `update_contact` sends only the language. Every call goes through `BotToolService.CallAsync`, which runs the same checks, outcomes, simulation and trace step as the `/api/ai` filters (a test compares the routes, capabilities and features with AiController's attributes). **Differs (decided):** only the bot's allowed tools are offered (the server check still runs). **Differs (edge):** a tool error reaches the model as `{"status":…, "error":…}` JSON, where n8n wrote its own error text; the model carries on in both. |
| 5 | Decision → `action_needed`: reply / schedule / cancel / handoff / anything else | **Same.** Reply sends `message`. Schedule sends `{scheduledStart, serviceId \|\| null, notes "", reason \|\| null, confirmReplaceExisting, bookingId \|\| null}`; values the endpoint couldn't bind (no or a bad start, a bad id) end the run with nothing sent, as n8n's 400 did. Cancel sends `bookingId \|\| null`, `{reason \|\| null}`. Handoff sends `reason \|\| "The contact needs a team member."` and nothing else. An unknown action, a null action or unreadable output sends nothing (traced). A 409 on the reply stops quietly (traced `bot.reply_stopped`); any other reply error stops too. |
| 6 | Formatter: model (setting), system rendered the same way, user = `VERIFIED BACKEND RESULT:\n{JSON}\n\nLATEST CONTACT MESSAGE:\n{messageText}`; every schedule/cancel result goes to it | **Same.** Model `Ai / FormatterModel` = gpt-4o-mini; both prompts rendered from the extracted text and tested; the result JSON is printed exactly like `JSON.stringify($json)` (JsJson). Routing follows the switches exactly: `success` true with BOOKED/RESCHEDULED (CANCELED), or any failure code incl. TOOL_NOT_ALLOWED and FEATURE_NOT_INCLUDED (fallback output), goes to the formatter, then its text is sent. **Note:** the plan line "every result" is slightly broader than n8n: a `success: true` with another code went nowhere in n8n and goes nowhere here (it can't happen today). An error answer (4xx/5xx) of schedule/cancel sends nothing, like n8n's failed HTTP node. Tests cover every schedule and cancel code with the AiTestController canned results. |
| 7 | Billing unchanged: `ai_reply` charged where it is today | **Same.** The reply goes through the same `BotConversationService.ReplyAsync` (live: `MessageService.SendAiReplyAsync`; test: `BotTestService`). |

**Other differences (decided in the plan):**
- **Background runs:** one run at a time per conversation; n8n could run two quick messages in parallel.
- **Concurrency:** different conversations run in parallel up to `Ai / MaxConcurrentRuns`.
- **Tracing:** every step is traced in the message's own trace (`bot.run_started`, `bot.model_called` with tokens and ms,
  `bot.decision`, `bot.tool_called`, `bot.action_result`, `bot.reply_stopped`, `bot.run_failed`).
- **Model API:** the OpenAI Responses API is used; the model and the prompt are the same.

## Calendar sync

| Contract | Result |
|---|---|
| Same payload in | **Same:** `CalendarSyncTriggerPayload`, built by the same `TriggerBookingSyncAsync`. |
| Same Google / Graph calls | **Same:** `calendars/primary/events` and `me/events`; POST create, PATCH update, DELETE cancel on `/{externalEventId}`; `Authorization: Bearer <accessToken>`, `Content-Type: application/json` (also on DELETE). Google body `{summary, description, start, end}`; Graph body `{subject, body:{contentType:"text", content}, start, end}`; description = `[eventDescription, dashboardLink].filter(Boolean).join('\n\n')`; `dateTime` is the payload's value as n8n received it, `timeZone` the payload's. Unknown provider or operation: nothing, as n8n's switches. Tested for all 6 pairs. |
| Then the same `ApplySyncCallbackAsync`, externalEventId = created id \|\| payload id | **Same** fields (`clientId, bookingId, calendarIntegrationId, requestId, success, externalEventId, errorMessage`), applied directly instead of through `/api/calendar-integrations/sync-callback` (which only calls the same method). |
| Failures reported (`success=false`) | **Differs (decided):** n8n never called back on a provider error. Code reports it, so the connection is marked unhealthy with the usual notification. |

**Shared with n8n:**
- **Primary calendar:** both write to the account's primary calendar. `externalCalendarId` (the calendar picked in the
  dashboard) is ignored.
- **Lost work:** a sync queued when the app restarts is lost and stays pending, like an n8n run that never called back.

## Benchmark generator

| Contract | Result |
|---|---|
| Same system prompt, model gpt-4o | **Same:** extracted prompt (tested equal), `Benchmark / GeneratorModel` = gpt-4o, no tools, no memory. |
| Input = the payload JSON | **Same:** `{generationId, clientId, chunks:[{documentId, chunkId, documentTitle, content}]}` printed like JSON.stringify (assumes n8n turned `$json.body` into compact JSON, its usual rendering of an object). |
| Output parsed by `GeneratorResponseParser`, stored by `ReceiveGenerationResultAsync` | **Same** as `send_questions` → callback: the raw output is parsed (n8n did `JSON.parse` and the callback parsed it again), then `requireEcho: false` as the callback. |
| Admin call answers as today | **Same:** queued and acknowledged at once, so "Generate" answers pending. |
| Failures | **Differs (decided):** a model error or an unusable output fails the generation with the reason; n8n stopped and the generation timed out. |

## Fixed in N4

- The test panel's "couldn't be reached" note no longer names the n8n workflow (en, ar, fr).
- The benchmark's "not configured" message names what to set for the engine in use.
- The Configuration page offers the three switches as `n8n` / `code` dropdowns (generic: any setting whose pattern is a list
  of values).
