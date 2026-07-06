---
schema_version: 1
plan_id: 08-duplicate-checkpoint-ids
plan_version: 1
title: Fixture plan
status: draft
target_repo: RecipeApp
base_branch: main
created: 2026-07-06T14:00:00Z
updated: 2026-07-06T14:00:00Z
planning:
  strategy: single_candidate
checkpoints:
  - id: 01-step
    description: Do the thing
    status: pending
    acceptance_criteria:
      - it works
  - id: 01-step
    description: Do the thing again
    status: pending
    acceptance_criteria:
      - it works twice
---
Body.
