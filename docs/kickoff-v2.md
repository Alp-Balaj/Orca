> **Provenance:** Implementation kickoff spec (v2), delivered 2026-07-06; amended the same
> day by owner decision after the step-4 auth findings and the subscription-auth review:
> all four roles (Planner, Implementer, Reconciler, Verifier) authenticate via the
> machine's interactive `claude` subscription login and run through `ClaudeCodeExecutor`
> (`--safe-mode`, not `--bare`; no `ANTHROPIC_API_KEY` anywhere), and structured output for
> Planner/Reconciler/Verifier is a vault-file contract (§5.4) replacing the former
> Messages-API forced-tool-use design. Safety layer 2 (§7) was re-based the same day, after
> the step-5 probes, from `PreToolUse` deny hooks (suppressed by safe mode) onto `--settings`
> `permissions.deny` rules, which survive safe mode and override the allowlist. The deviation log and open items live in the vault
> plan (`orca-build`). The product name was finalized as **Orca** at session 1 (owner
> approval of the kickoff proposal); this text retains the working name "orkestra"
> throughout. Mapping: tool command `orca`, per-repo config `orca.json`, gitignored state
> dir `.orca/`, branch prefix `orca/{plan-id}/{checkpoint-id}`. Approved schemas live in
> code (`src/Orca.Core/Domain`, `src/Orca.Core/Vault`); this document is the architectural
> source of truth, kept in sync with the vault copy.

# Orkestra — Implementation Kickoff (v2)

You are implementing **orkestra**, a .NET CLI tool that orchestrates Claude Code as a
disciplined, checkpoint-gated software-development agent, using an Obsidian vault as a
structured, stateless handoff layer between planning and implementation. This document is
the complete spec for the MVP. Read it fully before writing any code. Where something is
ambiguous, stop and ask rather than guessing — this project is explicitly optimizing for
control and inspectability over autonomy, so silent assumptions are the failure mode we're
avoiding.

## 0. What orkestra is, and isn't

Orkestra is a **workflow**, not an autonomous agent. A human (the owner) reviews and
approves at every checkpoint. The only agentic component is Claude Code itself, running
inside a tightly scoped sandbox for the duration of a single checkpoint. Everything above
that — planning, sequencing, reconciliation, verification, gating — is deterministic C#
code plus prompted `claude -p` invocations that write typed, schema-validated output files
into the vault. Do not build anything
that runs unattended across multiple checkpoints without human sign-off in between.

The defensible, novel part of this project is **the vault schema, the checkpoint gating,
and the verification discipline** — not a custom reasoning loop. Claude Code is rented as
the executor; do not reimplement ReAct, tool-calling loops, or context management.

## 0.5 Session protocol — read this before anything else

This build is itself checkpoint-gated. Do not attempt the full build order (§9) in one
pass.

**Your first response, before writing any code:**

1. Confirm your understanding of the architecture in one short paragraph — specifically
   the statelessness rule (§8) and the one-invocation-per-checkpoint rule (§6).
2. Propose the concrete plan-file frontmatter schema (§5.1) and the `orkestra.json` schema
   (§3) as YAML/JSON examples with field names and types. These are public contracts and
   therefore architecturally significant — they get explicit approval before any code
   depends on them.
3. Raise anything in the "owner decisions pending" list (§11) that blocks step 1–3 work,
   plus anything else in this document you'd push back on.

**This session's scope:** build-order steps 1–3 only (scaffold, domain model + parser,
vault I/O). Stop there and present the result for review, even if momentum suggests
continuing. The executor (step 4) starts in the next session against an approved schema.

**Session end condition:** all step 1–3 code compiling, parser unit tests green (including
the malformed-fixture fail-loud cases), and a short summary of any deviations from this
spec with reasons.

## 1. Environment

- **OS:** Arch Linux (Omarchy/Hyprland). Known issue: Arch's dotnet packaging has
  previously required a `Directory.Build.props` workaround for the missing
  `PrunePackageData` target. The owner has this workaround from a prior project
  (RecipeApp) — ask for it if the build hits that error rather than debugging from
  scratch.
