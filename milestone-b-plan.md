# Milestone B: execution plan (UI languages: English, Arabic with right-to-left, French)

Written 2026-10-20 from `generic-rename-plan.md` (v2 + "Final decisions") and `milestone-a-progress.md`. Checked against
`SculptFlowApp` `refactor/generic-platform` 2b72d25 (plus its working tree) and `SculptFlowAdmin` `refactor/generic-platform`
3c7c60f. Counts below were measured today; sizes are my estimates.

Planning written first; sessions log their progress in `milestone-b-progress.md`.

## 1. What Milestone B delivers

The client dashboard in **English, Arabic (full right-to-left layout) and French**. Each user picks a language in the
header; a user who hasn't picked one sees the client's default language, which the client owner sets in Business info.
Visitors who aren't signed in (sign-in, register, Google completion) pick on the page or get their browser's language.
Dates, numbers and plurals follow the language (Arabic keeps Latin digits 0-9). Business-type words and stage labels
for `clinic` and `general` exist in all three languages. The AI keeps answering in the contact's language, now with the
contact's language stored as a clean code and used when a message has no language of its own. The admin portal stays
English permanently; it only gains a few language fields and the kit's right-to-left-ready CSS. Go-live features
(invites, opt-out, reminders) are Milestone C.

### Where things stand after Milestone A (measured)

| Area | Today | B's work |
|---|---|---|
| Strings | `Resources/i18n/en.json`: **1,563 keys, ~7,300 words** (largest groups: campaigns 179, knowledge 136, integrations 105, billing 95, businessInfo 93, inbox 80). Every page, all 21 JS files and client-facing server messages already read keys | `ar.json`, `fr.json` with the same keys |
| Language choice | `ITextContext` picks `users.language` → `clients.default_language` → `Accept-Language` → `en`, limited to languages that have a strings file. Both columns exist with checks `en/ar/fr`. **Nothing writes `users.language`**; `default_language` is set only at sign-up | Switcher, settings, anonymous cookie, enabled-languages setting |
| Direction | `<html lang dir>` already follows the language in both layouts | The layout itself |
| CSS | `aurora.css` 3,735 lines with **58** left/right-bound declarations, `site.css` 1,551 lines with **69**; **0** logical properties, **0** `[dir]` rules. The portal's `aurora.css` is byte-identical | Logical properties + a few `[dir=rtl]` rules |
| Fonts | Inter (variable, **latin only**, no Arabic glyphs) + Material Symbols subset, self-hosted | Arabic font, mirrored directional icons |
| Plurals | Chosen in code with `n == 1 ? "….one" : "….other"` (e.g. `Dashboard/Contacts.cshtml:137`, `KnowledgeBase/Index.cshtml:63`); 31 `.one`/`.other` keys | Arabic needs zero/one/two/few/many/other |
| Dates/numbers in the browser | `local-time.js` formats with the **browser's** locale (`toLocaleString(undefined, …)`); 5 other scripts use `omniText.lang` (`'ar'` would give Arabic-Indic digits) | One locale tag with Latin digits |
| Dates/numbers on the server | Production runs the **invariant culture** (UI_GUIDE §0.4); 17 `N`/`C`/`P` and 27 explicit date formats | Explicit display culture |
| Business-type words | `core.business_types` name/terminology and `crm.pipeline_stages` labels are jsonb per language; **only `en` filled**. The portal already edits ar/fr fields with `dir=rtl` | Fill ar/fr for both built-in types |
| AI | Prompt already says "always reply in the contact's language"; `terms` come in the client's default language. `contacts.preferred_language` is free text (varchar 20) written by `update_contact`, not shown on the contact page, not in the trigger payload | Clean codes, payload field, fallback rule, contact page |

**Starting point:** both repos are clean on `refactor/generic-platform` (main app 5ff0659, portal 3c7c60f, checked
2026-10-20 after the subscription-plans thread committed its work). S1 starts from there.

## 2. Decisions I need from Mohammad before S1

**Confirmed 2026-10-20: Mohammad accepted every default below ("Defaults ok").** Each session may commit (never push) on
`refactor/generic-platform` once its checks pass.

