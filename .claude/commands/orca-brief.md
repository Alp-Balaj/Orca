---
description: Turn a vault audit note (BF-xx / SUG-xx) into a ready-to-run Orca kickoff brief
argument-hint: BF-03 [optional scope notes]
---

Produce a ready-to-run Orca kickoff brief from an audit note. Target: $ARGUMENTS

The first token is the note id (`BF-xx` or `SUG-xx`); anything after it is extra scope
guidance from the user and overrides this file where they conflict.

## Locate

- Vault base: read `vault.name` from this repo's `orca.json`, then resolve it through the
  `vaults` map in Orca's global config (`~/.config/orca/config.json` on Linux,
  `%APPDATA%\orca\config.json` on Windows). Fallback if either is missing: glob for
  `*/7 - Dev Folder/1. Projects/Orca` under the user's Documents folder; ask if not found.
- Audit folder: `<vault base>/7 - Dev Folder/1. Projects/Orca/` — notes live in
  `Bug Fixes/` and `Suggestions/`, kickoffs and `index.md` at the top level.
- Read the target note in full, plus the two exemplar kickoffs to match their shape:
  `2026-07-16 - Kickoff - Attempt Truth (BF-01).md` and
  `2026-07-16 - Kickoff - Prompts off argv (BF-02).md`.

## Re-verify — the load-bearing step

The audit is a snapshot (2026-07-15) and drifts as fixes land. Never copy a claim into
the brief unverified:

- Check every `file:line` and behavioral claim against the current code. Cite **symbols,
  not line numbers** in the brief (line numbers drift; the BF-01 plan flagged exactly this).
- When a claim is cheaply testable, test it empirically in the scratchpad (e.g. BF-02's
  "git lists FIFOs" claim died to a 10-second `mkfifo` + `git status` spike).
- Where the note is stale, add a dated `> [!note] Corrected <date>` callout to the note
  itself and carry the correction into the brief so the Planner doesn't chase ghosts.
- Run `dotnet build Orca.slnx` and `dotnet test Orca.slnx` for the live test count; the
  CONSTRAINTS block must state it.

## Write the kickoff

File: `<audit folder>/YYYY-MM-DD - Kickoff - <Short name> (<ID>).md` (today's date).

Frontmatter: `project: Orca`, `type: kickoff`, `parent: "[[2026-07-15 - Orca Deep Analysis]]"`,
`plan-id: <slug>`, `status: ready`, `created: <today>`. Name the plan-id for the **change**,
not the bug (`attempt-truth`, `prompts-off-argv` — not `bf-01`).

Intro (outside the fence): what it fixes, why now, which audit claims were corrected, and
the two usage modes with the exact command:
`orca plan-generate <plan-id> --goal-file <this file> --repo ~/Projects/Orca/Orca`.

Then one fenced code block — the goal brief the repo-blind Planner consumes. It must be
self-contained: every file pointer, symbol, and current fact rides inside. House sections:

1. Opening line: what to change, then
   `Repo: the Orca codebase itself (src/Orca.Core, tests/Orca.Tests; xUnit;
   warnings-as-errors; net10.0; <N> tests green).`
2. `THE BUG.` — mechanism with verified symbols, the failure scenario, what it costs
   (billing, wedged runs). Multiple legs get `LEG 1 / LEG 2` labels.
3. `WANTED OUTCOME.` — numbered, behavioral, each independently checkable.
4. `CHECKPOINT SIZING.` — 2–3 checkpoints. The pattern that has worked twice: checkpoint
   01 isolates the pure/risky core with exhaustive unit tests; checkpoint 02 wires it into
   control flow with behavior tests over the existing fixtures (`TempGitRepo`, the fake-CLI
   replay scripts in `ClaudeCodeExecutorTests`, `[ClaudeCliFact]` for opt-in billed
   contract tests). Name the existing test files whose style to follow.
5. `CONSTRAINTS.` — explicit out-of-bounds list (layers not to touch, with one-line
   rationale), append-only/schema rules, gates green with the live test count, and always:
   every acceptance criterion must be checkable from the diff plus green gates alone.

## Criteria quality bar

The Verifier enforces only what the criteria say — write them so a lazy diff cannot pass:

- Behavioral, not structural: "asserts Attempt == 2, not 1", never "a function exists".
- Pin exact message/marker texts wherever silent swallowing could hide
  ("(truncated, N bytes omitted — see git)", "there is no override for this check").
- Assert absences too: what must NOT appear (prompt absent from argv, no "delete the
  branch" advice, no new schema fields).
- Say what tests cannot prove (real-CLI behavior, cross-machine sync) and how it gets
  pinned instead: opt-in contract test, or "the next dogfood run is the live proof".

## Hygiene, then report

- Add the kickoff to `index.md`'s kickoff list (one line, status + why-next).
- Add `Ready-to-run brief: [[<kickoff>]]` at the bottom of the source note; keep its
  `status:` accurate.
- Report back: the exact `orca plan-generate` command, scope decisions (what was cut from
  the note's fix bundle and why), corrections found during re-verification, and 2–3
  specific places to probe the generated plan adversarially before approving it.
