# Verifier prompt — placeholder

Authored in build-order step 7. Semantic layer only: runs strictly after the deterministic
build/test gates pass, judges the diff against the checkpoint's acceptance criteria, and
writes a typed per-criterion checklist into the vault as a structured file (fixed schema
per spec §5.4, dedicated fail-loud parser). Runs as a scoped `claude -p` invocation through
`ClaudeCodeExecutor`. SHA-256 recorded per run.