- **SDK:** .NET 10.
- **Claude Code CLI:** installed and available as `claude`. All four roles (Planner,
  Implementer, Reconciler, Verifier) authenticate via the machine's interactive `claude`
  subscription login — no `ANTHROPIC_API_KEY` is required anywhere in this tool. Pin and
  record the installed Claude Code version in the repo README — the CLI surface moves
  fast.
- The executor integration test (§6) requires the real `claude` binary, logged in on the
  machine running it. Mark it with an explicit xUnit trait (e.g.
  `[Trait("Category", "Integration")]`) so default `dotnet test` runs exclude it and it is
  run deliberately.

## 2. Repository structure

Three projects, flat, no more:

```
orkestra/
  Directory.Build.props
  Directory.Packages.props
  Orkestra.sln
  .gitignore               # bin/, obj/, .orkestra/
  src/
    Orkestra.Core/         # domain types, vault I/O, executor wrapper, planning strategies, Anthropic client
    Orkestra.Cli/          # entry point, command parsing, terminal UX — thin, delegates to Core
  tests/
    Orkestra.Tests/
  prompts/                 # one markdown file per agent role (Planner, Reconciler, Verifier), versioned
```

- `Orkestra.Cli` references `Orkestra.Core` only. It contains no business logic — just
  command wiring, console output formatting, and confirmation prompts.
- `Orkestra.Tests` references `Orkestra.Core`.
- Use **central package management** (`Directory.Packages.props`) from the first commit.
- Set `<PackAsTool>true</PackAsTool>` on `Orkestra.Cli` with `ToolCommandName = orkestra`
  immediately — this forces correct handling of working directory, config discovery, and
  path resolution from day one instead of being retrofitted later.
- Target `net10.0`, nullable enabled, implicit usings enabled.
- Do not create additional projects (no `Orkestra.ReAct`, no `Orkestra.TreeOfThought`, no
  separate `Orkestra.Anthropic`). If a genuine second consumer or substitution need emerges
  later, extract then — not preemptively.

**Locked library choices** (see §11 for the ones still pending owner confirmation):

- **Testing:** xUnit.
- **YAML:** YamlDotNet (frontmatter only — the markdown body is passed through opaque).
- **No Anthropic API client.** All four roles (Planner, Implementer, Reconciler,
  Verifier) go through the same `ClaudeCodeExecutor` process wrapper (§6) — a scoped
  `claude -p` invocation differing only in prompt, tool allowlist, and expected output
  file. There is no Messages API integration anywhere in this codebase.
- **CLI parsing + terminal UX:** Spectre.Console + Spectre.Console.Cli (default, pending
  §11 confirmation). Orkestra's UX is dominated by confirmation prompts, diff summaries,
  and status tables — Spectre's prompt/render primitives fit that better than
  System.CommandLine, and since Cli is a thin shell over Core, swapping later is cheap.

## 3. Three filesystem roots — never conflate them

Orkestra touches three distinct roots. All three must be configurable, never hardcoded:

1. **The orkestra repo itself** (this codebase).
2. **The target repo** — the project being worked on (RecipeApp for the MVP validation run).
3. **The vault** — the Obsidian vault holding plan files, decisions, and verification
   records.

Configuration lives in an `orkestra.json` file at the target repo's root (per-project
settings: vault plan directory, build command, test command, an allowed-tools list **per
role** — Planner/Reconciler/Verifier get vault-write-only tools, Implementer gets its
per-checkpoint allowlist (§7) — and a model ID per role) plus global defaults in the
standard XDG/AppData location for the current OS. Model IDs, candidate count N for
multi-candidate planning, `--max-turns`, wall-clock timeout, retry ceilings for the §5.4
parse-retry loop, and cost ceilings all belong in this config — do not hardcode them.
There is no API key field anywhere in this schema (§4). The only things that should be
hardcoded are the plan-file schema itself and the stream-json event contract (§6), and
the latter only via the contract test.

`orkestra.json` is committed in the target repo. `.orkestra/` (run logs, run state) is
gitignored in the target repo.

## 4. Secrets

There are none. All four roles authenticate via the machine's interactive `claude` login
(subscription auth) — no `ANTHROPIC_API_KEY`, and no other credential, is read from or
written to any file in either repo. A logged-out machine fails loudly via the CLI's own
`authentication_failed` event; do not add a pre-flight credential check or a config-file
secret to work around that. This closes off the JWT-key-in-appsettings failure mode by
construction rather than by discipline.

