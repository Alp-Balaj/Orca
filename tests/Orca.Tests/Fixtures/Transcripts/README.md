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
| `safe-mode-hook-suppressed.ndjson` | `claude --safe-mode -p "Run the bash command: echo hello-from-probe …" --allowedTools "Bash(echo *)" --settings <PreToolUse deny hook> --output-format stream-json --verbose --max-turns 4` (step-5 probe, 2026-07-06) | 0 | **the negative that shaped safety layer 2**: an explicitly passed `--settings` `PreToolUse` deny hook never fires under `--safe-mode` — the command executes, the deny reason appears nowhere |
| `denied-by-permission-rules.ndjson` | same prompt/allowlist, `--settings {"permissions":{"deny":["Bash(echo *)"]}}` (step-5 probe) | 0 | `permissions.deny` **survives safe mode and wins over `--allowedTools`**: `user` tool_result `is_error:true` "Permission … has been denied", run itself completes `is_error:false` |
| `budget-exceeded.ndjson` | two-file Read task, `--safe-mode --max-budget-usd 0.05` under keychain subscription auth (step-5 probe) | 1 | **`--max-budget-usd` enforces under subscription billing**: run killed mid-checkpoint crossing the ceiling, `result` `subtype:"error_max_budget_usd"`, `is_error:true`, `result:null`, cost just past the limit |

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
- `result.subtype` **is** distinct for a budget kill (`error_max_budget_usd`, with
  `is_error:true`) — unlike auth failures, which hide behind `subtype:"success"`. Budget
  kills are therefore distinguishable from refusals and timeouts, and Orca relies on the
  CLI for cost-ceiling enforcement instead of double-accounting it.
- Deny-list mechanics on 2.1.201: `--settings` **hooks** are suppressed by `--safe-mode`;
  `--settings` **`permissions.deny` rules** are honored and beat the allowlist. Orca's
  layer 2 is built exclusively on the latter (`PermissionSettings`).
