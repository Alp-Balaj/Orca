# Orca

Orca orchestrates Claude Code as a disciplined, checkpoint-gated software-development agent.
An Obsidian vault is the stateless handoff layer: the Planner writes plan files, every
agent invocation is a single scoped `claude -p --safe-mode` process whose prompt is
assembled fresh from the vault, and Reconciler/Verifier results are written back to the
vault where they gate — and feed — the next attempt. A human reviews and approves at every
checkpoint; nothing runs unattended across checkpoint boundaries.

Orca is a workflow, not an autonomous agent. The full spec is `docs/kickoff-v2.md`.

## Pinned tool versions

| Tool | Version | Notes |
| --- | --- | --- |
| Claude Code CLI | **2.1.201** | the stream-json event contract is derived empirically from committed transcript fixtures; a trait-gated contract test fails loudly on CLI breaking changes |
| .NET SDK | 10.0.301 | `net10.0`, nullable + implicit usings, warnings as errors, central package management |
| Terminal.Gui | **2.4.17** | the TUI framework, `Orca.Cli` only (never `Orca.Core`); v2's API and driver model still move between minors, so pin here and upgrade deliberately — the same discipline as the CLI pin, with the shell kept thin so churn touches presentation only |

## Layout

    src/Orca.Core/     domain types, vault I/O, executor wrapper, planning strategies
    src/Orca.Cli/      thin Spectre.Console shell — command wiring only; packs as dotnet tool `orca`
    tests/Orca.Tests/  xUnit; the default `dotnet test` run excludes the billed contract test
    prompts/           one versioned prompt file per agent role (SHA-256 recorded per run)
    docs/              the kickoff spec, amended with the owner's auth decisions

## Build & test

    dotnet build
    dotnet test

The default run excludes the live-CLI contract test via the repo-root `.runsettings`
(wired through `RunSettingsFilePath` — `dotnet test` does not auto-discover it). Run the
contract test deliberately, on a logged-in machine (~$0.20 of subscription quota):

    dotnet test --settings tests/integration.runsettings

Process-mechanics tests (timeout kill, resolver) run by default and skip visibly when no
`claude` binary is on PATH.

## How to use

The manual for driving Orca against a repository. The sections after it (Commands,
Configuration, Auth) are the reference detail. Everything below was exercised for real
in the first end-to-end run (RecipeApp health endpoint, 2026-07-08).

### Interactive TUI

`orca` with no arguments, on an interactive terminal, opens a full-screen TUI — the default
entry point. `orca ui [--repo <path>]` launches it explicitly (for flags or muscle memory).
On a non-interactive terminal bare `orca` prints this help (exit 0) and `orca ui` refuses
cleanly with a one-line message (exit 1) — never a framework init crash; every subcommand
below still works unchanged for scripting.

The TUI is observability over the same workflow, not more autonomy — one session at a time,
a human at every gate. Three parts:

- **Shell** — a tree of the vault's plans → checkpoints (status-coloured), a detail pane for
  the selection, and a status bar (repo · branch + dirty flag · vault · the selected plan's
  accumulated cost). Arrow keys navigate and collapse/expand, `r` refreshes, `q` quits.
- **Live pane** — press `x` to run the selected checkpoint: a preview modal authorizes the
  spend, then all four roles (Planner/Implementer/Reconciler/Verifier) stream live as the
  invocation runs, with scrollback (scroll up to pause tail-follow, End to resume) and an
  honest elided-event count under load — the run log on disk always has everything. Ctrl-C
  interrupts: it kills the invocation and leaves the attempt resumable, nothing re-paid. The
  Approve/Reject/Defer gate is a modal; Reject chains the discard confirmation exactly as the
  CLI does.
- **Plan-generate form** — press `g` to draft a new plan: fields for id, goal (text or file),
  base branch, title, and strategy (`multi` still refuses, per the CLI), then a gate-less
  live Planner pane and the drafted-plan outcome. The draft still needs your approval in
  Obsidian — the TUI never approves a plan.

The TUI is `Orca.Cli` only (Terminal.Gui 2.4.17, pinned above); `Orca.Core` and every
subcommand's output are unchanged.

### Prerequisites

- .NET SDK 10.0.x and Claude Code CLI **2.1.201** (the pinned contract version) on PATH.
- A one-time interactive `claude` login on the machine (Pro/Max subscription). That login
  is the only credential Orca ever uses — there is no API key to configure, and a
  logged-out machine fails loudly at invocation time.
