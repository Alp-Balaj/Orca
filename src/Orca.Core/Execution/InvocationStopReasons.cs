using Orca.Core.Domain;

namespace Orca.Core.Execution;

/// <summary>Pure classification of one invocation's outcome into
/// <see cref="InvocationStopReason"/>, derived only from captured evidence
/// (owner-approved 2026-07-06; fixture provenance in the transcript README):
/// <list type="bullet">
/// <item>timeout — the executor's own wall-clock kill, checked first because a killed
/// process's other evidence is whatever happened to arrive before the kill;</item>
/// <item>auth — any event carrying <c>error: "authentication_failed"</c> (both auth
/// fixtures; the <c>result</c> hides these behind <c>subtype: "success"</c>);</item>
/// <item>budget — the distinct <c>result</c> subtype <c>error_max_budget_usd</c>;</item>
/// <item>rate limit — the run failed and the last <c>rate_limit_event</c> said anything
/// but <c>"allowed"</c> (defensive: a genuine exhaustion has never been captured);</item>
/// <item>everything else fails honestly as <see cref="InvocationStopReason.Failed"/>,
/// with the CLI's own <c>result</c> text as the explanation, never a guess.</item>
/// </list></summary>
public static class InvocationStopReasons
{
    public static InvocationStopReason Classify(ClaudeExecutionResult execution)
    {
        if (execution.Outcome == ExecutionOutcome.TimedOut)
            return InvocationStopReason.TimedOut;
        if (execution.Outcome == ExecutionOutcome.Cancelled)
            return InvocationStopReason.Interrupted;
        if (execution.AuthenticationFailed)
            return InvocationStopReason.AuthFailed;
        if (execution.Result?.Subtype == "error_max_budget_usd")
            return InvocationStopReason.BudgetExceeded;
        if (execution is { ExitCode: 0, Result.IsError: false })
            return InvocationStopReason.Succeeded;
        if (execution.LastRateLimit is { } rateLimit && rateLimit.Status != "allowed")
            return InvocationStopReason.RateLimited;
        return InvocationStopReason.Failed;
    }

    /// <summary>The snake_case wire spelling used in run logs; JSON state uses the same
    /// spellings via its serializer's snake-case enum converter.</summary>
    public static string ToWire(InvocationStopReason reason) => reason switch
    {
        InvocationStopReason.Succeeded => "succeeded",
        InvocationStopReason.AuthFailed => "auth_failed",
        InvocationStopReason.RateLimited => "rate_limited",
        InvocationStopReason.BudgetExceeded => "budget_exceeded",
        InvocationStopReason.TimedOut => "timed_out",
        InvocationStopReason.Interrupted => "interrupted",
        InvocationStopReason.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown stop reason"),
    };
}
