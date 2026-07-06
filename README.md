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

## Commands

    orca plan-init <plan-id> --title <title> [--base-branch main] [--repo <path>]
    orca checkpoint-run [--plan <id>] [--checkpoint <id>] [--dry-run] [--repo <path>]
    orca status [--plan <id>] [--repo <path>]

`checkpoint-run` runs exactly one checkpoint — clean-tree check, branch
`orca/{plan-id}/{checkpoint-id}` off the plan's base branch, one scoped `claude -p`
invocation, then an Approve / Reject / Defer prompt. Approve commits with a structured
message; Reject *offers* a hard reset (never automatic); Defer leaves the decision, and
the next `checkpoint-run` resumes it instead of paying for a new invocation. `--dry-run`
prints the branch, assembled prompt, and exact argv without mutating anything.

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

## Auth & secrets

There are none to manage. All four roles (Planner, Implementer, Reconciler, Verifier) run
through `ClaudeCodeExecutor` and authenticate via the machine's interactive `claude`
subscription login. No `ANTHROPIC_API_KEY`, no credential in any file in any repository —
by owner decision (2026-07-06), after the step-4 capture proved `--bare` cannot
authenticate from a subscription (hence `--safe-mode`). A logged-out machine fails loudly
via the CLI's own `authentication_failed` event; there is deliberately no pre-flight
credential check.

## Status

Build order (`docs/kickoff-v2.md` §9): **steps 1–5 complete** — scaffold, domain model +
strict frontmatter parsers, vault reader/writer, `ClaudeCodeExecutor` on the empirically
captured 2.1.201 stream contract (fixtures in `tests/Orca.Tests/Fixtures/Transcripts/`),
and config loading + git mechanics + the `plan-init`/`checkpoint-run`/`status` commands.
Next session: step 6, `IPlanningStrategy` with the single-candidate path.
