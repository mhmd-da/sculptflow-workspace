# Milestone B progress log

Plan: `milestone-b-plan.md`. Each session appends its entry; the next session reads the last one first.

## S1 · Language plumbing: choice, culture, plurals, guards (2026-10-20)

Main app, branch `refactor/generic-platform` (D1: no separate branch; A and B ship in one deploy), on top of `5ff0659`.
Committed `b05fe92`, not pushed. Portal untouched.

**Result:** build 0 errors / same 8 old test warnings on a clean build; full `dotnet test` **1,984 passed**, 0 failed, 0 skipped
(throwaway Postgres, port 55433). Next session's floor: 1,984. All guards green (leftover names, UI text keys with the new
language rules, server messages, n8n contracts, copy-script coverage, config settings, endpoint protection, new display formats).

**Done (per the plan):**
- Settings `Localization:Languages` (default `en,ar,fr`), `Localization:LanguageCookieDays` (365), `Localization:ShowTextKeys`
  (false): ConfigDefaults, IConfigManager/ConfigManager (the list is read with `LanguagePicker.Enabled`), `configuration.md` (83
  settings), `seed-config.sql`.
- Language resolution in `TextContext`: signed in `users.language` → `clients.default_language` → Accept-Language → `en`; visitor
  `omni.lang` cookie → Accept-Language → `en`; an `/api` call without a signed-in user is always English (simpler and safer than a
  path list: n8n, portal and webhooks never carry a user cookie). `ITextContext` gained `Culture`, `Offered`, `ShowKeys`.
- Pickers: `POST /language` (`LanguageController`, form + anti-forgery, also for visitors; plan said `PUT /api/members/me/language`,
  a form post was simpler for both the header and the sign-in pages) → `MemberService.SetOwnLanguageAsync` for signed-in users,
  `omni.lang` cookie for everyone, local return URLs only. Header user menu and the sign-in pages' corner menu (`_LanguageChoices`,
  `.au-auth-tools`). Business info "Dashboard language" panel (owner only; members see it read-only) →
  `ClientProfileService.SetDefaultLanguageAsync`. Platform admin `PUT /api/platform-admin/clients/{id}/default-language`
  (`DefaultLanguageBody {language}`), `MemberRow.Language` (additive, last field).