- An Obsidian vault (any directory of Markdown works) and a target repository under git.
- An interactive terminal for the decision prompt. `checkpoint-run` ends in a real
  Approve/Reject/Defer selection; on a non-TTY context (CI, a piped shell) it does **not**
  crash — it runs the pipeline, then auto-defers, exiting **2** ("ran, needs a human") with
  the attempt left in the resumable deferred state. Re-run it from a normal terminal and the
  decision is picked back up with nothing re-paid (see Resuming below). A human's own
  deliberate defer exits 0. Bare `orca` on an interactive terminal opens the full-screen TUI
  instead (see Interactive TUI below); every subcommand keeps working for scripting.

### Install

    dotnet pack src/Orca.Cli -c Release
    dotnet tool install --global --add-source src/Orca.Cli/bin/Release orca.cli

That puts `orca` on PATH. `orca --help` lists the four commands.

The packed version is derived from git at pack time: `1.0.<commit count>`, with a
`-dirty` suffix when tracked files had uncommitted changes at pack time (so `1.0.11`, or
`1.0.11-dirty`). `dotnet tool list --global` therefore answers "which build is this?" —
cross-reference the patch number against `git rev-list --count HEAD`, and the short
commit SHA is embedded in the assembly's InformationalVersion (`1.0.11+g20b60ea`).

### Updating an installed tool

Because every commit produces a higher version, the normal flow after pulling or
committing changes is just:

    dotnet pack src/Orca.Cli -c Release
    dotnet tool update --global --add-source src/Orca.Cli/bin/Release orca.cli

Two caveats, both because `dotnet tool update` compares version strings and skips
anything that isn't strictly newer:

- **Repacking the same commit with the same dirty state** produces an unchanged version
  string, and `update` reports "already installed" and silently does nothing. To force
  that build in, uninstall and reinstall:

      dotnet tool uninstall --global orca.cli
      dotnet tool install --global --add-source src/Orca.Cli/bin/Release orca.cli

- **`-dirty` builds are prerelease versions**, which sort *below* the clean version of
  the same commit, and old `.nupkg` files accumulate in `bin/Release` (plain `install`
  picks the highest stable one). To install a dirty build, name it explicitly:

      dotnet tool uninstall --global orca.cli
      dotnet tool install --global --add-source src/Orca.Cli/bin/Release orca.cli --version 1.0.11-dirty

### One-time machine setup

Create the global config — `%APPDATA%\orca\config.json` on Windows,
`~/.config/orca/config.json` on Linux/macOS. It maps vault *names* to machine-local
paths (so repo configs stay portable) and carries the machine-level deny rules that no
repo can remove:

    {
      "schemaVersion": 1,
      "vaults": {
        "my-vault": "C:/Users/me/Documents/MyVault"
      },
      "deniedTools": [
        "Bash(git push*)", "Bash(git commit*)", "Bash(git reset*)",
        "Bash(git checkout*)", "Bash(git switch*)", "Bash(git restore*)",
        "Bash(git clean*)", "Bash(git rebase*)", "Bash(git merge*)",
        "Bash(rm*)", "Bash(rmdir*)", "Bash(del *)"
      ]
    }

### Per-repository setup

Commit an `orca.json` at the target repo's root. Minimal working shape (this is the
config the e2e run used, trimmed):

    {
      "schemaVersion": 1,
      "vault": {
        "name": "my-vault",
        "plansDir": "Projects/MyApp/Plans"
      },
      "commands": {
        "build": ["dotnet", "build", "MyApp.slnx"],
        "test":  ["dotnet", "test", "MyApp.slnx"]
      },
      "gateTimeoutSeconds": 600,
      "maxParseRetries": 2,
      "maxCheckpointAttempts": 3,
      "roles": {
        "planner":     { "allowedTools": ["Read", "Write", "Edit"],
                         "maxTurns": 30, "timeoutSeconds": 900,  "maxBudgetUsd": 2 },
        "implementer": { "allowedTools": ["Read", "Glob", "Grep", "Write", "Edit",
                                          "Bash(dotnet build*)", "Bash(dotnet test*)",
                                          "Bash(git status*)", "Bash(git diff*)", "Bash(git log*)"],
                         "maxTurns": 60, "timeoutSeconds": 1800, "maxBudgetUsd": 5 },
        "reconciler":  { "allowedTools": ["Read", "Write", "Edit"],
                         "maxTurns": 15, "timeoutSeconds": 600,  "maxBudgetUsd": 2 },
        "verifier":    { "allowedTools": ["Read", "Write", "Edit"],
                         "maxTurns": 15, "timeoutSeconds": 600,  "maxBudgetUsd": 2 }
      }
    }

