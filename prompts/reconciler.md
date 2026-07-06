# Reconciler prompt — placeholder

Authored in build-order step 7. Deliberately thin for the MVP: git diff + the relevant plan
section + one question — "does this diff implement what the plan says, and what's missing or
extra" — answered as a typed verdict written into the vault as a structured file (fixed
schema per spec §5.4, dedicated fail-loud parser), never parsed prose. Runs as a scoped
`claude -p` invocation through `ClaudeCodeExecutor`. SHA-256 recorded per run.
