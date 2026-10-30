# AI bots and Inbox settings: what was done

Commits (not pushed), both on `feature/go-live`:
- Main app `ef11dce`: AI bots, per-account Inbox settings, generic AI workflow.
- Admin portal `0330ef1`: AI per connected account on the client page.

Checks: 2,365 main-app tests and 94 portal tests pass. The old-project copy test (`test-copy.ps1`) passes. `schema.sql` re-runs cleanly over a database with the old client-wide Inbox settings row. The demo seed applies. I clicked through the Bots page, the bot editor, its test panel and Inbox settings in the real app.

## What you get

**Bots page** (sidebar, under setup): create a Support, Booking, Sales or Custom bot. Every new bot starts as a draft and opens in the editor. From the list you can Activate, Deactivate, "Use as custom bot" (an editable copy of a template) and Delete.
- **Template bots** keep their locked instructions and actions. You can still change the name, description, Knowledge Base, language and handoff settings.
- **Custom bots** add their own instructions (required) and allowed actions: Knowledge Base, services, availability, bookings, contact updates, pipeline.
- **Knowledge Base:** all sources, or a chosen list of documents and website sources.
- **Language:** automatic (the contact's language), or one of 12 fixed languages.
- **Handoff:** two policies ("asks for a person", or "asks, or the bot can't confidently help", the default), plus an optional handoff message.
- **Status:** Draft, Active or Inactive.
- No model, temperature, token or price settings anywhere.

**Test panel** in the editor: it runs the real n8n workflow with the bot's real instructions, Knowledge Base and allowed tools.
- Replies are charged as `ai_reply`, like a live conversation.
- Bookings, cancellations, contact updates and stage moves are simulated. The test contact gets one pretend booking checked against the real open slots.
- Handoff is shown once, with the handoff message, and ends the test until you press "Start over".
- Nothing is sent to WhatsApp or Telegram, and no real data changes.

**Settings > Inbox settings:** one form per connected WhatsApp or Telegram account.
- Each form has: AI on/off, the bot that answers (active bots only), answers new conversations, and answers reopened conversations.
- A badge on each account says who answers right now: the bot, or the team and why.
- One bot per account; one bot can serve several accounts. Turning AI off on one account never touches another.
- The team answers when AI is off, no bot is chosen, the bot is inactive, or the plan has no AI assistant.
- The AI tab on Business Info is gone.

**Plans:** `ai_assistant` still decides whether a client can use AI at all. `max_ai_bots` (plus add-ons) now means how many bots can be **active** at once. Activating one more is refused. After a downgrade, the newest active bots stop answering (reason `ai_bot_limit`).

**Handoff:**
- Only a real AI-to-team switch logs the handoff, notifies members and sends the bot's handoff message, once.
- The conversation row is locked while it switches, so retries and two messages arriving together can't send it twice.
- The message is skipped outside WhatsApp's 24-hour window.
- A member's Take Over and a billing hand-over never send it.

**Security (enforced in Omni, not just the prompt):** every `/api/ai/*` call must name its bot (`X-Omni-Bot-Id`).
- Omni allows only the calling client's own active bot, and only the actions that bot has. Anything else gets `TOOL_NOT_ALLOWED`.
- `search_knowledge` only searches that bot's sources.
- Booking tools still need the plan's bookings feature.

**Admin portal:** the client page shows the plan's AI limits and the client's bots. For each account you can switch AI on or off, pick the bot and set the two switches.

## Manual n8n steps (please do these)

1. **Re-import `docs/n8n/omni-ai-assistant.json`** as a new workflow, then attach the **Omni ingest key** and **OpenAI** credentials to every HTTP and model node. Three tool nodes are new: `get_services`, `update_contact_details`, `update_stage`.
2. **Point the HTTP nodes at your app:** replace `https://sculptflowapp.onrender.com` with your app's URL, in the new nodes too.
3. **Switch the main app over:** activate the workflow, copy its webhook URL into `N8n:AiWebhookUrl`, and turn the old AI workflow off.
4. **Old workflow won't work:** the old one gets `TOOL_NOT_ALLOWED` on every tool. It also posts replies to the dashboard send route, which no longer accepts AI replies (it is member-only now).

What changed in the workflow, for reference:
- The prompt is generic, with the bot's own instructions inserted as "YOUR BOT".
- Every call back to Omni sends `X-Omni-Bot-Id` and `X-Omni-Test-Session` from the trigger.
- The reply node posts to `POST /api/ai/conversations/{id}/reply` with `{content}`.
- The schedule and cancel result switches route `TOOL_NOT_ALLOWED`, and any unknown code, to the formatter, so the contact always gets an answer.

## Database / deploy

- Run the new `Database/schema.sql`. It adds `crm.bots`, `crm.bot_knowledge_sources`, `crm.bot_test_sessions` and `crm.bot_test_messages`, and turns `crm.inbox_settings` into one row per account. Old client-wide rows are deleted.
- `seed-config.sql` adds four settings: `Bots:InstructionsMaxLength`, `HandoffMessageMaxLength`, `TestMessageMaxLength`, `TestSessionHours`.
- After deploying, AI is **off on every account** until a client creates a bot and turns it on in Inbox settings. The data copy from the old project doesn't carry the old AI switches either.

## Limitations

- **Conversations don't store their account.** Today each client can have one connection per channel (a unique index), so a conversation's account is found by client + channel. Real multiple WhatsApp numbers would need `channel_integration_id` on conversations; the settings are already per account.
- **Instagram/Facebook connections** have no AI settings, because Omni can't send replies there yet.
- **The test panel polls** every 1.5 s, for up to 90 s, and only runs once the n8n workflow above is imported. Without it, the panel shows "the AI workflow couldn't be reached".
- **The prompt asks for `on_request` handoffs; Omni doesn't force them.** It can't tell "can't help" from "chose to hand off". An explicit request for a person is always honoured.
- **No platform-admin API for bots yet.** The portal shows bots read-only on the client page.
- The legacy `book_slot` and `reschedule_booking` AI endpoints were removed (n8n didn't use them).
- **Unrelated bug spotted, not fixed:** `wwwroot/js/inbox.js` line 52 reads `new ErroromniText.t(...)`, which throws when a GET fails.
