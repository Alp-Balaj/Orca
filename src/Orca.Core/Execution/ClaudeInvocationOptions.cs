namespace Orca.Core.Execution;

/// <summary>Everything one <c>claude -p</c> invocation needs. These fields are the
/// executor-side shape that step 5's <c>orca.json</c> maps onto: verbatim allowed-tool
/// pattern arrays, max turns, wall-clock timeout, optional model and max budget. There
/// is deliberately no auth field anywhere — all four roles authenticate via the
/// machine's interactive <c>claude</c> subscription login (spec §1/§4).</summary>
public sealed record ClaudeInvocationOptions
{
    public required string Prompt { get; init; }

    /// <summary>Working directory for the child process — the target repo root for the
    /// Implementer, the vault for vault-writing roles.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Verbatim <c>--allowedTools</c> patterns, one argv element each. Prefix
    /// matching is exact: <c>Bash(git diff *)</c> (space before <c>*</c>) matches
    /// commands starting with <c>git diff</c>; <c>Bash(git diff*)</c> also matches
    /// <c>git diff-index</c>. Never normalized, joined, or trimmed. An empty list omits
    /// the flag entirely.</summary>
    public required IReadOnlyList<string> AllowedTools { get; init; }

    public required int MaxTurns { get; init; }

    /// <summary>Wall-clock ceiling enforced by the executor as a hard process-tree
    /// kill — a backstop against a runaway checkpoint, distinct from <c>--max-turns</c>.</summary>
    public required TimeSpan Timeout { get; init; }

    public string? Model { get; init; }

    /// <summary>Passed as <c>--max-budget-usd</c>. Whether the CLI enforces it under
    /// subscription auth is an open step-5 question (plan: orca-build, open items); it is
    /// plumbed through regardless so config can wire it once that is settled.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Path to a <c>--settings</c> JSON file carrying the
    /// <c>permissions.deny</c> payload (safety layer 2). Callers generate it via
    /// <c>PermissionSettings.ToJson</c> and place it under the run-log directory so every
    /// attempt records the deny rules that were in force. Null omits the flag.</summary>
    public string? SettingsFilePath { get; init; }

    /// <summary>Explicit path to the claude executable; null resolves from PATH. Also the
    /// test seam for process-mechanics tests.</summary>
    public string? ClaudeExecutable { get; init; }
}