Notes: `commands.build`/`commands.test` are argv arrays run as plain processes — no
shell, and they are the *gates*, so they must exit 0 on a healthy tree before you start.
`maxBudgetUsd` values are runaway backstops, not targets (the whole e2e checkpoint cost
$1.14). Use bare file-tool names (`Read`/`Write`/`Edit`): path-scoped rules like
`Write(dir/**)` silently match nothing on CLI 2.1.201, because file tools receive
absolute paths. Give the Implementer only the Bash prefixes your gates and diff-reading
need; the deny list catches the rest.

The first `plan-generate` or `checkpoint-run` in a repo appends `.orca/` to its
`.gitignore` if missing and then **stops**, asking you to commit that — Orca never
commits to your branch on its own. Commit it (or pre-add `.orca/` yourself) and re-run.

### The loop

**1. Get a plan into the vault.** Either have the Planner write one:

    orca plan-generate <plan-id> --goal-file goal.md --repo <repo> [--dry-run]

or scaffold one to write by hand: `orca plan-init <plan-id> --title "..."`. A good goal
brief states the repository layout, the wanted outcome, an explicit checkpoint size, and
— important — that every acceptance criterion must be checkable from the diff plus green
gates alone. The Planner writes a **draft**; it cannot approve itself.

**2. Review and approve the plan.** Read `<plansDir>/<plan-id>/<plan-id>.md`, edit what
you disagree with, then flip `status: draft` → `status: approved` in its frontmatter.
Nothing runs against a draft.

**3. Run one checkpoint** from an interactive terminal:

    orca checkpoint-run --repo <repo>

(`--plan` only when the vault holds several plans; `--checkpoint` only to override the
default first-pending; `--dry-run` first if you want to see the branch, prompt, and
exact argv without spending anything.) Orca refuses a dirty tree, creates
`orca/<plan-id>/<checkpoint-id>` off the base branch, runs the Implementer, then
verifies automatically: build gate, test gate, Reconciler (`implements_plan:
full|partial|none`), Verifier (per-criterion `pass|fail|unclear`). You get the rendered
verdicts, the working-tree diffstat, and the decision prompt:

- **Approve** — Orca commits everything on the checkpoint branch with a structured
  message. Merging that branch into the base branch is *your* act, done however you
  like (local merge, PR). Afterwards, mark the checkpoint `complete` in the plan file.
- **Reject** — optionally hard-reset the branch (asked explicitly, never automatic).
  The verification findings are already in the vault and ride the next attempt's
  Implementer prompt; re-run `checkpoint-run` for attempt n+1 (capped by
  `maxCheckpointAttempts`).
- **Defer** — leave everything; the next `checkpoint-run` detects the undecided attempt
  and comes straight back to this prompt without re-invoking anything.

**4. Check where things stand** any time: `orca status --repo <repo>` — plan,
checkpoints, latest verification verdict, accumulated cost.

Repeat step 3 per checkpoint. Orca never advances to the next checkpoint on its own.

### Resuming and failures

Every interruption resumes without re-paying: rate limit, auth expiry, budget kill,
timeout, and a non-TTY auto-defer all leave records + run state behind, and the next
`checkpoint-run` continues from the first sub-step that has no record. Nothing is ever
retried automatically; the CLI's own error text is printed with a per-cause hint (log in
again, raise the ceiling, wait for the window). A failed gate writes the verification
record from machine facts alone and spends nothing on the LLM roles.

### Where everything lands

- **Vault** (durable, reviewable): the plan file, `verification/` and `reconciliation/`
  records per attempt (append-only, written by Orca itself after validation; `.staging/`
  holds only rejected raw output, for inspection).
- **Target repo `.orca/`** (gitignored, machine-local): per-attempt NDJSON transcripts,
  gate output, the exact `--settings` payload used, and run state with per-checkpoint
  accumulated cost and token usage (`.orca/state/<plan-id>.json`).

