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

## Auth & secrets

There are none to manage. All four roles (Planner, Implementer, Reconciler, Verifier) run
through `ClaudeCodeExecutor` and authenticate via the machine's interactive `claude`
subscription login. No `ANTHROPIC_API_KEY`, no credential in any file in any repository —
by owner decision (2026-07-06), after the step-4 capture proved `--bare` cannot
authenticate from a subscription (hence `--safe-mode`). A logged-out machine fails loudly
via the CLI's own `authentication_failed` event; there is deliberately no pre-flight
credential check.

## Status

Build order (`docs/kickoff-v2.md` §9): **steps 1–4 complete** — scaffold, domain model +
strict frontmatter parsers, vault reader/writer, and `ClaudeCodeExecutor` on the
empirically captured 2.1.201 stream contract (fixtures in
`tests/Orca.Tests/Fixtures/Transcripts/`). Next session: step 5, `orca.json` config +
git mechanics + first CLI commands.
