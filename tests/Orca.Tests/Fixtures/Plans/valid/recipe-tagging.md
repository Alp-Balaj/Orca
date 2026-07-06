---
schema_version: 1
plan_id: recipe-tagging
plan_version: 1
title: Add tagging to recipes
status: approved
target_repo: RecipeApp
base_branch: main
created: 2026-07-06T14:00:00Z
updated: 2026-07-06T15:30:00Z
planning:
  strategy: single_candidate
checkpoints:
  - id: 01-tag-entity
    description: Add Tag entity + EF mapping
    status: complete
    acceptance_criteria:
      - Tag entity with unique (UserId, Name)
      - dotnet build clean, migration added but never applied
  - id: 02-tag-endpoints
    description: CRUD endpoints for tags
    status: in_progress
    acceptance_criteria:
      - POST/GET/DELETE /api/tags following existing controller pattern
      - tests cover happy path and duplicate-name 409
---

The human-facing story of this plan lives here. Orca never parses this prose.
