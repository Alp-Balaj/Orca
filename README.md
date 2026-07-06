# Orca

Orca orchestrates Claude Code as a disciplined, checkpoint-gated software-development agent.
An Obsidian vault is the stateless handoff layer: the Planner writes plan files, every
Implementer invocation is a single scoped `claude -p --bare` process whose prompt is
assembled fresh from the vault, and Reconciler/Verifier results are written back to the
vault where they gate — and feed — the next attempt. A human reviews and approves at every
checkpoint; nothing runs unattended across checkpoint boundaries.

Orca is a workflow, not an autonomous agent. The full spec is `docs/kickoff-v2.md`.

## Pinned tool versions

| Tool | Version | Notes |
| --- | --- | --- |
| Claude Code CLI | **2.1.201** | the stream-json event contract is derived empirically; a trait-gated integration test (build-order step 4) fails loudly on CLI breaking changes |
| .NET SDK | 10.0.301 | `net10.0`, nullable + implicit usings, warnings as errors, central package management |

## Layout

    src/Orca.Core/     domain types, vault I/O, executor wrapper, planning strategies, Anthropic client
    src/Orca.Cli/      thin Spectre.Console shell — command wiring only; packs as dotnet tool `orca`
    tests/Orca.Tests/  xUnit; the default `dotnet test` run excludes [Trait("Category","Integration")]
    prompts/           one versioned prompt file per agent role (SHA-256 recorded per run)
    docs/              the as-received kickoff spec

## Build & test

    dotnet build
    dotnet test

Integration tests (real `claude` binary + `ANTHROPIC_API_KEY`) arrive in step 4 and run only
deliberately: `dotnet test --filter Category=Integration`.

## Secrets

`ANTHROPIC_API_KEY` comes from the environment only. It is never read from, or written to,
any file in any repository. Non-negotiable.

## Status

Build order (`docs/kickoff-v2.md` §9): **steps 1–3 complete** — scaffold, domain model +
strict frontmatter parsers, vault reader/writer. Next session: step 4, `ClaudeCodeExecutor`
(empirical transcript capture first).