- Culture: `AppCulture` (en-US, fr-FR, `ar` with Gregorian calendar + Latin number symbols; invariant fallback if the host has no
  ICU), `CurrentCulture` invariant for every thread (Program.cs), `CurrentUICulture` = the request's culture (middleware).
  `TextCulture.Display(...)` / `TextCulture.Date(..., DateStyle.*)`; moved Dashboard range label, Billing money, KB file size and
  upload limit, Integrations webhook tooltip, Business info closed dates, `UiTextHelper.Amount`. Text placeholders accept `{0:N0}`
  (C# and JS); the document-too-long error uses it.
- Plurals: `PluralRules` (CLDR en/fr/ar), `T.Plural` / `T.PluralKey`, `omniText.plural`; 22 sites moved; 13 `xOne`/`xOther` key
  pairs renamed to `x.one`/`x.other`; every `.one` text now carries `{0}` (French uses `.one` for 0).
- JS: `omniTextConfig` has `locale`, `dir`, `showKeys`; `omniText.locale` used by `local-time.js` and the 6 scripts that format
  dates/numbers.
- Strings: empty `ar.json` / `fr.json`; the browser copy of a translation carries English for missing keys (version covers both
  files). Guards: `UiTextKeysTests` (plural families, translation keys/placeholders, same-as-English list,
  `Guards/i18n-incomplete.txt` ratchet with every group pending for ar and fr), new `Guards/DisplayFormatTests`.
- Review tooling: review mode (`?textKeys=1` / `?textKeys=0`, session cookie `omni.textkeys`, refused in Production) appends
  ` ⟦key⟧` to each text (server and JS); `docs/tools/i18n-review.js <ar|fr> <out.csv>` writes the reviewer sheet (UTF-8 with BOM).

**Checks:** `schema.sql` + `seed.sql` + `seed-config.sql` + `seed-demo.sql` applied twice on an empty DB (usual `real[]` swaps;
demo embedding swap is `pg_temp.dummy_embedding()::vector` → a `real[]` literal) ✓, the three Localization rows present. Published
app in Staging on the scratch DB: sign-in page language menu (Arabic → `<html lang="ar" dir="rtl">`, English text as fallback);
demo client signs in English (client default), user menu shows "Clinic default (English)" + three languages; picking Français gives
"1 oct. – 20 oct. 2026" and "5 000"; owner set the default to Arabic on Business info ("Default language saved."), user back on
"business default" → Arabic, calendar title "أكتوبر 2026" (Latin digits). 15 pages 200, no console errors, no raw keys, no server
failures in the log.

**Decided here (beyond the plan):**
- Arabic culture is built from neutral `ar` (months يناير…, D3) with Latin symbols; JS uses `ar-u-nu-latn-ca-gregory`.
- `DateStyle` values: Date, MonthDay, WeekdayDate, MonthDayTime, MonthShort; English month-first, French/Arabic day-first.
- Picker items wrap the language name in `<bdi lang>` (a `dir=rtl` button flipped the menu row).

**Config / env changes for Render:** none required (seed-config adds the three rows). Production should set
`Localization:Languages` = `en` until the native review is applied (plan D5).

**For later sessions:**
- S2 (RTL): layout is still left-to-right inside `dir=rtl` (labels left-aligned on the sign-in page). `.au-auth-tools` already uses
  `inset-inline-end`. Turn on Arabic with an English fallback to walk pages: it already works on this branch.
- S4–S6: fill `ar.json` / `fr.json` by group and delete each finished group from `Guards/i18n-incomplete.txt`; list legit
  same-as-English texts (e.g. "{0} {contact}" if identical) in `Guards/i18n-same-as-english.txt`. Plural families may add Arabic
  `zero/two/few/many` keys.
- P2 (portal): `MemberRow.language` (new last field) and `PUT /api/platform-admin/clients/{id}/default-language` are ready.
- Still English by design: AI/n8n texts, `BookingService` slot text, crawler reasons (see S8c notes).

## S2 · Right-to-left layout and Arabic font (2026-10-20)

Main app, branch `refactor/generic-platform`, on top of `b05fe92`. Committed `a562596`, not pushed. Portal untouched.

**Result:** build 0 errors / 0 warnings (incremental); full `dotnet test` **2,017 passed**, 0 failed, 0 skipped (throwaway Postgres,
port 55433). Next session's floor: 2,017. Guards green, including the new `Guards/RtlStyleTests`.

**Done (per the plan):**
- `aurora.css` / `site.css` on logical properties (all physical margin/padding/border/inset/text-align/corner radii and the 12
  asymmetric shorthands). What logical properties can't express: `--au-x` (1, -1 under `[dir='rtl']`, 1 again under
  `[dir='ltr']`) multiplies every `translateX` (toasts, drawer, switch knob, off-canvas sidebar, nav hover, tooltips,
  progress sweep) and the sidebar active gradient angle; `background-position` of select chevrons and the search icon has
  `[dir='rtl']` overrides marked `rtl-ok:`. Removed `direction: ltr` from `.au-icon` (it made icon margins resolve left to
  right inside Arabic pages; ligatures render the same).
- Font (D4): `wwwroot/fonts/noto-sans-arabic-wght-normal.woff2` (166 KB, Arabic subset of
  `@fontsource-variable/noto-sans-arabic` 5.3.0, OFL), `@font-face` with the Arabic `unicode-range` in `fonts.css`,
  `'Noto Sans Arabic Variable'` right after Inter in `--au-font-sans`. Arabic in buttons, badges and table cells keeps the
  same heights as English (36 / 22 / 45 px measured).
- Icons: `[dir='rtl'] .au-icon--dir { scale: -1 1 }`; `Common/Helpers/IconDirection` (glyph list + `ClassFor`) used by
  `_EmptyState` and the campaign Send/Resume button; 25 Razor icons marked. No JS-built directional icons exist.
