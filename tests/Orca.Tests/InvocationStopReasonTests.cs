using Orca.Core.Domain;
using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>The owner-approved stop-reason derivations (2026-07-06), pinned on the same
/// evidence shapes the transcript fixtures carry. rate_limited has never been captured as
/// a real failure — its rule is deliberately defensive (failed run + last status not
/// "allowed"), and the first genuine occurrence belongs in the fixtures.</summary>
public class InvocationStopReasonTests
{
    private static ClaudeResultSummary Result(
        bool isError, string subtype = "success", string? text = null) => new()
    {
        Subtype = subtype,
        IsError = isError,
        SessionId = "s-1",
        NumTurns = 1,
        DurationMs = 10,
        TotalCostUsd = 0.01m,
        Usage = new TokenUsage
        {
            InputTokens = 1,
            OutputTokens = 1,
            CacheCreationInputTokens = 0,
            CacheReadInputTokens = 0,
        },
        ResultText = text,
    };

    private static ClaudeExecutionResult Execution(
        ExecutionOutcome outcome = ExecutionOutcome.Completed,
        int exitCode = 0,
        ClaudeResultSummary? result = null,
        bool authFailed = false,
        RateLimitInfo? rateLimit = null) => new()
    {
        Outcome = outcome,
        ExitCode = exitCode,
        SessionId = "s-1",
        Result = result,
        AuthenticationFailed = authFailed,
        LastRateLimit = rateLimit,
        Elapsed = TimeSpan.FromSeconds(1),
    };

    [Fact]
    public void A_clean_run_succeeds_even_when_it_saw_an_allowed_rate_limit_event() =>
        // The success fixture's shape: healthy runs emit rate_limit_event too.
        Assert.Equal(InvocationStopReason.Succeeded, Execution(
            result: Result(isError: false),
            rateLimit: new RateLimitInfo { Status = "allowed" }).StopReason);

    [Fact]
    public void Timeout_wins_over_everything_it_killed_midway() =>
        Assert.Equal(InvocationStopReason.TimedOut, Execution(
            outcome: ExecutionOutcome.TimedOut, exitCode: 137, authFailed: true).StopReason);

    [Fact]
    public void Auth_failure_is_the_event_flag_not_the_result_subtype() =>
        // The logged-out fixture: exit 1, subtype "success", is_error true — the
        // error:"authentication_failed" event is the signal.
        Assert.Equal(InvocationStopReason.AuthFailed, Execution(
            exitCode: 1,
            result: Result(isError: true, text: "Not logged in · Please run /login"),
            authFailed: true).StopReason);

    [Fact]
    public void Budget_kill_is_its_distinct_subtype() =>
        Assert.Equal(InvocationStopReason.BudgetExceeded, Execution(
            exitCode: 1, result: Result(isError: true, subtype: "error_max_budget_usd")).StopReason);

    [Fact]
    public void A_failed_run_with_a_non_allowed_rate_limit_status_is_rate_limited() =>
        Assert.Equal(InvocationStopReason.RateLimited, Execution(
            exitCode: 1,
            result: Result(isError: true),
            rateLimit: new RateLimitInfo { Status = "rejected" }).StopReason);

    [Fact]
    public void A_successful_run_is_never_rate_limited_whatever_the_last_event_said() =>
        Assert.Equal(InvocationStopReason.Succeeded, Execution(
            result: Result(isError: false),
            rateLimit: new RateLimitInfo { Status = "queued" }).StopReason);

    [Fact]
    public void Anything_else_fails_honestly_without_a_guess()
    {
        // A refusal-shaped failure: is_error, no auth event, no rate-limit evidence.
        Assert.Equal(InvocationStopReason.Failed, Execution(
            exitCode: 1, result: Result(isError: true, text: "some model refusal")).StopReason);
        // A crash without any result event.
        Assert.Equal(InvocationStopReason.Failed, Execution(exitCode: 2).StopReason);
        // A result that claims success from a non-zero exit is not a success.
        Assert.Equal(InvocationStopReason.Failed, Execution(
            exitCode: 3, result: Result(isError: false)).StopReason);
    }

    [Theory]
    [InlineData(InvocationStopReason.Succeeded, "succeeded")]
    [InlineData(InvocationStopReason.AuthFailed, "auth_failed")]
    [InlineData(InvocationStopReason.RateLimited, "rate_limited")]
    [InlineData(InvocationStopReason.BudgetExceeded, "budget_exceeded")]
    [InlineData(InvocationStopReason.TimedOut, "timed_out")]
    [InlineData(InvocationStopReason.Interrupted, "interrupted")]
    [InlineData(InvocationStopReason.Failed, "failed")]
    public void Wire_spellings_are_snake_case(InvocationStopReason reason, string wire) =>
        Assert.Equal(wire, InvocationStopReasons.ToWire(reason));

    [Fact]
    public void A_cancel_kill_classifies_as_interrupted_never_timed_out() =>
        // BF-12 §1: the ledger names the right killer.
        Assert.Equal(InvocationStopReason.Interrupted, Execution(
            outcome: ExecutionOutcome.Cancelled, exitCode: 137).StopReason);
}
