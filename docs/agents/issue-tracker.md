# Issue tracker: Obsidian vault

Issues, tickets, and specs for Orca live in an Obsidian vault — **not** in this
repo, and **not** in GitHub Issues. GitHub Issues has never been used for this
project and holds zero issues; do not create issues there.

There is no CLI and no API. Every operation below is a plain file read or write.

## Locating the vault

This is a committed file, so it never names a machine path. Resolve the vault the
same way Orca itself does:

1. Read `vault.name` from `orca.json` at the repo root (currently
   `brian-the-brain`).
2. Look that name up in Orca's global config `vaults` map, which maps a vault
   name to its absolute path **on this machine**:
   - Linux / macOS: `~/.config/orca/config.json`
   - Windows: `%APPDATA%\orca\config.json`
3. The project root is `<vault>/7 - Dev Folder/1. Projects/Orca/`.

If the name isn't in the global config, stop and say so — that's the same
failure Orca raises, and it means this machine hasn't been set up yet. Never
guess a path, and never write an absolute path into a committed file.

The vault is **not under version control**. Tickets have no history, no diff, and
no rollback. Treat every write as irreversible and destructive-adjacent: prefer
appending over rewriting, and never delete a note to "clean up".

## Vault write rules — these override everything below

The vault's own `CLAUDE.md` files are the authority: one at the vault root, one
at `7 - Dev Folder/CLAUDE.md`. Read them before writing. In short:

- Write **only** inside `7 - Dev Folder/`. Everything else in the vault is
  read-only, including the personal folders. Refuse and explain if asked
  otherwise.
- Every generated file carries a date header and stays clearly marked
  AI-generated.
- Never pre-populate a folder with placeholder entries.

## Folder map

Under `7 - Dev Folder/1. Projects/Orca/`:

| Folder | Owner | Skills may |
| --- | --- | --- |
| `Tickets/` | these skills | read + write |
| `Bug Fixes/` | hand-authored (BF-01…) | **read only** |
| `Suggestions/` | hand-authored (SUG-01…) | **read only** |
| `Decisions/` | `/domain-modeling` | read + write (see `domain.md`) |
| `Sessions/` | after real coding sessions | read + write |
| `index.md` | the dashboard | read + update |

`Bug Fixes/` and `Suggestions/` are the owner's own tracker, with their own
numbering and their own frontmatter axes (`severity: high|medium|low` and
`tier: 1|2|3`). **Read them freely for context — never write into them, and never
renumber them.** Skill-generated work goes in `Tickets/` alone.

## Ticket conventions

- One feature per directory: `Tickets/<feature-slug>/`
- The spec is `Tickets/<feature-slug>/spec.md`
- One file per ticket: `Tickets/<feature-slug>/T-NN - <Title>.md`, numbered from
  `01` — never a single combined tickets file. The `T-` prefix keeps these
  distinct from `BF-` and `SUG-`.
- Frontmatter carries the machine-readable state:

```yaml
---
id: T-01
type: ticket
triage: needs-triage
blocked_by: []
status: open
created: 2026-08-10
---
```

- `triage:` holds the triage role — see `triage-labels.md` for the role strings.
- `blocked_by:` is a list of ticket ids (`[T-02, T-03]`). A ticket is unblocked
  when every id it lists has `status: done`.
- `status:` moves `open` → `in-progress` → `done`.
- Conversation history appends to the bottom under a `## Comments` heading.

## Branches

Match the convention already in this repo's history: `fix/<id>-<slug>`,
lowercased and without the hyphen in the id — e.g. `fix/t01-vault-locking`,
alongside the existing `fix/bf18-docs-drift` and `fix/sug03b-worktrees`.

## When a skill says "publish to the issue tracker"

Create a file under `Tickets/<feature-slug>/`, creating the directory if needed.

## When a skill says "fetch the relevant ticket"

Read the file. The owner will normally pass the ticket id or the path directly.
`BF-` and `SUG-` ids refer to the hand-authored folders.

## Keeping the dashboard current

`index.md` is the project's front door and is written in a specific voice —
a reading order with a one-line gloss per entry. When a feature's tickets are
created or a set completes, add or update a line under a `## Tickets` section.
Match the existing tone; don't restructure the file.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a file with one **child** file per ticket.

- **Map**: `Tickets/<effort>/map.md` — the Notes / Decisions-so-far / Fog body.
- **Child ticket**: `Tickets/<effort>/T-NN - <Title>.md`, numbered from `01`,
  with the question in the body. Frontmatter adds
  `wayfinder_type: research|prototype|grilling|task`.
- **Blocking**: the `blocked_by:` list, as above.
- **Frontier**: scan `Tickets/<effort>/` for tickets that are open, unblocked,
  and unclaimed; lowest number wins.
- **Claim**: set `status: in-progress` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set
  `status: done`, then append a context pointer to the map's Decisions-so-far.
