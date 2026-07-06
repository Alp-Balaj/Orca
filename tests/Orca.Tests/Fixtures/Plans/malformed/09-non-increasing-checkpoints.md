---
schema_version: 1
plan_id: 09-non-increasing-checkpoints
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
  - id: 02-second
    description: Listed first
    status: pending
    acceptance_criteria:
      - it works
  - id: 01-first
    description: Listed second
    status: pending
    acceptance_criteria:
      - it works
---
Body.
