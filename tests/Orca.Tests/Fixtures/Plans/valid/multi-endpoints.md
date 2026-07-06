---
schema_version: 1
plan_id: multi-endpoints
plan_version: 2
title: Rework endpoint layering
status: draft
target_repo: RecipeApp
base_branch: main
created: 2026-07-05T09:00:00+02:00
updated: 2026-07-06T10:00:00+02:00
tags:
  - orca
  - plans
aliases:
  - Endpoint rework
planning:
  strategy: multi_candidate
  candidates: 3
  selected: 2
  rationale: Candidate B isolates EF changes behind the existing repository seam
checkpoints:
  - id: 01-extract-services
    description: Extract endpoint logic into services
    status: pending
    acceptance_criteria:
      - endpoints delegate to services only
---
Body prose for the multi-candidate plan.
