# Milestone B summary: English, Arabic and French dashboard

Branch `refactor/generic-platform` in both repos, not pushed. It ships in the same deploy as Milestone A, following
`SculptFlowApp/docs/deploy-runbook.md`.

## What changed

- **Three languages.** Every dashboard screen, server message and notification exists in English, Arabic
  (Modern Standard Arabic, right to left) and French. Each person picks a language in the account menu; otherwise the
  client's default applies (set by the owner on Business info, or by you in the portal). Visitors pick on the sign-in pages.
- **Right to left.** Arabic pages mirror: sidebar, menus, calendar, inbox bubbles, toasts and arrows. Phones, emails and
  codes stay left to right; names and messages follow their own script. Arabic uses Noto Sans Arabic.
- **Dates and numbers** follow the language (Gregorian months and Latin digits in Arabic, `1 234,5` and day-first dates in
  French); form values stay machine-readable.
- **Business-type words** (patient / مريض / patient …) and stage labels exist in all three languages for the built-in types.
  The portal flags a type that is missing Arabic or French words.
- **AI language.** Each contact has a stored language (two-letter code, shown and editable on the contact page). The AI
  replies in the language of the contact's latest message, falls back to the stored language (emoji, photo), then to the
  business default, and updates the stored language through a new `update_contact` tool.
- **Portal.** Client default language, each member's language, contact language, and the missing-translation badges.
- **Review.** There were no external reviewers; S7 reviewed every Arabic and French text and made 132 Arabic and 67
  French corrections. Each one, with its old text, new text and reason, is in `docs/i18n-review-changes.csv` (workspace
  root). The fixed vocabulary is `SculptFlowApp/docs/i18n/glossary.md`.

Commits: main app `b05fe92` (S1), `a562596` (S2), `09ded40` (S3), `e763835` (S4), `328fac0` (S5), `8099234` (S6), `80207cb` (S7);
portal `f639abb` (P1), `3949b5a` (P2), `7c7b38e` (S7). Tests: 2,084 main app, 80 portal, all passing.

## Settings (database, created by `seed-config.sql`; no environment variables)

| Setting | Default | Meaning |
|---|---|---|
| `Localization:Languages` | `en,ar,fr` | Languages people can pick; English is always on. Remove one to hold it back. |
| `Localization:LanguageCookieDays` | 365 | How long a visitor's choice on the sign-in pages is remembered. |
| `Localization:ShowTextKeys` | false | Review mode (`?textKeys=1` shows each text's key); never works in Production. |

## What you do at go-live (plan §7, now in the runbook)

1. Deploy A and B together with the runbook.
2. Keep `Localization:Languages` at `en,ar,fr` (portal Configuration page) and `ShowTextKeys` off.
3. Import `docs/n8n/omni-ai-assistant.json` in n8n and pick the "Omni ingest key" credential on every HTTP node, including the
   new `update_contact` tool.
4. On staging, run a conversation in English, Arabic (script and Arabizi) and French: ask, book, reschedule, cancel,
   hand-off, then an emoji-only message. Replies should follow the latest message's language and the contact page should
   show the stored language.
5. In the portal, set the default language of any client that should open in Arabic or French.

## Known limits

- Some labels stay a little stiff because the business-type words come without an article («معلومات {Client}»,
  «Infos : Clinique»). A definite form per word would fix it; it is a design change, not a text fix.
- A business type made in the portal whose words have other genders than the built-in ones can read slightly off in French
  where a word agrees with it.
- Texts written outside a request (a campaign recipient's failure reason, the AI's booking refusals) stay in English, as
  planned.