- Mixed text: `dir="ltr"` on phones, emails, URLs, WABA id, service codes, error codes, template names; `dir="auto"` /
  `<bdi>` on contact/member/campaign/service names, KB titles, notes, inbox bubbles, previews, composer, template previews
  (Razor + `inbox.js`, `bookings-calendar.js`, `campaign-audience.js`); `input[type=tel|email|url]` LTR by CSS in Arabic.
- Guard `Guards/RtlStyleTests`: physical sides in any `wwwroot/css/*.css`, in Razor `style=""` and in scripts
  (`.style.left` etc.), unmirrored directional icons; theory tests for the classifier and `IconDirection`.
- Docs: UI_GUIDE §0.4 (Right to left), §3.2 (Arabic font), Appendix A = new `aurora.css` verbatim; system-design §3.3 +
  change-log row; repo `CLAUDE.md` UI rule.

**Checks:** English unchanged: element boxes + direction-related computed styles on 22 signed-in pages at 1366 and 390
identical before/after (only differences: relative "x min ago" texts and the Arabic language-menu item now in Noto).
Arabic mirrors: every element box of the 22 pages in `ar` equals the mirrored `en` box at 1366 and 390, except text-width
changes from Arabic dates/months and English fallback text reordering inside lines; no horizontal scroll at 390 (also
sign-in, register, 404). Checked live: calendar drawer opens on the left, prev/next arrows mirrored, toast bottom-left
sliding in from the left, phone drawer slides in from the right. Theme only changes colours (no theme-specific direction
rules). Console: only the expected 404 test pages, Facebook's http warning and SignalR reconnects after app restarts.

**For later sessions:**
- P1 (portal kit sync): copy the new `aurora.css` (Appendix A) and `fonts.css` + the woff2.
- S4–S6: Arabic text inside English-ordered sentences: wrap names in `<bdi>` when a sentence embeds them (e.g.
  `bookings.today.next` builds text with a name; fine once translated, revisit if it reads oddly).
- `play_arrow` (campaign Resume button icon) is not in `icons.txt`: pre-existing missing glyph, run `node docs/tools/icon-font.js`
  in a later UI session.

## P1 · Portal kit sync (2026-10-20)

- Portal repo (`refactor/generic-platform`): `Omni.Admin/wwwroot/css/aurora.css` = main app's aurora.css from a562596;
  Appendix A of `docs/UI_GUIDE.md` replaced verbatim (the old appendix matched the old file, so the sync is clean).
  No `fonts.css`, woff2 or `dir` added: portal stays English, left to right.
- Checks: `dotnet test` 57 passed, 0 failed, build clean. Secrets scan of staged diff clean. Not pushed.
- Not done: before/after screenshots of the shell and 5 pages (needs the portal running against the local main app);
  left for the S7 page walk.

## S3 · AI language and business-type words (2026-10-20)

Main app, branch `refactor/generic-platform`, on top of `a562596`. Committed `09ded40`, not pushed.
Portal untouched (P1 ran in parallel in its own repo).

