# Triage Labels

The skills speak in terms of five canonical triage roles. This tracker is
file-based, so there are no labels to apply — the role is recorded as the
`triage:` frontmatter key on a ticket note (see `issue-tracker.md`).

| Label in mattpocock/skills | Value in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Owner needs to evaluate this ticket      |
| `needs-info`               | `needs-info`         | Waiting on the owner for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), set
`triage:` to the corresponding value from this table.

## Existing axes are orthogonal — leave them alone

The hand-authored `Bug Fixes/` and `Suggestions/` notes carry their own
frontmatter: `severity: high|medium|low` on bug fixes, `tier: 1|2|3` on
suggestions. These are **not** triage roles and do not map onto this table.
Never rewrite them, and never add a `triage:` key to a `BF-` or `SUG-` note —
those folders are read-only for the skills.

Edit the middle column to change the vocabulary.