## 5. Domain model and the plan-file schema

This is the first thing to build — everything else depends on this shape existing.

### 5.1 Plan file format

Plan files live in the vault under a `Plans/` directory. Each plan file is markdown with
**YAML frontmatter** as the machine-readable spine, parsed with YamlDotNet. The markdown
body below the frontmatter is free-form prose for the human. Parse frontmatter **strictly**
— fail loudly (throw, don't warn-and-continue) on schema violations. A half-parsed plan
silently executed is exactly the drift this tool exists to prevent.

Propose the concrete schema in your first response (§0.5), but it must at minimum express:

- **`schema_version`** — version of the frontmatter schema itself, distinct from the
  plan's own version, so future schema migrations are possible without archaeology.
- Plan identity and version (so re-runs and comparisons are attributable)
- Status (e.g. draft, approved, in-progress, blocked, complete)
- An ordered list of checkpoints, each with: an id, a short description, acceptance
  criteria (a list, not a paragraph), and a status
- The planning strategy used to produce this plan (single-candidate vs multi-candidate)
  and, if multi-candidate, which candidate was selected and why
- A link/reference to the target repo and the branch this plan is executing against

### 5.2 Child step notes

Each checkpoint may have a child note (`NN-[checkpoint-slug].md` convention) containing the
detailed instructions for that specific checkpoint — this is what actually gets assembled
into the Implementer's prompt. Keep the parent plan file as the index/spine; checkpoint
detail lives in children so a single checkpoint's prompt doesn't require loading the entire
plan's prose.

### 5.3 Run state and run logs

Define a run-state record capturing what's needed to resume a run cold: plan id + version,
current checkpoint index, Claude Code `session_id` (for within-checkpoint `--resume` only,
per §8), branch name, prompt-file hashes in effect, accumulated cost, and timestamps. This
is the resumability contract — conceptually LangGraph's interrupt/resume model (persist
state, allow a human to resume later), implemented as a plain JSON record, not a graph
library.

Run logs (the full NDJSON transcript per checkpoint invocation) are **ephemeral evidence,
not durable knowledge** — they go in the gitignored `.orkestra/runs/` directory in the
target repo. The vault holds plans, decisions, and verification results only. Do not let
transcripts leak into the vault; that turns it into a log dump instead of durable
knowledge.

### 5.4 Structured output for agent roles (vault file, not forced tool use)

Every agent role (Planner, Reconciler, Verifier) is a `claude -p` invocation, not a
Messages API call, so there is no `tool_choice` to force schema conformance. Structured
output is instead a **file contract**: each role's prompt instructs the model to write its
result as a file into the vault in a fixed, documented schema, and nothing else outside
that file. The stream-json transcript is captured as evidence exactly as it is for the
Implementer (§6) but is never parsed for the result — only the vault file is, and only
after the process exits.

- **Planner** writes a plan file: YAML frontmatter per §5.1, plus free-form prose body.
- **Reconciler** writes a small fixed YAML/JSON verdict block (implements-plan: yes/no/
  partial, missing items, extra items) into the vault, adjacent to the plan per the vault
  I/O conventions already used for verification records.
- **Verifier** writes a small fixed YAML/JSON block (per-criterion pass/fail plus notes)
  into the verification record.

Each schema gets its own C# parser/validator, held to the same strict, fail-loud
discipline as the plan-frontmatter parser (§5.1) — throw on schema violations, never
warn-and-continue.

**On parse or validation failure, do not fail the checkpoint hard.** Feed the parse error
back into a retry prompt for that same role, reusing the Reflexion-style retry pattern
already specified for Implementer verification failures (§8): "your last output did not
parse — here is the exact error — try again, writing only the file in the documented
schema, nothing else." Cap retries (configurable, same ceiling concept as `--max-turns`)
and, if exhausted, surface a clear, specific error to the human rather than silently
accepting a malformed file or guessing at its content.

## 6. `ClaudeCodeExecutor` — the invocation wrapper for all four roles

