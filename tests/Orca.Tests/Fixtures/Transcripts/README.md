# Transcript fixtures — provenance

Real `claude` CLI output captured **before** the stream parser was written (checkpoint 04's
non-negotiable order of work), re-captured during the step-4 re-implementation.

- **Captured:** 2026-07-06, Arch Linux, native `claude` binary **2.1.201**
  (`~/.local/share/claude/versions/2.1.201`), machine logged in via interactive `claude`
  subscription login (keychain). `ANTHROPIC_API_KEY` and `CLAUDE_CODE_OAUTH_TOKEN` absent;
  nested-session `CLAUDE_CODE_*` variables explicitly unset for the capture. stdin closed
  (`</dev/null`).
- **Sanitization:** the scratch working directory was replaced with `/home/user/scratch`
  everywhere it appears (`cwd`, tool input, tool results). Nothing else was altered;
  session/message/request ids are as emitted.

| Fixture | Command | Exit | Shows |
| --- | --- | --- | --- |
| `success.ndjson` | `claude --safe-mode -p "Read the file README.md and summarize it in one sentence" --allowedTools "Read" --output-format stream-json --verbose` | 0 | full anatomy: `system`/`init` (session_id, `apiKeySource:"none"` under keychain auth), **`rate_limit_event` mid-stream in a normal successful run**, `assistant` (thinking/tool_use/text blocks), `user` (tool_result), final `result` (`total_cost_usd`, `usage`, `modelUsage`, `num_turns`) |
| `logged-out.ndjson` | same prompt, `--bare`, no key in env (bare mode never reads the keychain, so this reproduces the logged-out shape safely on a logged-in machine) | 1 | synthetic `assistant` event with `"error":"authentication_failed"` and "Not logged in", `result` with `is_error:true`, `total_cost_usd:0` |
| `invalid-api-key.ndjson` | same prompt, `--bare`, garbage `ANTHROPIC_API_KEY` | 1 | HTTP 401 shape: `apiKeySource:"ANTHROPIC_API_KEY"`, `assistant` `"error":"authentication_failed"`, `result` with `is_error:true`, `api_error_status:401` |

Observations pinned by these captures (and by the contract test):

- `-p` + `--output-format stream-json` **requires `--verbose`** on 2.1.201; without it:
  exit 1, empty stdout, stderr `Error: When using --print, --output-format=stream-json
  requires --verbose`.
- `result.subtype` is `"success"` even when the run failed — **`is_error` is the verdict
  field**, never `subtype`.
- Auth failures still exit 1 *and* emit well-formed NDJSON; the executor treats them as a
  completed process whose result carries `is_error:true`.
- Event types are an **open union** (`rate_limit_event` is undocumented and appears in
  successful runs); unknown types must pass through to the run log unparsed.
