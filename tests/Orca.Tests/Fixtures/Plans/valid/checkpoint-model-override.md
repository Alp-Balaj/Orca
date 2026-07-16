---
schema_version: 1
plan_id: checkpoint-model-override
plan_version: 1
title: Checkpoint with a model override
status: approved
target_repo: RecipeApp
base_branch: main
created: 2026-07-06T14:00:00Z
updated: 2026-07-06T14:00:00Z
planning:
  strategy: single_candidate
checkpoints:
  - id: 01-hairy-migration
    description: A checkpoint judged hard enough to escalate the model tier
    status: pending
    acceptance_criteria:
      - The migration applies cleanly against a populated table
    model: opus
    max_budget_usd: 8
  - id: 02-trivial-docs-fix
    description: A checkpoint with no override — the ordinary case
    status: pending
    acceptance_criteria:
      - The typo is corrected
---

Body.
