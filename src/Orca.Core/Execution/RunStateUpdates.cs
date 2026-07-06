using Orca.Core.Domain;

namespace Orca.Core.Execution;

/// <summary>Pure merge of executor facts into <see cref="RunState"/> — checkpoint 04's
/// "session_id, usage and cost extracted into run state" criterion. No I/O here; callers
/// own persistence.</summary>
public static class RunStateUpdates
{
    public static RunState Apply(RunState state, ClaudeExecutionResult execution, DateTimeOffset now) =>
        state with
        {
            SessionId = execution.SessionId ?? state.SessionId,
            AccumulatedCostUsd = state.AccumulatedCostUsd + (execution.Result?.TotalCostUsd ?? 0m),
            LastUsage = execution.Result?.Usage ?? state.LastUsage,
            UpdatedAt = now,
        };
}
