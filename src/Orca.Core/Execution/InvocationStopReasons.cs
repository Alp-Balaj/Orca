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
/// <item>rate limit — the run failed and the last <c>rate_limit_event</c> carried a
/// non-benign status (BF-08: statuses with an <c>allowed</c> prefix are warning-class
/// noise healthy runs emit as the window fills, never evidence of exhaustion; a genuine
/// exhaustion has still never been captured, so the classification reads "possible",
/// not certain);</item>
/// <item>everything else fails honestly as <see cref="InvocationStopReason.Failed"/>,
/// with the CLI's own <c>result</c> text as the explanation, never a guess.</item>
/// </list></summary>
public static class InvocationStopReasons
{
    public static InvocationStopReason Classify(ClaudeExecutionResult execution)
    {
        if (execution.Outcome == ExecutionOutcome.TimedOut)
            return InvocationStopReason.TimedOut;
        // Load-bearing order (BF-08): AuthFailed outranks Succeeded because both captured
        // auth fixtures hide the failure behind a `result` claiming subtype "success" —
        // the event flag is the only honest evidence. A run that both flagged auth and
        // genuinely succeeded has never been observed; if one ever is, the fixture
        // belongs in the suite before this order changes.
        if (execution.AuthenticationFailed)
            return InvocationStopReason.AuthFailed;
        if (execution.Result?.Subtype == "error_max_budget_usd")
            return InvocationStopReason.BudgetExceeded;
        if (execution is { ExitCode: 0, Result.IsError: false })
            return InvocationStopReason.Succeeded;
        if (execution.LastRateLimit is { } rateLimit && !IsBenignRateLimitStatus(rateLimit.Status))
            return InvocationStopReason.RateLimited;
        return InvocationStopReason.Failed;
    }

    /// <summary>BF-08: statuses that never indicate a blocked run. "allowed" is the only
    /// captured value, and Claude Code emits warning-class variants on healthy runs as
    /// the subscription window fills — an `allowed` prefix keeps those from turning an
    /// unrelated failure into "wait hours for a problem that was never quota".</summary>
    public static bool IsBenignRateLimitStatus(string status) =>
        status.StartsWith("allowed", StringComparison.Ordinal);

    /// <summary>The snake_case wire spelling used in run logs; JSON state uses the same
    /// spellings via its serializer's snake-case enum converter.</summary>
    public static string ToWire(InvocationStopReason reason) => reason switch
    {
        InvocationStopReason.Succeeded => "succeeded",
        InvocationStopReason.AuthFailed => "auth_failed",
        InvocationStopReason.RateLimited => "rate_limited",
        InvocationStopReason.BudgetExceeded => "budget_exceeded",
        InvocationStopReason.TimedOut => "timed_out",
        InvocationStopReason.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown stop reason"),
    };
}