| # | Question | My default |
|---|---|---|
| D1 | Branch | **Stay on `refactor/generic-platform` in both repos** (you said B comes first, then one deploy of everything). A and B merge to `main` together, once, after S7 |
| D2 | May each session **commit (not push)** once its checks pass? | **Yes**, one commit per session, as in A |
| D3 | Arabic style | **Modern Standard Arabic**, Gregorian months as "يناير، فبراير…" (understood everywhere), **Latin digits** (already decided). The alternative is Levantine months ("كانون الثاني…") |
| D4 | Arabic font | **Noto Sans Arabic** (variable, OFL licence), self-hosted like Inter, loaded only for Arabic characters (`unicode-range`), so English pages showing an Arabic contact message also render it well. S2 downloads one `woff2` from the `@fontsource-variable/noto-sans-arabic` npm package (**needs your OK in S2**, ~150 KB). Alternative: IBM Plex Sans Arabic |
| D5 | Languages live at go-live | New setting `Localization:Languages` (default `en,ar,fr`). The single deploy turns on all three only if the native review is applied by then; otherwise it goes out with `en` and you switch the setting on later, no redeploy |
| D6 | Native reviewers | **You find one Arabic and one French reviewer.** They get a CSV (key, English, translation, where it's used) and a Staging login with "show text keys" on, so they review in context and report keys |
| D7 | Where language is chosen | **Header menu** (each user, with "Use the business default"), **Business info** (client owner: the client default), **sign-in/register pages** (visitors, cookie). Members can't change another user's language |
| D8 | Contact language | Store **two-letter ISO 639-1 codes** (`en`, `ar`, `fr`, and any other language the AI meets, e.g. `es`); Arabizi/Lebanese Latin script counts as `ar`. Unknown text → empty |
| D9 | Tester | Still on hold (as in A); no tester session in B |

## 3. Design (single source of truth for every session)

### 3.1 Language resolution

| Who | Order |
|---|---|
| Signed-in `client` / `member` | `users.language` → `clients.default_language` → `en` |
| Visitor (sign-in, register, Google completion, Status/Error without a user) | cookie `omni.lang` → `Accept-Language` → `en` |
| Machine routes: `/api/platform-admin/*`, `/api/ai/*`, n8n ingest, provider webhooks, background jobs | **always `en`** (portal and n8n read English; today a stray `Accept-Language` would change it) |

- Only languages in `Localization:Languages` **and** with a strings file count; a disabled language falls through to the
  next step. The picker lists each language in its own name (English, العربية, Français), never translated.
- Register and Google completion keep "client default = the language the owner signed up in" (now the visitor's
  choice).
- `users.language` lives in the cached `UserAccess`; saving the user already clears it (CacheKeys comment), so a change
  shows on the next request.

### 3.2 Culture, dates and numbers

- **`CurrentCulture` stays invariant** for every request (form binding, `value="@x"` in inputs, `data-*` numbers read by
  JS, URLs and JSON all keep `.` decimals). Only display goes through the language's culture: a new
  `AppCulture.For(language)` (`en-US`; `fr-FR`; `ar` cloned with the **Gregorian calendar** and **Latin number symbols**,
  because plain `ar-SA` would print Hijri dates and `ar` uses `٫` as the decimal sign) exposed as `ITextContext.Culture`.
  `CurrentUICulture` is set per request for libraries that read it.
- Razor/page-model display formatting uses helpers that take that culture (`T.Number`, `T.Money`, `T.Date`), and
  `UiTextHelper.Amount` / `TextFormat` / `TextHtml` format arguments with it. A guard test fails on `ToString("N…")`,
  `("C…")`, `("P…")` or a month/day date pattern in `Pages/**` without a culture argument.
- Browser: `omniText.locale` = `en-US`, `fr-FR`, `ar-u-nu-latn-ca-gregory` (Latin digits, Gregorian), plus `omniText.dir`.
  `local-time.js` and every `toLocale…` / `Intl.*` call use `omniText.locale` (machine conversions that rely on `en-US` /
  `en-CA` output stay as they are). Times stay 24-hour where they are today.
- `<input type="date|time|number">` keep invariant values; their pickers follow the browser's own language (a browser
  limit, noted in the UI guide).

### 3.3 Plurals

