# MySculptFlow workspace — shared rules for AI assistants

Three separate git repos live here; sessions often start from this root. Each repo has its own `CLAUDE.md` (architecture and
standing rules). This file holds only what applies to all of them.

| Project | For |
|---|---|
| `SculptFlowApp` | Main app: clinic dashboard, APIs, DB, integrations, `/api/platform-admin` endpoints |
| `SculptFlowAdmin` | Platform-admin portal (calls the main app's platform-admin APIs; no main-table access) |
| `SculptFlowAutomation` | Automation tester (throwaway `[Automation]` clinic per run) |

Work out which project a task belongs to before exploring, and read that project's `CLAUDE.md` first. Don't look at sibling
projects unless the task is cross-project (e.g. a new portal feature = main-app endpoint + portal client/service/page).

## Always

- Never `git commit` or push unless Mohammad says so. Never stage `launchSettings.json` (real keys).
- Don't read `SculptFlowApp/PROJECT_HANDOFF.md` or the `omni-*`/`new-omni-*` HTML files here unless Mohammad asks.
- Lead with the answer. Short progress updates and final summaries; no restating the task.
- Quality is not negotiable: complete implementations, error handling, edge cases and tests. Save tokens by cutting waste
  (re-reads, broad exploration, verbose output), never by skipping validation.

## Context efficiency

- Find with `Grep`/`Glob`, then `Read` with `offset`/`limit`. Don't read whole large files (`system-design.html`,
  `schema.sql`, big services) or whole directories.
- Don't re-read unchanged files already in context, or a file you just edited. Batch independent tool calls.
- Reuse existing helpers, patterns and docs; edit in place instead of rewriting files; no unrelated refactors.
- `bin/`, `obj/`, `node_modules/`, `.vs/`, logs and generated output are blocked in `.claude/settings.json`; don't work around it.
- Subagents (`Explore`) only for broad sweeps across many files; do small lookups yourself.
- If an approach fails twice, change the approach instead of repeating the investigation.
- Long task: `/compact` at a natural break with a focused summary. New topic or long context: start a new session.

## Build and test

- Run the targeted tests for the changed area first (`dotnet test --filter ...`), then the broader suite before finishing.
- Send verbose build/test output to a file (scratchpad/temp) and read only failures and the summary line.
- Don't re-run a green build or test without a code change since.

## Models and effort

I can't switch models or measure usage on my own; recommend the model before an expensive task and let Mohammad switch.

- Opus: complex features, major architecture changes, hard debugging, complex business logic, important technical decisions.
  Prefer Opus to plan (brief plan, no long ones for simple tasks), then Sonnet to implement.
- Sonnet (default): small/medium features, UI redesigns, ordinary bug fixes, refactoring, unit tests, routine work.
- Haiku: simple investigations and lightweight tasks.
- Effort: low for straightforward tasks, medium by default, high only for genuinely hard problems.
- If a task looks likely to be large or expensive, say so up front and suggest a cheaper way to run it (split it, plan on Opus,
  implement on Sonnet, new session).

## Conventions that apply everywhere

- Time: UTC in the DB; activity shown in viewer time; appointments in clinic time; availability clinic-local.
- Platform-admin API changes: main app first (`/api/platform-admin/{domain}`), then the portal's client/service/page and its
  copied shapes (see `SculptFlowAdmin/MAIN_APP_SYNC.md`).