## Commands

    orca plan-init <plan-id> --title <title> [--base-branch main] [--repo <path>]
    orca plan-generate <plan-id> (--goal <text> | --goal-file <path>) [--title <t>]
                       [--base-branch main] [--strategy single|multi] [--dry-run] [--repo <path>]
    orca checkpoint-run [--plan <id>] [--checkpoint <id>] [--dry-run] [--repo <path>]
    orca status [--plan <id>] [--repo <path>]
    orca ui [--repo <path>]
    orca                        (no arguments, on an interactive terminal — opens the TUI)

`checkpoint-run` runs exactly one checkpoint — clean-tree check, branch
`orca/{plan-id}/{checkpoint-id}` off the plan's base branch, one scoped `claude -p`
invocation — then verifies it and stops at an Approve / Reject / Defer prompt. Approve
commits with a structured message; Reject *offers* a hard reset (never automatic); Defer
leaves the decision, and the next `checkpoint-run` resumes it instead of paying for a new
invocation. `--dry-run` prints the branch, assembled prompt, and exact argv without
mutating anything.

**Verification runs automatically after the Implementer** (spec §8, built in step 7),
in a fixed order: deterministic gates first — `commands.build` then `commands.test` as
plain process calls, both always run, output captured under `.orca/runs/` — and if either
fails, the verification record is written (`verdict: fail`, `semantic: null`) and **no
LLM runs at all**. With green gates, the **Reconciler** judges the diff against the
checkpoint's plan section (one typed verdict: `implements_plan: full|partial|none` plus
`missing`/`extra` lists) and the **Verifier** judges each acceptance criterion byte-exact
(`pass|fail|unclear`, note required on every non-pass; `unclear` fails the checkpoint).
Both are vault-write-scoped `claude -p` invocations under the §5.4 file contract: they
write into `<plan-dir>/.staging/`, Orca parses fail-loud, retries with the exact error up
to `maxParseRetries`, then re-serializes the validated content into the canonical
append-only records — `reconciliation/<checkpoint>.attempt-<n>.md` and
`verification/<checkpoint>.attempt-<n>.md` (gates + semantic + a **derived** verdict; the
model never transcribes a machine fact). Failed findings ride the next attempt's
Implementer prompt as a "Previous attempt findings" section — the Reflexion loop, with
the vault as the memory. `maxCheckpointAttempts` caps those retries; the ceiling refuses
at prepare time, before anything is spent. An interrupted pipeline (rate limit, auth
expiry) resumes on the next `checkpoint-run`: sub-steps whose records already exist are
never re-paid.

`plan-generate` runs the Planner: one `claude -p` invocation, working directory = the
vault, scoped by `roles.planner`, that must write a schema-valid **draft** plan file
(`prompts/planner.md` carries the exact contract; the Planner can never self-approve).
The produced file is parsed by the real plan parser; a parse/validation failure feeds a
capped Reflexion-style retry (`maxParseRetries`) whose prompt quotes the exact error.
How much deliberation a plan deserves is your call — `--strategy` or an interactive
prompt, never auto-decided; multi-candidate is deferred until the implementer loop is
proven, and choosing it refuses clearly. `--dry-run` is strictly zero-mutation.

Every invocation is classified from observed evidence into a **stop reason** —
`succeeded | auth_failed | rate_limited | budget_exceeded | timed_out | failed` —
recorded in the run log's `process_exit` event, in run state (`lastStopReason`), and
per-attempt in planning state (`.orca/state/<plan-id>.planning.json`). Auth expiry,
rate limiting, budget kills, and timeouts fail the attempt loudly and are **never
retried automatically**: re-run later and attempt numbering resumes. All four roles
share one subscription window; only parse failures of a role's output file retry.

`ui` opens the full-screen interactive TUI (see **Interactive TUI** above for the shell,
live pane, and plan-generate form); bare `orca` on an interactive terminal opens the same
TUI before Spectre dispatch, while on a non-interactive terminal bare `orca` prints help
(exit 0) and `orca ui` refuses cleanly (exit 1). The TUI drives the same Core services as
the subcommands and spends nothing until you authorize a run in its preview modal.

## Configuration