- `T.Plural("contacts.count", n, args)` and `omniText.plural('contacts.count', n, [args])` pick `key.{category}` by CLDR
  rules: en (one, other), fr (one for 0 and 1, other), ar (zero, one, two, few = n%100 in 3–10, many = n%100 in 11–99,
  other). C#: a small `PluralRules` table with tests per boundary; JS: `Intl.PluralRules(omniText.locale)`. Missing
  category → `other`.
- Every `== 1 ? ".one" : ".other"` site moves to the helper. `en.json` keeps `.one` + `.other`; `ar.json` may add
  `.zero`, `.two`, `.few`, `.many`.
- Arabic counted nouns don't agree with a glued term (3–10 plural, 11+ singular), so Arabic count strings put the term
  as a label (`"{contacts}: {0}"`) or use a fixed Arabic noun. Placeholder order is free per language.

### 3.4 Strings files and their guard

- `ar.json`, `fr.json`: same flat keys as `en.json`, UTF-8, embedded like `en.json`; per-key fallback to English stays.
- `Guards/UiTextKeysTests` grows language rules: no key that `en.json` lacks (plural categories excepted); each
  translation uses only placeholders its English key has, and never adds `{0}`-style arguments; plural families keep
  `.other`; values are not left as the English text unless listed in `Guards/i18n-same-as-english.txt` (brand names,
  "WhatsApp", "OK"…).
- **Ratchet** (as `PendingScopes` in A): `IncompleteLanguages` lists key groups still missing per language; the test fails
  on a missing key outside the list and on a listed group that is already complete. Empty at the end of S6.
- Term placeholders in Arabic: capitalized forms (`{Contact}`) are the same word; use verbal nouns for buttons
  ("إضافة {contact}") so no verb has to agree with the term's gender.
- `docs/i18n/glossary.md` (S3): the fixed words per language (the six terms, stage labels, Save/Cancel/Delete, Inbox,
  Dashboard, Settings…), so S4–S6 and the reviewers use one vocabulary.

### 3.5 Right-to-left layout

- **Logical properties** in `aurora.css` and `site.css` (`margin-inline-start`, `padding-inline-end`, `border-inline-end`,
  `inset-inline-start`, `text-align: start`, `border-start-end-radius`…). They render the same in left-to-right, so the
  portal can keep a byte-identical kit. `[dir=rtl]` rules only for what logical properties can't express:
  `translateX`/slide-in animations, `background-position`, gradients with `to right`, shadow x-offsets, the calendar
  drawer's slide direction.
- **Icons**: `.au-icon--dir` mirrors under `[dir=rtl]` (`transform: scaleX(-1)`), applied to `arrow_back`,
  `arrow_forward`, `chevron_left`, `chevron_right`, `send`, `logout`, `undo` and any new directional icon (rule in the UI
  guide). Prev/next buttons keep their meaning (in Arabic "next" points left).
- **Mixed text**: phone numbers, emails, URLs, times, ids, codes, amounts with currency codes → `dir="ltr"` (or `<bdi>`)
  so `+961 3 123 456` never flips; `<input type="tel|email|url">` get `dir="ltr"`. **User content** (message bubbles,
  contact names, notes, conversation previews, knowledge entries, template bodies) gets `dir="auto"`: this also fixes
  Arabic messages shown in an English dashboard today.
- Places to check by hand: sidebar (side, border, accent bar, collapsed rail), header menus and bell dropdown, toasts,
  calendar grid (day order, time column side, now-line, drawer), inbox (bubble sides, composer, voice-note player),
  stacked phone tables (`data-label`), Business info hours grid, campaign audience cards, template preview, pagers,
  breadcrumbs/back headers.

### 3.6 Business-type words and stage labels

- `schema.sql` fills `ar` and `fr` for `name`, `terminology` (one/other per term) and every `crm.pipeline_stages.labels`
  of `clinic` and `general`, **only where that language is missing** (`jsonb` merge where the existing value wins), so
  portal edits survive re-runs, like the S9 `ai_profile` rule. The copy script doesn't copy business types (they come
  from `schema.sql`), so it needs no change for this.
