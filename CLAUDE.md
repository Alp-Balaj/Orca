# Orca

Orca orchestrates Claude Code as a disciplined, checkpoint-gated
software-development agent, with an Obsidian vault as the stateless handoff
layer. See `README.md`; the full spec is `docs/kickoff-v2.md`.

## Agent skills

### Issue tracker

Issues and tickets live in the Obsidian vault named by `orca.json` →
`vault.name`, under `7 - Dev Folder/1. Projects/Orca/`. GitHub Issues is not
used. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, recorded as a `triage:` frontmatter key rather than a
label. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context, kept in the vault rather than the repo — `Reference - How Orca
Works.md` is the glossary, `Decisions/` holds ADRs. See
`docs/agents/domain.md`.