All four roles (Planner, Implementer, Reconciler, Verifier) are scoped `claude -p`
invocations through this same process wrapper, differing only in prompt, tool allowlist,
and expected output file — one process spawn, one parsed output file, one retry policy.
What follows describes the mechanism in full through the Implementer's use of it,
including the checkpoint/branch/git specifics (§7) that are unique to the Implementer;
Planner, Reconciler, and Verifier share the spawn/stream/log/retry mechanics below but
write a vault file per §5.4 instead of code, and carry none of the git/checkpoint
mechanics.

This is a process wrapper, not a reasoning loop. Responsibilities:

- Shell out to the `claude` CLI with `-p`/`--print` for non-interactive execution.
- Always pass `--safe-mode` so only explicitly-passed flags take effect — no ambient
  hooks/skills/plugins/MCP/memory/CLAUDE.md discovery. Orkestra supplies all context itself
  via the assembled checkpoint prompt; ambient config is noise and breaks reproducibility
  across machines. (Not `--bare`: bare mode never reads the OAuth/keychain state that
  subscription login depends on, so it cannot authenticate any of the four roles now that
  none of them use an API key — see §1/§4.)
- Always pass `--output-format stream-json`. Read stdout **line-by-line**, parse each
  NDJSON event with `System.Text.Json`, and append every event to the run log as it
  arrives (don't buffer and parse at the end — the point is a durable, resumable,
  inspectable transcript).
- **Derive the event contract empirically, do not trust this document for it.** Before
  writing the parser, run one trivial invocation (e.g. `claude --safe-mode -p "read this file
  and summarize it" --allowedTools "Read" --output-format stream-json`) and capture the
  transcript. Expect top-level event types along the lines of `system` (init, carries
  `session_id`), `assistant` (whose content blocks may include `tool_use`), `user` (tool
  results), and `result` (final, carries usage/cost) — but codify whatever you actually
  observe, in the contract test. Also verify whether the current CLI requires `--verbose`
  alongside `stream-json` in `-p` mode; historically it has.
- Persist the `session_id` from the init event into run state. Extract usage and cost from
  the final `result` event into run state and the run log — the Phase 4 with/without-tool
  comparison depends on cost being captured per checkpoint.
- Scope tool access per checkpoint via `--allowedTools`, sourced from `orkestra.json`. Get
  the allowlist syntax exactly right: `Bash(git diff *)` (space before `*`) matches
  commands *starting with* `git diff`; `Bash(git diff*)` (no space) also matches
  `git diff-index`. Do not assume these are equivalent.
- Enforce a `--max-turns` ceiling and a wall-clock process timeout as a backstop against a
  runaway checkpoint, both configurable.
- One `claude -p` invocation per checkpoint. Do not build an interactive/looping wrapper
  that spans multiple checkpoints — checkpoint granularity is a hard architectural
  boundary, not a convenience.
- Authentication: the machine's interactive `claude` login (subscription) must be valid
  for the child process; there is no `ANTHROPIC_API_KEY` fallback or pre-flight check —
  a logged-out machine fails loudly via the observed `authentication_failed` event.
- Write the integration test (trait-gated, §1) asserting the observed stream-json event
  shapes. Pin the Claude Code version you develop against; this test's job is to fail
  loudly on a CLI breaking change rather than letting the parser silently misparse.

## 7. Safety layers around the Implementer

Three layers, all required, none optional:

1. **Allowlist** (`--allowedTools`) — destructive commands (`dotnet ef database update`,
   `git push`, `rm`, etc.) are simply never in the allowlist for a checkpoint. Per §7.1,
   no git *write* commands of any kind are in the MVP allowlist.
2. **Deny-list permission rules** — pass `--settings` JSON carrying `permissions.deny`
   patterns (e.g. `Bash(git push*)`) for destructive commands; sourced from `orca.json`'s
   `deniedTools` (union of global and repo lists, so a repo can add rules but never remove
   machine-level ones). *Amended 2026-07-06 (step-5 probes, transcript fixtures in the
   repo):* the original mechanism here — `PreToolUse` hooks with
   `permissionDecision: "deny"` — is dead under `--safe-mode`, which suppresses even
   explicitly-passed `--settings` hooks on 2.1.201. `permissions.deny` rules in the same
   payload are honored and **win over `--allowedTools`**, so this layer still fails closed
   when the allowlist is misconfigured.