**Result:** build 0 errors / 0 new warnings; full `dotnet test` **2,079 passed**, 0 failed, 0 skipped (throwaway Postgres,
port 55433). Next session's floor: 2,079. All guards green. `test-copy.ps1` PASS (40 assertions, sign-ins ok).
`i18n-incomplete.txt`: group `terms` closed for ar and fr (default words = the `general` type's).

**Done (per the plan):**
- `Common/Helpers/ContactLanguage` (`Normalize`: ISO 639-1 codes, names in English/own language/Arabic, three-letter codes,
  tags `fr-CA` → `fr`, Arabizi/Lebanese/Franco → `ar`, else null). SQL twin `crm.normalize_language` in `schema.sql`;
  existing values normalized once, then `ck_contacts_preferred_language` (`^[a-z]{2}$`). `ContactLanguageWalkTests` runs every
  name, code and case through both.
- Writes: dashboard create/PATCH and `POST /api/contacts/{id}/stage` (`UpdateContactStageRequest.PreferredLanguage`, blank clears)
  refuse non-languages (`contacts.errors.invalidLanguage`, 400); AI `update_contact` maps names, ignores blanks and non-languages
  (keeps the stored one) with trace step `contact.language_ignored` (trace mapper summary added).
- Contact page: "Language" row in Details and "Language they write in" (Not known yet / English / Arabic / French / Other + code).
  City and timeline got `dir="auto"` (they flipped "3 months" in Arabic).
- AI: `AiTriggerPayload.contactLanguage` (after `language`) via `IAiProfileService.GetContactLanguageAsync` →
  `IContactRepository.GetPreferredLanguageAsync`. Workflow: new `update_contact` httpRequestTool (PATCH, body only
  `preferredLanguage` via `$fromAI`), prompt section "CONTACT LANGUAGE TOOL", reply rule = latest message's language →
  `contactLanguage` → `language`; result formatter has the same fallback. `N8nContractTests` updated (tool list, field list,
  update_contact body). Payload test: English then Arabizi → `contactLanguage` en then ar.
- Copy script: `crm.normalize_language(o.preferred_language)`; extras add Arabic / Français / Arabizi / EN-us / xx / ?? values.
- Business types: `schema.sql` merges ar + fr name, six words and every stage label of `clinic` and `general` (existing wins,
  per term; only rewrites rows that change). `docs/i18n/glossary.md` (types, stages, common dashboard words, status words, rules).
- Docs: system-design §4 (trace step, update_contact row), §7 (tool, payload, prompt rule), §8 (contacts, business types),
  change-log row; repo `CLAUDE.md` UI-text section (contact language + glossary).

**Checks:** schema + seed + seed-config + seed-demo applied twice on an empty DB ✓; a portal edit (Arabic label, Arabic term,
French name removed) → third `schema.sql` run kept both edits and restored only the missing French name, and an untouched type
wasn't rewritten (same `updated_at`). Published app in Staging on that DB: contact page saves "Spanish" as `es`, refuses "Klingon"
with the keyed message; clinic client in ar (مرضى, Arabic stage list and terms) and fr (Inbox info panel "Humain requis",
"Intervention"); demo client switched to `general` in the scratch DB: fr (Réservé, Présent, Client, entreprise) and ar at 375px
(no horizontal scroll). Console: only SignalR reconnects from the app restart.

**Decided here (beyond the plan):**
- The workflow had no `update_contact` tool; added one that can only send the language (identity and stage stay out of the AI's
  reach through it).
- Dashboard input is strict (400), AI input lenient (ignored + trace), so a bad guess never erases a known language.
- `ContactDetail` "Other" takes any language name or code (normalized), not only two letters.

**For later sessions:**
- S4–S6: use `docs/i18n/glossary.md`; fr `terms.contact.*` / `terms.service.*` are in `i18n-same-as-english.txt`. English UI
  strings still glue type words ("your عيادة"), fine once translated.
- P2: contact language is now always a code (or null); `MemberRow.language` and the default-language endpoint from S1.
- Mohammad at go-live: re-import `omni-ai-assistant.json` (new tool + prompt), check the `update_contact` node picks the
  "Omni ingest key" credential. S7 adds this to `deploy-runbook.md`.

## P2 · Portal language fields (2026-10-20)

- Portal repo (`refactor/generic-platform`), on top of `f639abb`. Committed `3949b5a`, not pushed. Main app untouched.
- **Result:** build 0 errors / 0 warnings; `dotnet test` **80 passed**, 0 failed (floor was 57). Leftover-names guard OK.
  Secrets scan of the staged diff clean.
- Done: client details "Dashboard language" panel (`PUT clients/{id}/default-language`, audit `client.default_language_changed`
  with from/to, no call when unchanged, refuses anything but en/ar/fr); members list, member page and the client page's members
  table show each user's language ("Business default" when null); contact details show "Language" by name ("Spanish (es)",
  via `CultureInfo`, falls back to the code) separately from country/city; business types list "Translations" column and edit
  page header show "missing Arabic / missing French" chips (`Common/Helpers/TranslationGaps`: name, any `TermName` word without
  both forms, any empty stage label), caption lists what's missing. Copied shapes `MemberRow.Language`, `DefaultLanguageBody`;
  `MAIN_APP_SYNC.md` checked against `09ded40`; README + UI_GUIDE updated.
