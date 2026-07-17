using Orca.Core.Domain;

namespace Orca.Core.Execution;

/// <summary>Pure merge of executor facts into <see cref="RunState"/> — checkpoint 04's
/// "session_id, usage and cost extracted into run state" criterion. No I/O here; callers
/// own persistence.</summary>
public static class RunStateUpdates
{
    /// <summary>Merges one invocation's facts, attributed to <paramref name="roleKey"/>
    /// ("implementer" | "reconciler" | "verifier"). BF-07: SessionId and LastUsage are
    /// the IMPLEMENTER's — a judge invocation must never clobber the session id that
    /// anchors within-checkpoint --resume (and lands in the approval commit trailer) or
    /// the usage figure a human eyeballs for spend. Cost, stop reason, observed model,
    /// and the per-role usage accumulation (SUG-06) apply for every role.</summary>
    public static RunState Apply(
        RunState state, ClaudeExecutionResult execution, DateTimeOffset now,
        string roleKey = "implementer")
    {
        var isImplementer = roleKey == "implementer";
        return state with
        {
            SessionId = isImplementer ? execution.SessionId ?? state.SessionId : state.SessionId,
            AccumulatedCostUsd = state.AccumulatedCostUsd + (execution.Result?.TotalCostUsd ?? 0m),
            LastUsage = isImplementer ? execution.Result?.Usage ?? state.LastUsage : state.LastUsage,
            LastStopReason = execution.StopReason,
            LastModel = execution.ObservedModel ?? state.LastModel,
            // BF-08: keep the last non-benign rate-limit evidence instead of discarding
            // it after classification; a run that saw only benign statuses clears it.
            LastRateLimitStatus = NonBenignRateLimit(execution)?.Status,
            LastRateLimitResetsAt = NonBenignRateLimit(execution)?.ResetsAt,
            UsageByRole = Accumulate(state.UsageByRole, roleKey, execution.Result?.Usage),
            UpdatedAt = now,
        };
    }

    private static RateLimitInfo? NonBenignRateLimit(ClaudeExecutionResult execution) =>
        execution.LastRateLimit is { } rateLimit
        && !InvocationStopReasons.IsBenignRateLimitStatus(rateLimit.Status)
            ? rateLimit
            : null;

    private static IReadOnlyDictionary<string, TokenUsage>? Accumulate(
        IReadOnlyDictionary<string, TokenUsage>? current, string roleKey, TokenUsage? usage)
    {
        if (usage is null)
            return current;

        var merged = current is null
            ? new Dictionary<string, TokenUsage>(StringComparer.Ordinal)
            : new Dictionary<string, TokenUsage>(current, StringComparer.Ordinal);
        merged[roleKey] = (merged.TryGetValue(roleKey, out var prior) ? prior : TokenUsage.Zero).Plus(usage);
        return merged;
    }
}
