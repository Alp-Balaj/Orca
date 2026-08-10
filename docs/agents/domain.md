# Domain Docs

How the engineering skills should consume this project's domain documentation
when exploring the codebase.

Orca's written thinking lives in the Obsidian vault, not in this repo. Resolve
the vault as described in `issue-tracker.md` — by name via `orca.json` and the
global config, never by a hardcoded path. Everything below is relative to
`<vault>/7 - Dev Folder/1. Projects/Orca/`.

## Before exploring, read these

- **`Reference - How Orca Works.md`** — the verified-architecture note: config,
  vault schema, invocation anatomy, pipeline order, resume semantics, exit
  codes. This is the CONTEXT glossary for this project; it is where the domain
  vocabulary is defined.
- **`Decisions/`** — cross-cutting decisions (ADRs) that affect more than one
  plan, or that were escalated rather than resolved inline. Read the ones that
  touch the area you're about to work in.
- **`index.md`** — the dashboard, for reading order and current status.

In the repo itself, `README.md` and `docs/kickoff-v2.md` (the full spec) are the
primary sources for behaviour.

If any of these don't exist, **proceed silently**. Don't flag their absence;
don't suggest creating them upfront. `/domain-modeling` creates them lazily when
terms or decisions actually get resolved.

`Decisions/` does not exist yet. That is deliberate — the vault's own Dev Folder
conventions say not to pre-populate it with placeholder ADRs. Create it when the
first real escalated decision arrives, not before.

## Where a new decision goes

The vault's `7 - Dev Folder/CLAUDE.md` draws the line, and it holds here:

- A choice **local to one feature**, reversible and non-destructive, is resolved
  inline in that feature's spec with a `**Decision**:` line and its reason.
- A choice that is **cross-cutting**, or **irreversible/destructive**, is
  escalated to a note in `Decisions/`.

Every generated note gets a date header and stays clearly marked AI-generated,
per the vault's root `CLAUDE.md`.

## Use the glossary's vocabulary

When your output names a domain concept — a ticket title, a refactor proposal, a
hypothesis, a test name — use the term as defined in
`Reference - How Orca Works.md`. This project has a precise vocabulary
(checkpoint, gate, attempt, reconciler, verifier, plan, lane, evidence); don't
drift to synonyms.

If the concept you need isn't defined yet, that's a signal — either you're
inventing language the project doesn't use (reconsider) or there's a real gap
(note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing decision, surface it explicitly rather
than silently overriding:

> _Contradicts the checkpoint-status decision in `Decisions/` — but worth
> reopening because…_