- `ai_profile` stays English (the prompt tells the AI to answer in the contact's language).

### 3.7 AI language

- `contacts.preferred_language` holds a two-letter code (D8): `ContactLanguage.Normalize` maps "Arabic"/"arabizi"/
  "Lebanese"/"ar" → `ar`, "French"/"français" → `fr`, any valid ISO 639-1 code → itself, anything else → null (with a
  trace step). Used by `update_contact`, the contact create/update APIs and the copy script (old free text mapped the
  same way; `test-copy-assert.sql` + `test-old-extras.sql` updated). Check constraint `^[a-z]{2}$` or null.
- Trigger payload gains `contactLanguage`; the prompt rule becomes: reply in the language of the contact's latest
  message; if it has none (emoji, number, attachment, voice note without transcript), use `contactLanguage`, else the
  client's default `language`. Set the contact's language with `update_contact` (two-letter code) when it is first seen
  or changes.
- AI-facing texts (tool results, slot errors, `text-ok` messages) stay English. Messages sent first (Milestone C
  reminders, campaigns) will read `preferred_language` → client default.

### 3.8 New settings (repo rule: ConfigDefaults + IConfigManager + `docs/configuration.md` + `seed-config.sql` row)

| Setting | Type, default | Meaning |
|---|---|---|
| `Localization:Languages` | string list, `en,ar,fr` | Languages offered and used; `en` is always on |
| `Localization:LanguageCookieDays` | int, `365` | Lifetime of the visitor's `omni.lang` cookie |
| `Localization:ShowTextKeys` | bool, `false` | Review mode: when on, `?textKeys=1` adds each string's key as a tooltip (`data-text-key`) for reviewers. Refused in Production by code |

## 4. Rules for every session

- **Repo, branch**: `refactor/generic-platform` (D1) in the main app (S sessions) and the portal (P sessions). Read the repo's
  `CLAUDE.md` first. Never read `PROJECT_HANDOFF.md` or `omni-*` / `new-omni-*` HTML files.
- **Commits**: only per D2. Never stage `launchSettings.json`; never push.
- **One writer per repo**: S sessions run one after another; P sessions only run alongside S sessions.
- **Progress log**: each session appends to `milestone-b-progress.md` (workspace root, not in git): test count (floor =
  the last count logged; A ended at 1,886 main app / 51 portal, plus whatever the other thread's commit adds),
  `IncompleteLanguages` groups closed, settings added, anything deferred. The next session reads it first.
- **Docs in the same change**: `docs/system-design.html` (§3 frontend, §4 backend, §7 n8n contract, §8 schema, §9
  configuration, "Last verified", change-log row), `docs/configuration.md`, `docs/UI_GUIDE.md` (§0.4, §3.2, Appendix A
  when `aurora.css` changes), repo `CLAUDE.md` "UI text" rule.
- **New text** in any session goes into all three files (English, plus an Arabic and French draft), so nothing new joins
  the backlog.

### Standard checks (end of every main-app session)

1. `dotnet build Omni.App.slnx`: 0 errors, no new warnings. Output to scratchpad; read only failures and the summary.
2. Throwaway Postgres (port 55433, memory recipe), `OMNI_TEST_DB` set, **full** `dotnet test`: 0 failures, passed count ≥
   the floor. Guards green: LeftoverNames, UiTextKeys (with the language rules and ratchet), ServerMessageKeys,
   N8nContract, CopyScriptCoverage, ConfigSettings, and the new display-format guard.
3. If the session touched `schema.sql` or seeds: `schema.sql` + `seed.sql` + `seed-config.sql` + `seed-demo.sql` apply
   **twice** on an empty DB; a portal-edited business-type label survives a third run. If it touched the copy script:
   `test-copy.ps1` passes.
4. App boots in Staging on the scratch DB (env connection string, blank n8n URLs). With the built-in browser, every page
   the session touched opens in **en, ar and fr** (from S2: Arabic right-to-left) at desktop and 390px, light and dark on
   the shell plus the busiest pages: 200, no JS console errors, no raw key on screen, no horizontal scroll at 390px
   (`scrollWidth > innerWidth` check in the console).
5. Docs and progress log written; `git status` shows no secrets and no `launchSettings.json`.

Portal sessions: portal build, `Omni.Admin.Tests` (≥ 51), leftover guard, touched pages opened against a locally
running main app.

## 5. Sessions

Order: **S1 → S2 → S3 → S4 → S5 → S6 → S7** in the main app. **P1** after S2, **P2** after S3 (both alongside later S
sessions). Sizes are rough.

### S1 · Language plumbing: choice, culture, plurals, guards
Opus, **high** effort (design) · size L · after D1–D9.

- On `refactor/generic-platform` (D1); record the baseline (build warnings, test count).
- Settings from §3.8 (all four steps of the repo rule). `TextCatalog.Languages` ∩ `Localization:Languages`.
- Resolution per §3.1: visitor cookie `omni.lang` (set by `POST /language` with a local-only return URL, so no open
  redirect); machine routes pinned to `en` in `RequestTextMiddleware`; tests for each row of the table, a disabled
  language, a stored language that is no longer enabled.
- Writes: `PUT /api/members/me/language` (`{ language: "ar" | null }`, any signed-in user, own row only); Business info
  gains "Default language" (role `client` only, through the existing save, `UnauthorizedAccessException` → 403 for
  members); `PUT /api/platform-admin/clients/{id}/default-language` (answers `PlatformAdminChange`, audit) and
  `language` on the platform-admin member rows (for P2).
- Switchers: header user menu (list + "Use the business default"), `_AuthLayout` (sign-in, register, Google completion,
  Error/Status). Page reloads after a change.
- Culture per §3.2: `AppCulture`, `ITextContext.Culture`, `CurrentUICulture`, `T.Number`/`T.Money`/`T.Date`, formatting
  in `TextFormat`/`TextHtml`/`UiTextHelper.Amount`; move the 17 number and 27 date formats; display-format guard.
  Tests: `ar` prints `1,234.5` and "5 يناير 2026", `fr` prints `1 234,5` (narrow no-break space), form posts of `12.50`
  still bind with an Arabic or French user.
- Plurals per §3.3 in C# and JS; every `== 1 ?` site moved; UiTextKeysTests learns `T.Plural("…"` / `omniText.plural('…'`.
- JS: `omniText.locale`, `omniText.dir`, `omniText.plural`; `local-time.js` and the 5 other scripts use the locale.
- Strings files: empty `ar.json` / `fr.json` (`{}`), language rules and the `IncompleteLanguages` ratchet (every group
  pending for both), `Guards/i18n-same-as-english.txt`.
- Review tooling: `Localization:ShowTextKeys` + `?textKeys=1`; `docs/tools/i18n-review.js` writes the reviewer CSV
  (key, en, ar, fr, files that use it) to a path you pass (never into the repo).
- **Acceptance**: standard checks; with `ar` enabled the pages come up right-to-left with English text (fallback), the
  switcher changes the language for that user only, the owner's default applies to a member with no choice, visitors'
  choice survives to sign-up, platform-admin and `/api/ai` answers stay English with `Accept-Language: ar`.

### S2 · Right-to-left layout and the Arabic font
Opus, medium effort · size L · after S1. (Sonnet, medium is a cheaper option; RTL breaks show on every screen, so I'd
keep Opus.)

- `aurora.css` and `site.css` to logical properties (§3.5): all 58 + 69 direction-bound declarations, then the
  `[dir=rtl]` rules for transforms, gradients, shadows and the calendar drawer. Left-to-right must look **identical**:
  compare before/after screenshots of the shell and 5 pages in English.
- Font (D4): `wwwroot/fonts/noto-sans-arabic-wght-normal.woff2`, `@font-face` with the Arabic `unicode-range`, added
  after Inter in `--au-font-sans` (Latin keeps Inter). Line-height check for Arabic in buttons, badges and table cells.
- Icons: `.au-icon--dir` and the list in §3.5 across Razor and JS-built markup (`icons.txt` unchanged: same glyphs).
- Mixed text: `dir="ltr"` / `<bdi>` for phones, emails, URLs, times, ids, codes; `dir="auto"` on user content; input
  types per §3.5. Includes script-built markup (inbox, calendar, templates, campaign audience).
- Walk every page in Arabic (English text is fine at this point) at desktop and 390px, light and dark, and the places
  listed in §3.5; fix what's off with logical properties, never page-specific hacks.
- UI guide: §0.4 (RTL rules, icon list, mixed text, browser date pickers), §3.2 (the font), Appendix A = the new
  `aurora.css` verbatim. Repo `CLAUDE.md`: "no physical left/right in CSS; `dir=auto` on user content".
- **Acceptance**: standard checks; no `margin-left`/`right:`-style physical declarations left in either CSS file except
  ones marked `/* rtl-ok: reason */`; a small CSS guard test enforces it.

### S3 · AI language and business-type words
Opus, **high** effort (contract + copy script) · size M · after S2.

- `ContactLanguage.Normalize` + check constraint (§3.7) in `schema.sql` (guarded, re-runnable, existing values
  normalized in place first); `update_contact`, contact create/update APIs, the contact page shows and edits the
  language (a short list en/ar/fr + "Other" with a two-letter code).
- Copy script: map old free text with the same rules; `test-copy-assert.sql`, `test-old-extras.sql` (add Arabic, French,
  "Arabizi" and junk values); `test-copy.ps1` passes.
- `AiTriggerPayload.contactLanguage`; prompt rule from §3.7 in `docs/n8n/omni-ai-assistant.json`; `N8nContractTests`
  field lists; system-design §7.
- Business-type words (§3.6) for `clinic` and `general`: names, six terms × one/other, every stage label, in Arabic and
  French; `docs/i18n/glossary.md`. The portal-edit-survives check (standard check 3).
- **Acceptance**: standard checks; a contact walks en → ar on two messages in a payload test; an Arabic and a French
  client see their stage labels and terms on Contacts and Inbox.

### S4 · Arabic strings, part 1 (the daily screens)
Opus, medium effort · size M–L · after S3.

- `ar.json` for: `common`, `nav`, `header`, `layout`, `aurora`, `auth`, `login`, `register`, `signup`, `identity`,
  `status`, `error`, `noClient`, `messages`, `time`, `timeView`, `days`, `values`, `stages`, `statusHelp`, `dashboard`,
  `inbox`, `contacts`, `contactDetail`, `bookings`, `bookingDetail`, `orders`, `services`, `members`, `clients`,
  `businessTypes`, `businessInfo`, `availability`, `notifications`, `terms`, with their `*.errors.*` keys (~780 keys).
- Follow the glossary and §3.3/§3.4; read each string where it renders (key overlay on) rather than as a list; move
  closed groups out of `IncompleteLanguages`.
- **Acceptance**: standard checks; those screens fully Arabic for a `clinic` and a `general` client, right-to-left, no
  English left except the same-as-English list.

### S5 · Arabic strings, part 2 (setup and marketing screens, server messages)
Opus, medium effort · size M–L · after S4.

- `ar.json` for the remaining groups: `campaigns`, `knowledge`, `knowledgeSettings`, `website`, `integrations`,
  `channels`, `calendars`, `oauth`, `billing`, `templates`, `whatsAppHealth`, with their `*.errors.*` keys (~780 keys).
  Arabic `IncompleteLanguages` empty.
- Write the reviewer CSV for Arabic (`i18n-review.js`) into the scratchpad and hand it over through the progress log.
- **Acceptance**: standard checks; every page in Arabic for both business types; keyed server errors (a duplicate
  service, an entitlement refusal, an Identity password error) show in Arabic.

### S6 · French strings
Sonnet, medium effort · size M · after S5 (any time after S3 if you want it earlier; same repo, so not in parallel).

- `fr.json` for all groups, following the glossary; French `IncompleteLanguages` empty (the ratchet list is now empty).
- Typography: no-break space before `: ; ! ?` and inside « », French date order. Reviewer CSV for French.
- **Acceptance**: standard checks; every page in French for both business types, no clipped buttons or labels at 390px
  (French runs ~20% longer).

### S7 · Native review fixes and final verification
Opus, medium effort · size M · after S6 and the reviewers' answers (D6), P1 and P2.

- Apply the reviewers' corrections (by key); re-check each changed string in context.
- Merge `main` into the branch in both repos if anything landed there; re-run every check in both repos,
  `test-copy.ps1`, and a full page walk in all three languages.
- Docs sweep: `system-design.html` (frontend i18n, language resolution, culture, settings, n8n contract, change log),
  `configuration.md`, `UI_GUIDE.md`, README, both `CLAUDE.md` files (UI-text rule: three files, plurals, culture helpers,
  RTL rules), `deploy-runbook.md` (setting `Localization:Languages`, n8n prompt re-import, the staging conversation in
  three languages).
- Write the Milestone B summary for you: what changed, settings, the steps in §7.
- **Acceptance**: every check green; `IncompleteLanguages` empty; both reviewers' lists applied.

### P1 · Portal kit sync
Sonnet, low effort · size S · after S2.

- Copy the new `aurora.css` into `Omni.Admin/wwwroot/css/` and Appendix A of the portal's `docs/UI_GUIDE.md` (the kit
  stays identical in both apps). The portal stays left-to-right English: no `dir`, no Arabic font.
- Before/after screenshots of the portal shell and 5 pages must be identical.
- **Acceptance**: portal build, tests, leftover guard; every portal page opens against the local main app.

### P2 · Portal language fields
Sonnet, medium effort · size S · after S3 (uses S1's endpoints and S3's contact codes).

- Client details: change the client's default language (`PUT /api/platform-admin/clients/{id}/default-language`, audit).
- Members list: each user's language ("Business default" when empty). Contact details: language shown by name.
- Business types list and edit page: a "missing Arabic / missing French" badge when a name, term or stage label is
  empty in that language (computed from the existing response, no new endpoint).
- Copied shapes updated; `MAIN_APP_SYNC.md` last-checked commit.
- **Acceptance**: portal build, tests (+ the new ones), leftover guard; the language change shows on the main app's
  Business info after the client cache clears.

## 6. Dependencies at a glance

| Session | Repo | Needs | Model, effort |
|---|---|---|---|
| S1 | main app | D1–D9, clean tip | Opus, **high** |
| S2 | main app | S1 | Opus, medium |
| S3 | main app | S2 | Opus, **high** |
| S4 → S5 | main app | S3 | Opus, medium |
| S6 | main app | S5 | Sonnet, medium |
| S7 | both | S6, P1, P2, reviewers' answers | Opus, medium |
| P1 | portal | S2 (runs alongside S3–S6) | Sonnet, low |
| P2 | portal | S3 (runs alongside S4–S6) | Sonnet, medium |

Rough total: S1–S3 ~1 week, S4–S6 ~1 week of drafting, then the reviewers' time and S7: about **2–2.5 weeks**, as in
the v2 plan.

## 7. What Mohammad does himself

**Before S1**
1. Answer D1–D9 (or accept the defaults).
2. Find an Arabic and a French reviewer (D6).

**During B**
3. In S2, approve the Arabic font download (D4).
4. After S5 and S6, send the reviewer CSVs and a Staging login (with `Localization:ShowTextKeys` on) to the reviewers;
   return their corrections (by key) for S7.

**At go-live (one deploy of A and B together, following `deploy-runbook.md`, which S7 updates)**
5. Production: `Localization:Languages` = `en,ar,fr` once the review is applied (the Configuration page in the portal
   or the settings table); `Localization:ShowTextKeys` stays off.
6. Import `omni-ai-assistant.json` in n8n (S3 changes the prompt and payload), then a scripted conversation in
   English, Arabic (script and Arabizi) and French: ask, book, reschedule, cancel, hand-off, an emoji-only message.
   Check the reply language and the contact's stored language.
7. Set your own clients' default language in the portal if any should open in Arabic or French.

## 8. Risks specific to B

- **Arabic quality** is the main risk: drafts are mine; nothing goes live in Arabic or French before the native review
  (D5 keeps production on `en` until then).
- **Layout regressions in English** from the CSS conversion: logical properties render the same in left-to-right, and
  S2 and P1 compare before/after screenshots.
- **Culture leaks into machine data**: avoided by keeping `CurrentCulture` invariant and formatting display explicitly
  (S1 tests form binding under Arabic and French users).
- **Text stored outside a request** (campaign recipient failure reasons, the AI's refusals) stays in the language it was
  worded in (English for n8n); accepted, listed in system-design §10.
- **One long branch**: A and B ship in one deploy, so `refactor/generic-platform` lives ~2.5 weeks longer; keep `main`
  frozen except small fixes and merge them in (S7 at the latest). The runbook's staging check now covers B too.
- **French length**: longer labels can clip on phones; S6 checks at 390px.