- Not done: the live check (change the language in the portal, see it on the main app's Business info after the client cache
  clears) needs both apps running; left for the S7 page walk. `ClientProfileService.SetDefaultLanguageAsync` shows no cache eviction of its own (read, not tested).

## S4 · Arabic strings, part 1: the daily screens (2026-10-20)

Main app, branch `refactor/generic-platform`, on top of `09ded40`. Committed `e763835`, not pushed. Portal untouched (P2 ran
in parallel in its own repo).

**Result:** build 0 errors / the same 8 old test warnings; full `dotnet test` **2,081 passed**, 0 failed, 0 skipped (throwaway
Postgres, port 55433). Next session's floor: 2,081. All guards green.
`i18n-incomplete.txt` Arabic line now lists only the S5 groups: billing integrations calendars campaigns knowledge
knowledgeSettings website whatsAppHealth templates channels oauth. Closed for ar: the 37 S4 groups plus `languages` (2 keys,
in neither S4 nor S5's list, so done here).

**Done (per the plan):**
- `ar.json`: 836 keys (783 new drafts + plural forms), Modern Standard Arabic per the glossary. Arabic `zero/two/few/many` for
  attention counts, members count, open days, summary hours/days and relative times. Counts with a business-type word use
  `{0} {contact}` for one and the label form `{contacts}: {0}` for the rest.
- 30 Arabic keys listed in `i18n-same-as-english.txt` (term-only labels like `{Contacts}`, `{0} {contact}` counts, `{0} / {1}`,
  `{0}%`, `{0}`, email/URL placeholders).
- Code: `LocalTimeHelper.Ago` uses plural families `time.ago.minutes|hours|days.{one,other}` (en) instead of `time.ago.hour` +
  `time.ago.hours` (Arabic needs منذ 5 دقائق / منذ 11 ساعة). Test `Ago_counts_follow_the_language_plural_rules`.
- Fixed S3 leftover: `BuiltInTypeWordsTests.cs:77` comment ("consultation") failed `LeftoverNamesTests` once the file was tracked;
  marked `leftover-ok: a French word`.
- P2's question (does the platform-admin default-language endpoint clear the client cache?): **no bug**. It goes
  `ClientAdminService` → `ClientProfileService.SetDefaultLanguageAsync`, which loads the client tracked and saves, so
  `CacheInvalidationInterceptor` drops `core:client:{id}`. Added `CachingRepositoryTests.A_new_default_language_reaches_the_next_cached_client_read`
  (Postgres) to prove it. Note: `UserAccess` (membership cache) holds only `users.language`, not the client default, so it needs no clear.
- Docs: system-design §3 UI text bullet (Arabic coverage, `time.ago.*` families) + change-log row; glossary rule for Arabic with
  term placeholders (no «ال», no definite adjective, no gender agreement).

**Checks:** scratch DB (schema + seeds, real[] swaps), published app in Staging. Demo clinic in Arabic: dashboard, inbox, contacts,
contact page, bookings calendar, services + edit, members, Business info (details, hours, AI), 404 page: no English UI text left
except data (service names, sources, messages) and S5 groups; JS texts too (calendar "اليوم: لا توجد استشارات", "6 أيام مفتوحة",
rules summary). No horizontal scroll at 390 on those 8 pages; console only the 404s my own checks requested. Same client switched
to `general`: جهات اتصال / حجوزات / طلبات / خدمة read correctly.

**For the reviewer / S7:**
- The words arrive without «ال», so some labels read as indefinite idafa: «توقيت عيادة», «معلومات عيادة», «حسب خدمة» (ideal:
  «توقيت العيادة»). Fixing that needs a definite form per term (e.g. `{the_client}`) in business types: a design change, not
  a string fix. Where a sentence allowed it I left the word out («ما تقدمه», «من يتواصل معك»).
- «{contacts} جدد» (dashboard funnel) is correct for مرضى, loose for جهات اتصال.
- Numbers glued to a fixed noun outside plural families stay singular («10 دقيقة», «{0} حرفًا» in length errors): acceptable in
  UI Arabic, a reviewer may prefer plural families.
- Business info time inputs at 390px clip "PM" (browser's own 12-hour widget in an English browser); not caused by S4, check in S7.

## S5 · Arabic strings, part 2: setup and marketing screens, server messages (2026-10-21)

Main app, branch `refactor/generic-platform`, on top of `e763835`. Committed `328fac0`, not pushed. Portal untouched.
Model/effort as planned (Opus, medium).

**Result:** build 0 errors / 0 new warnings (same 8 old test warnings on a full build); full `dotnet test` **2,082 passed**, 0
failed, 0 skipped (throwaway Postgres, port 55433). Next session's floor: 2,082. All guards green.
`i18n-incomplete.txt`: the Arabic line is gone (all 11 S5 groups closed: billing integrations calendars campaigns knowledge
knowledgeSettings website whatsAppHealth templates channels oauth). Only `fr:` remains, for S6.

**Done (per the plan):**
- `ar.json`: 1,647 keys (811 new: 779 drafts + Arabic `two/few/many` plural forms for template blanks, website problems,
  "imported with N problems", KB entries, KB pages, upload pieces, channels needing attention, campaign send/resume confirms).
  Counts with a business-type word keep the S4 pattern (`{0} {contact}` for one, `{contacts}: {0}` for the rest); campaign
  progress/stats use label form («وصلت: {0} · قُرئت: {1}»). Path arrows are «←» in Arabic («الإعدادات ← الفوترة»).
- 6 keys added to `i18n-same-as-english.txt` (`billing.plan.price` "{0} / {1}", `campaigns.create.summary.one`,
  `campaigns.recipients.contact` "{Contact}", `knowledge.category.booking` "{Booking}", `knowledgeSettings.topK.range`,
  `whatsAppHealth.event.webhook`).
- Fix found on the page walk: Billing showed the stored period ("$99.00 / month") in every language. Now
  `billing.plan.perMonth` / `perYear` (en, ar, fr).
- Test `ServerTextTests.Keyed_server_errors_read_in_arabic_with_the_clients_arabic_words` (duplicate service, entitlement
  refusal, template duplicate, campaign status argument, Arabic plurals, Identity password error).
- Docs: system-design §3 UI text bullet + change-log row + Last verified 2026-10-21; glossary "Dashboard words" got the
  integration, billing and KB words (connect, WhatsApp Health, blank, plan, credit, entry, crawl, path arrow).
- **Reviewer sheet:** `docs/i18n-review-ar.csv` in the workspace root (not in git; 1,647 rows, UTF-8 with BOM, from
  `node docs/tools/i18n-review.js ar`). Hand it to the Arabic reviewer (plan D6).

**Checks:** scratch DB (schema + seeds, `real[]` swaps; S5 didn't touch SQL), published app in Staging, demo client user in
Arabic. 14 pages (campaigns list, create, 3 details, KB list, new and edit entry, KB settings, templates, WhatsApp health,
messaging integrations, calendar integrations, billing): 200, `dir=rtl`, no raw keys, no horizontal scroll at 1366 and 390,
no English UI text left (only data: names, template bodies, Meta reasons, plan/ledger descriptions, health raw values, the
language picker). Same pages with the client switched to `general`: جهات اتصال / خدمة / خدمات read correctly. Live API in
Arabic: duplicate service 400 «يوجد بالفعل إجراء باسم 'Rhinoplasty'.», seat limit 422 «وصلت إلى حد باقتك لـ«المقاعد» (3)…»,
short password 400 (three Arabic Identity messages). Console: only my own 400/404/422 probes, Facebook's http warning and
SignalR reconnects from app restarts; error log only the usual https-port warnings.

**For the reviewer / S7:**
- Agreement words that fit people but not «جهات اتصال» were rewritten without the term («تواصلوا منذ مدة ولم يحجزوا قط
  ({contacts})»); the S4 note about indefinite idafa («حسب خدمة», «مصدر جهة اتصال») applies here too.
- Contact source values (Referral, Website…) and service names on the campaign filters are stored data, shown as stored.
- `billing.eventType.whatsappUtilityMessage` keeps "(utility)" so admins can match Meta's category name; reviewer may drop it.

**For S6 (French):** the new `billing.plan.perMonth/perYear` already have French; everything else per the `fr:` line.

## S6 · French strings (2026-10-21)

Main app, branch `refactor/generic-platform`, on top of `328fac0`. Committed `8099234`, not pushed. Portal untouched.
Ran on Opus (plan said Sonnet, medium): same work, higher cost.

**Result:** build 0 errors / the same 8 old test warnings; full `dotnet test` **2,084 passed**, 0 failed, 0 skipped (throwaway
Postgres, port 55433). Next session's floor: 2,084. All guards green. `Guards/i18n-incomplete.txt` now lists no language (only
its comment): the ratchet is empty, so every new key needs English, Arabic and French.

**Done (per the plan):**
- `fr.json`: 1,589 keys (1,564 new drafts), *vous*, glossary words. Typography: U+00A0 before `: ; ! ?` and inside « »,
  typographic apostrophe, "Ko/Mo", "{0} %"; French date order came from S1. 77 keys added to `i18n-same-as-english.txt`
  (term-only labels like `{Contacts}`, `{0} {contact}` counts, `{0} / {1}`, Source, Actions, Marketing, Spam, FAQ, Webhook…).
- Business-type words: a translation may use only the placeholder forms its English has, so where English capitalizes the
  word French puts it first with a colon («{Booking} : règles»), and «Pas de {contacts}» replaces «Aucun {contact}».
  `{booking}`, `{order}`, `{client}` are feminine and `{contact}`, `{member}` masculine in both built-in types, so participles
  agree with them; `{service}` (une intervention / un service) only after a colon or in parentheses. Rules in the glossary.
- New guard `UiTextKeysTests.French_texts_use_no_break_spaces_and_typographic_apostrophes`; new test
  `ServerTextTests.Keyed_server_errors_read_in_french_with_the_clients_french_words` (duplicate service, entitlement refusal,
  campaign status, 0/1/2 plural, Identity password error). `TextTests` no longer expects `nav.inbox` to be missing in French.
- Fixes found on the page walk: Dashboard funnel rates were "100%" in every language, now `common.percent` ("100 %");
  status badges' `text-transform: capitalize` made French read «À Vérifier Par L’équipe», now only English keeps Title Case
  (`html:not([lang='en']) .badge`).
- Docs: system-design §3 UI text bullet + change-log row; glossary French rules.
- **Reviewer sheet:** `docs/i18n-review-fr.csv` in the workspace root (not in git; 1,589 rows, UTF-8 with BOM). Hand it to the
  French reviewer (plan D6).

**Checks:** scratch DB (schema + seeds, `real[]` swaps; S6 didn't touch SQL), published app in Staging, demo user in French.
26 pages (every dashboard page, details and edit pages, 404) at 390 and 1366: 200, `lang=fr`, no horizontal scroll, no raw keys,
no clipped buttons, labels or badges. Leftover-English scan of the same pages: only data (contact messages, sources, service
names, KB titles, Meta event texts, "Google Calendar", the language picker's own names). Client switched to `general`:
contacts / réservations / entreprise read correctly, still no scroll at 390. Sign-in and register pages in French for a visitor
(Accept-Language). Console: only SignalR reconnects from my app restarts; no server failures in the log.

**For the reviewer / S7:**
- Labels built as «{Client} : …» / «Nom : {Client}» / «Détails : Entreprise» are correct but stiff; a definite-article form
  per term (the Arabic note from S4) would let French write «Nom de l’entreprise».
- A portal-made type whose words have other genders than the built-ins reads slightly off where a participle agrees.
- Light/dark: theme changes colours only, not checked page by page in French.

## S7 · Language review fixes and final verification (2026-10-21)

Main app and portal, branch `refactor/generic-platform`, on top of `8099234` (main app) and `3949b5a` (portal). Committed
`80207cb` (main app) and `7c7b38e` (portal). Nothing new on
`main` in either repo, so no merge. Not pushed. Mohammad decided there are no external reviewers: this session was the review.

**Result:** main app build 0 errors / the same 8 old test warnings; full `dotnet test` **2,084 passed**, 0 failed, 0 skipped
(throwaway Postgres, port 55433); all guards green. Portal build 0 warnings, `dotnet test` **80 passed**, leftover-names guard OK.
`test-copy.ps1` PASS (40 assertions, forced re-run, sign-ins). Schema + seed + seed-config + seed-demo applied twice ✓.
`Guards/i18n-incomplete.txt` stays empty.

**Review (Arabic = neutral Modern Standard Arabic, French = standard international French):** read every key in English,
Arabic and French side by side, then re-checked the changed strings on the pages. **132 Arabic and 67 French corrections by
key**, each with old text, new text and reason in `docs/i18n-review-changes.csv` (workspace root, not in git; UTF-8 with BOM).
Reviewer sheets `docs/i18n-review-ar.csv` / `-fr.csv` regenerated from the corrected files.
- Arabic: «» around quoted names and values (was '…'); «متصل / غير متصل» for connected (was «مربوط», colloquial); every count or
  length limit that a setting or code fills is written as a label («يجب ألا يتجاوز عدد أحرف الاسم {0}», «الحد الأقصى لعدد
  المواقع: {0}», «مدة كل موعد (بالدقائق): {0}») so no number has to agree with a noun; calques and colloquial phrases
  («وانتهى الأمر», «كل الوقت», «جديد في Omni؟», «رقِّ باقتك», «الآن {0}») replaced; gender clashes with «جهة اتصال» and indefinite
  «{client}» phrases («الخاص بـ{client}») rewritten; «إعادة التنشيط» used consistently for the reactivation audience.
- French: agreement with the built-in genders (`{booking}`, `{order}`, `{client}` feminine; `{contact}`, `{member}` masculine), so
  many parentheses and colon labels became natural («Sa {booking} est planifiée», «le nom de la {client}», «Changer de {contact}»);
  counts that read «1 canaux» / «1 sélectionnés» / «1 non lus» became labels; service names (unknown gender) get neutral
  wording («n’est plus disponible»); the booking-rules summary no longer says «de immédiatement à …» when the notice is 0;
  «Politiques» for policy (was «Règlement»); gender-neutral wording where a person was assumed male or female.
- Glossary: «متصل / غير متصل»; new rules for Arabic quotes and counts, French `{client}` gender and service names.

**Also fixed:**
- Demo seed: the booking-information KB entry had category `consultation` (old name) and showed raw English in Arabic and
  French; now `booking`, as `copy-from-old.sql` maps it.
- Portal: P2's tests tripped the portal's leftover-names guard (old business-type code and words as test data); two tests now use
  `general`, the business-type fixture lines carry `leftover-ok:`.

**Checks:** published app in Staging on a scratch DB, demo client: 26 pages (every dashboard page, details and edit pages, 404)
in en, ar and fr at 390 and 1366: 200 (404 page 404), right `lang`/`dir`, no horizontal scroll, no raw keys, no script errors;
Arabic pages show Latin text only in data (names, services, template names, Meta reasons). Changed strings re-read in place
(dashboard funnel, integrations summary, members, billing, WhatsApp health, KB, booking-rules summary with and without a gap and
with no notice). Portal (published, Staging) against the local main app: all 28 pages 200, business types show "complete"
translations; P2 live check done: client default language set to French in the portal → the main app's Business info opens in
French with French selected. P1's before/after screenshots not redone: the portal's `aurora.css` is the main app's, whose
left-to-right rendering S2 measured as identical.

**Docs:** `deploy-runbook.md` (A and B, language settings, `update_contact` credential, three-language staging checks incl.
Arabizi and emoji, client default languages), system-design (UI text bullet, change-log row), glossary, `configuration.md`
(Languages row), README (languages).

**Deferred / for later:**
- A definite form per business-type word (`{the_client}` / «la clinique») would remove the last stiff labels («معلومات {Client}»,
  «Infos : {Client}»); a design change, not a string fix.
- A portal-made business type whose words have other genders than the built-ins can read slightly off in French where a word
  agrees (rule in the glossary).
