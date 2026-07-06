namespace Orca.Core.Domain;

/// <summary>Why one executor invocation stopped, classified from observed evidence only
/// (owner-approved 2026-07-06; derivations in <c>InvocationStopReasons</c>). Persisted in
/// run/planning state and the run log as snake_case strings. The operational rule it
/// exists for: quota-exhausted, auth-expired, budget-killed, and timed-out attempts fail
/// loudly and are <b>never</b> retried automatically — the human resumes later; only
/// parse/validation failures of a role's output file enter the capped Reflexion retry
/// loop (spec §5.4).</summary>
public enum InvocationStopReason
{
    /// <summary>Process exited on its own, exit 0, <c>result.is_error: false</c>.</summary>
    Succeeded,

    /// <summary>An event carried <c>error: "authentication_failed"</c> — the shape of a
    /// logged-out machine or an expired/invalid credential (both auth fixtures). Keychain
    /// state is per-machine and mutable, so this can appear on any invocation.</summary>
    AuthFailed,

    /// <summary>The run failed and the last <c>rate_limit_event</c> reported a
    /// <c>rate_limit_info.status</c> other than <c>"allowed"</c>. Defensive: only
    /// <c>"allowed"</c> has ever been captured (healthy runs emit the event too); the
    /// first real occurrence belongs in the transcript fixtures.</summary>
    RateLimited,

    /// <summary>The CLI killed the run crossing <c>--max-budget-usd</c> — distinct
    /// <c>result</c> subtype <c>error_max_budget_usd</c> (step-5 probe).</summary>
    BudgetExceeded,

    /// <summary>Orca's wall-clock ceiling killed the process tree; nothing the CLI
    /// reported afterwards exists to classify.</summary>
    TimedOut,

    /// <summary>Everything else — surfaced with the CLI's own <c>result</c> text, never
    /// a guess at a more specific cause.</summary>
    Failed,
}
