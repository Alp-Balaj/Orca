using Orca.Core.Domain;
using Orca.Core.Execution;

namespace Orca.Tests;

public class RunStateUpdatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 18, 0, 0, TimeSpan.Zero);

    private static RunState State() => new()
    {
        SchemaVersion = 1,
        PlanId = "recipe-tagging",
        PlanVersion = 1,
        CheckpointIndex = 0,
        CheckpointId = "01-tag-entity",
        Attempt = 1,
        Branch = "orca/recipe-tagging/01-tag-entity",
        SessionId = null,
        PromptHashes = new Dictionary<string, string>(),
        AccumulatedCostUsd = 1.00m,
        LastUsage = null,
        LastStopReason = null,
        StartedAt = Now.AddHours(-1),
        UpdatedAt = Now.AddHours(-1),
    };

    private static ClaudeExecutionResult Completed() => new()
    {
        Outcome = ExecutionOutcome.Completed,
        ExitCode = 0,
        SessionId = "0f058114-37ac-4e08-a46e-b2e1dfd7b83b",
        Result = new ClaudeResultSummary
        {
            Subtype = "success",
            IsError = false,
            SessionId = "0f058114-37ac-4e08-a46e-b2e1dfd7b83b",
            NumTurns = 2,
            DurationMs = 9486,
            TotalCostUsd = 0.16m,
            Usage = new TokenUsage
            {
                InputTokens = 2716,
                OutputTokens = 206,
                CacheCreationInputTokens = 5289,
                CacheReadInputTokens = 16913,
            },
        },
        Elapsed = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public void Merges_session_id_cost_and_usage()
    {
        var updated = RunStateUpdates.Apply(State(), Completed(), Now);

        Assert.Equal("0f058114-37ac-4e08-a46e-b2e1dfd7b83b", updated.SessionId);
        Assert.Equal(1.16m, updated.AccumulatedCostUsd);
        Assert.Equal(206, updated.LastUsage!.OutputTokens);
        Assert.Equal(Now, updated.UpdatedAt);
        Assert.Equal(State().StartedAt, updated.StartedAt);
    }

    [Fact]
    public void Stop_reason_lands_in_run_state_distinctly()
    {
        // The checkpoint-06 contract: a rate-limited attempt is recorded as exactly
        // that — never conflated with a refusal or a timeout kill.
        Assert.Equal(InvocationStopReason.Succeeded,
            RunStateUpdates.Apply(State(), Completed(), Now).LastStopReason);

        var rateLimited = Completed() with
        {
            ExitCode = 1,
            Result = Completed().Result! with { IsError = true },
            LastRateLimit = new RateLimitInfo { Status = "rejected" },
        };

        Assert.Equal(InvocationStopReason.RateLimited,
            RunStateUpdates.Apply(State(), rateLimited, Now).LastStopReason);
    }

    [Fact]
    public void Merges_the_observed_model()
    {
        // SUG-05: attributable actuals — the prompt-hash discipline already has this for
        // prompts, extended to the model that actually ran.
        var updated = RunStateUpdates.Apply(State(), Completed() with { ObservedModel = "claude-sonnet-5" }, Now);

        Assert.Equal("claude-sonnet-5", updated.LastModel);
    }

    [Fact]
    public void A_missing_observed_model_keeps_the_prior_one()
    {
        var first = RunStateUpdates.Apply(State(), Completed() with { ObservedModel = "claude-sonnet-5" }, Now);

        // A later invocation whose init event never arrived (e.g. a very early crash)
        // must not blank out an already-known model.
        var second = RunStateUpdates.Apply(first, Completed() with { ObservedModel = null }, Now.AddMinutes(5));

        Assert.Equal("claude-sonnet-5", second.LastModel);
    }

    [Fact]
    public void Non_benign_rate_limit_evidence_is_persisted_not_discarded()
    {
        // BF-08: previously LastRateLimit was classified against and thrown away.
        var rateLimited = Completed() with
        {
            ExitCode = 1,
            Result = Completed().Result! with { IsError = true },
            LastRateLimit = new RateLimitInfo { Status = "rejected", ResetsAt = 1_784_000_000 },
        };

        var updated = RunStateUpdates.Apply(State(), rateLimited, Now);

        Assert.Equal("rejected", updated.LastRateLimitStatus);
        Assert.Equal(1_784_000_000, updated.LastRateLimitResetsAt);
    }

    [Fact]
    public void A_benign_status_clears_stale_rate_limit_evidence()
    {
        var warned = RunStateUpdates.Apply(State(), Completed() with
        {
            ExitCode = 1,
            Result = Completed().Result! with { IsError = true },
            LastRateLimit = new RateLimitInfo { Status = "rejected", ResetsAt = 1_784_000_000 },
        }, Now);

        var healthy = RunStateUpdates.Apply(warned, Completed() with
        {
            LastRateLimit = new RateLimitInfo { Status = "allowed" },
        }, Now.AddMinutes(5));

        Assert.Null(healthy.LastRateLimitStatus);
        Assert.Null(healthy.LastRateLimitResetsAt);
    }

    [Fact]
    public void Cost_accumulates_across_invocations()
    {
        var once = RunStateUpdates.Apply(State(), Completed(), Now);
        var twice = RunStateUpdates.Apply(once, Completed(), Now.AddMinutes(5));

        Assert.Equal(1.32m, twice.AccumulatedCostUsd);
    }

    [Fact]
    public void Timeout_without_a_result_event_changes_nothing_but_the_clock()
    {
        var prior = RunStateUpdates.Apply(State(), Completed(), Now);
        var timedOut = new ClaudeExecutionResult
        {
            Outcome = ExecutionOutcome.TimedOut,
            ExitCode = 137,
            SessionId = null,
            Result = null,
            Elapsed = TimeSpan.FromMilliseconds(250),
        };

        var updated = RunStateUpdates.Apply(prior, timedOut, Now.AddMinutes(10));

        Assert.Equal(prior.SessionId, updated.SessionId);
        Assert.Equal(prior.AccumulatedCostUsd, updated.AccumulatedCostUsd);
        Assert.Equal(prior.LastUsage, updated.LastUsage);
        Assert.Equal(Now.AddMinutes(10), updated.UpdatedAt);
    }
}