Per-project settings live in `orca.json` at the **target repo's** root (committed there);
machine defaults live at `~/.config/orca/config.json` (Linux/macOS, XDG) or
`%APPDATA%\orca\config.json` (Windows). The repo file wins field-by-field; arrays replace
wholesale — except `deniedTools`, which is the **union** of both files (a repo can add
guardrails, never remove machine-level ones). Parsing is strict and fail-loud: unknown
keys, wrong types, or an unresolvable vault name refuse to load.

Vault paths are machine-local, so the committed file names its vault (`vault.name`) and
each machine's global config maps names to paths (`"vaults": { "name": "/abs/path" }`) —
`vaults` is only legal in the global file.

Per-role blocks (`planner`/`implementer`/`reconciler`/`verifier`) carry `allowedTools`
(verbatim patterns — `Bash(git diff *)` and `Bash(git diff*)` deny/allow different
command sets), `maxTurns`, `timeoutSeconds`, optional `model`, and optional
`maxBudgetUsd` (enforced by the CLI itself, even under subscription billing — distinct
result subtype `error_max_budget_usd`, probed on 2.1.201). `deniedTools` is safety
layer 2, passed to every invocation as a `--settings` `permissions.deny` payload: probed
to survive `--safe-mode` and to beat the allowlist (`PreToolUse` hooks do *not* survive
safe mode and are not used). There is **no auth field and no API-key field** anywhere.

Note: under subscription auth, a configured `model` the plan doesn't offer fails at
invocation time, not config-load time — the run summary surfaces the CLI's own message.

`maxParseRetries` (the §5.4 structured-output retry ceiling) is required the first time
`plan-generate` runs; roles only need to be configured when they are actually invoked.
Since step 7, `checkpoint-run` invokes the whole pipeline, so it requires
`maxParseRetries`, `maxCheckpointAttempts` (the verification-retry ceiling), and complete
`roles.reconciler`/`roles.verifier` blocks — validated at prepare time, before the
Implementer spends anything. `gateTimeoutSeconds` (optional, top-level) is the
runaway-build backstop: absent means unbounded (Ctrl-C is the interactive fallback); when
set, a gate crossing it has its process tree killed and records `fail`.

## Auth & secrets

There are none to manage. All four roles (Planner, Implementer, Reconciler, Verifier) run
through `ClaudeCodeExecutor` and authenticate via the machine's interactive `claude`
subscription login. No `ANTHROPIC_API_KEY`, no credential in any file in any repository —
by owner decision (2026-07-06), after the step-4 capture proved `--bare` cannot
authenticate from a subscription (hence `--safe-mode`). A logged-out machine fails loudly
via the CLI's own `authentication_failed` event; there is deliberately no pre-flight
credential check.

## Status

Build order (`docs/kickoff-v2.md` §9): **all 8 steps complete** — scaffold, domain model +
strict frontmatter parsers, vault reader/writer, `ClaudeCodeExecutor` on the empirically
captured 2.1.201 stream contract (fixtures in `tests/Orca.Tests/Fixtures/Transcripts/`),
config loading + git mechanics + the `plan-init`/`checkpoint-run`/`status` commands,
`IPlanningStrategy` with the single-candidate Planner (`plan-generate`) plus the
stop-reason contract for shared-subscription failure modes, the step-7 verification
loop (deterministic gates, the Reconciler, the semantic Verifier, their §5.4 vault-file
contracts with capped Reflexion retries, and the failed-verification → next-prompt
feedback loop), and the step-8 end-to-end validation: the full pipeline live against a
real repository — Planner-written plan, human approval, one implemented checkpoint,
green gates, `implements_plan: full`, all criteria pass, human diff review, merge —
with the per-checkpoint cost ($1.14) visible in run state and derivable from the run
logs.

Non-TTY `checkpoint-run` is no longer a crash: it runs the pipeline, then auto-defers and
exits **2** ("ran, needs a human"), with the attempt left in the resumable deferred state
that an interactive re-run recovers for free (a human's deliberate defer exits 0). Known
limitations that remain: path-scoped file-tool allowlist rules don't match on CLI 2.1.201
(use bare tool names + deny rules); multi-candidate planning is deferred (`--strategy multi`
refuses clearly); and the TUI tree reads **every** plan in the vault's plans directory, so a
single plan with invalid frontmatter blocks the whole tree until you fix it — the parser
fails loud with the offending file's path (never a crash), and the subcommands, which
resolve a single plan, are unaffected.