3. **Git as the sandbox** — every checkpoint runs on an isolated branch in the target
   repo. The human's approval step *is* reviewing the diff before merge. This is the layer
   to actually lean on hardest: even if layers 1 and 2 leak, nothing reaches the real
   branch without a human looking at it first.

Do not attempt per-tool-call interactive approval (recreates interactive Claude Code with
extra steps) or per-feature review (too coarse, reintroduces the autonomous sprawl this
tool exists to prevent). Per-checkpoint is the only granularity to build for the MVP.

### 7.1 Git mechanics — who does what (default policy, pending §11 confirmation)

- **Orkestra** (deterministic C#, not the agent) creates the checkpoint branch
  (`orkestra/{plan-id}/{checkpoint-id}` naming) from the plan's base branch before
  invoking the executor, and verifies the working tree is clean first — refuse to launch
  a checkpoint onto a dirty tree.
- **Claude Code** gets no git write commands: no `git add`, `git commit`, no branch
  manipulation. Read-only git (`git diff`, `git log`, `git status`) may be allowlisted.
- **On human approval**, orkestra stages and commits with a structured message referencing
  plan id + checkpoint id, so target-repo history is greppable back to plans.
- **On rejection**, orkestra offers to hard-reset the checkpoint branch (this is orkestra
  acting on explicit human instruction, so it does not violate the no-destructive-actions
  rule — but it must always be a confirmed choice, never automatic).
- **Branch, not worktree, for the MVP.** Worktrees earn their complexity only if
  checkpoints ever run in parallel; they don't in this design.

## 8. Agent roles and the vault-as-handoff pattern

**Every agent call is stateless.** No agent passes context to the next agent through
conversation state or transcript chaining. The Planner writes `plan.md` (and any
`decisions.md`); the Implementer's prompt is assembled fresh, from those files, every time.
No hand-summarizing, no context rot by construction. Treat this as the single most
important architectural rule in the whole system — do not weaken it for convenience
anywhere.

**Scope of `--resume`:** permitted only *within* a single checkpoint — e.g. a continuation
after a timeout, or a retry that benefits from the session's file-read context. Never use
`--resume` across checkpoint boundaries; cross-checkpoint context flows through the vault
only. Fresh session per checkpoint is the default; `--resume` is the exception and must be
an explicit, logged choice.

### Roles to implement

- **Planner** — a `ClaudeCodeExecutor` invocation (§6) scoped to write-into-the-vault
  only, producing the plan file per §5.4. Supports two strategies behind an
  `IPlanningStrategy` interface:
  - `SingleCandidatePlanningStrategy` — one generation call, used for routine work (e.g. a
    new CRUD endpoint following an existing pattern).
  - `MultiCandidatePlanningStrategy` — for architecturally significant decisions only
    (defined as: touches project/layer boundaries, database schema, a public contract
    including the plan-file schema itself, a cross-cutting concern, or is expensive to
    reverse). Generates N genuinely distinct candidates (prefer N parallel calls with
    varied framing over one call asked to produce N variants — this yields more diverse
    candidates), scores them against project-defined axes via a critic call whose verdict
    is written as a structured vault file per §5.4, and optionally runs a synthesis call.

  Whether a given decision gets single- or multi-candidate treatment is a flag the Planner
  surfaces to the human to decide — do not have the Planner decide this autonomously; how
  much deliberation to spend is exactly the kind of meta-decision that should route to a
  checkpoint.

- **Implementer** — a `ClaudeCodeExecutor` invocation (§6) with the full per-checkpoint
  tool allowlist (§7), driven by the assembled checkpoint prompt (plan-file section +
  acceptance criteria for that checkpoint only, nothing else).

- **Reconciler** — a `ClaudeCodeExecutor` invocation scoped to write-into-the-vault only.
  For the MVP, keep this deliberately thin: `git diff` output plus the relevant plan-file
  section plus one prompt asking "does this diff implement what the plan says, and what's
  missing or extra," writing a typed verdict as a structured vault file per §5.4. Do not
  build a static-analysis drift-detection engine — that's a later-version idea, not MVP
  scope.

- **Verifier** — two layers, in order, and do not conflate them:
  1. **Deterministic gates first:** orkestra runs the configured build and test commands
     as plain process calls and records pass/fail and output. No LLM is involved in
     establishing whether the build compiles or the tests pass — never ask a model for a
     fact the machine can state.
  2. **Semantic verification second, only if the gates pass:** a `ClaudeCodeExecutor`
     invocation scoped to write-into-the-vault only, over the diff, the deterministic-gate
     output, and the checkpoint's acceptance criteria, writing a typed per-criterion
     checklist result as a structured vault file per §5.4 (never parsed prose).

  When verification fails at either layer, write the structured findings into the vault
  such that they become part of the *next* Implementer invocation's prompt — this is a
  deliberate Reflexion-style feedback loop (Shinn et al. 2023: verbal self-feedback fed
  into the retry), implemented via the vault as the memory rather than via conversation
  state.

### Prompts as files

Each role's prompt lives in `/prompts/{role}.md` in the orkestra repo, versioned in git.
Record a hash of the prompt file used in every run log entry and in run state, so that
later comparisons ("did the Verifier get stricter, or did prompt v3 land") are
attributable to the right cause.

## 9. Build order

Work in this order; don't jump ahead to the Implementer before the plan-file schema is
solid, since everything downstream depends on it. **Steps 1–3 are this session's scope
(§0.5); each subsequent step is its own session with review in between.**

1. Solution/project scaffold (§2), `Directory.Build.props`, `Directory.Packages.props`,
   `PackAsTool` on the CLI, `.gitignore`.
2. Domain records for the plan-file schema, checkpoint model, run-state model (§5.1–5.3).
   YAML frontmatter parser (YamlDotNet) with strict failure on schema violations. Unit
   tests over the parser using hand-written fixture plan files, including deliberately
   malformed ones to confirm the fail-loud behavior.
3. Vault reader/writer built directly on top of the schema from step 2 — read a plan file,
   read a checkpoint's child note, write verification results back.
4. `ClaudeCodeExecutor` (§6): capture a real transcript first, then process invocation,
   NDJSON stream parsing, run-log writer, cost extraction, `--allowedTools` construction
   from config, the trait-gated event-shape contract test.
5. `orkestra.json` config loading (target-repo + global defaults), the git mechanics from
   §7.1 (branch creation, clean-tree check, commit-on-approval), and the CLI command
   surface for at least: initializing a plan directory in a vault, running a single
   checkpoint, and viewing run status.
6. `IPlanningStrategy` with `SingleCandidatePlanningStrategy` first; defer
   `MultiCandidatePlanningStrategy` until the single-candidate path and the Implementer
   loop are proven end-to-end on a trivial checkpoint in RecipeApp.
7. Reconciler and Verifier roles (deterministic gates + semantic layer), vault-file output
   schemas and their parsers/validators (§5.4), the Verifier-failure → next-prompt feedback
   loop.
8. End-to-end validation: one full trivial checkpoint against RecipeApp, human review of
   the diff, merge.

## 10. Non-goals for this pass

Do not build: a custom ReAct/tool-calling loop, a tree-search (real ToT) planning strategy,
per-tool-call interactive approval, a static-analysis-based Reconciler, SQLite-backed run
storage (JSON files are the confirmed choice), worktree-based isolation, agent-driven git
commits, or anything that executes across multiple checkpoints without a human sign-off
between them. If you find yourself reaching for any of these, stop and flag it rather than
proceeding.

## 11. Owner decisions pending — confirm or override at session start

Defaults are set throughout this document so work can begin immediately; these are the
ones the owner explicitly reserves the right to override, and your first response should
list any that block current-session work:

1. **Spectre.Console.Cli vs System.CommandLine** for the CLI layer (default: Spectre, per
   §2 rationale).
2. **Git policy in §7.1** — specifically agent-has-no-git-write and
   orkestra-commits-on-approval (default: as written).
3. **Branch naming convention** `orkestra/{plan-id}/{checkpoint-id}` (default: as written).
4. **Frontmatter field names/types** — you propose in the first response; owner approves
   before step 2 code depends on them.
5. **`orkestra.json` field names** — same protocol as above.

Do not silently invent answers to architecturally significant questions (per the
definition in §8) — surface them for a decision instead.
