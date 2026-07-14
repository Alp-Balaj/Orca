using Orca.Cli.Commands;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>Build-order step 3 (§3.6): the non-TTY fix for the subcommands. A
/// non-interactive terminal must never crash at a prompt after quota was spent — the
/// Spectre gate takes the safe, resumable path instead, checkpoint-run exits 2 for an
/// auto-defer (0 for a human's deliberate defer), and plan-generate without --strategy
/// refuses up front. The interactivity result is injected so every branch is exercised
/// without a real redirected console.</summary>
public sealed class NonInteractiveCliTests
{
    // --- The non-interactive Spectre gate's three answers (§8 decision 6, §3.6) ---
    //
    // Each method short-circuits before touching AnsiConsole or its context, so the
    // decision context is never dereferenced here — passing null documents that.

    [Fact]
    public async Task Non_interactive_gate_auto_defers_the_decision_with_the_flag_set()
    {
        var gate = new SpectreCheckpointGate(interactive: false);

        var decision = await gate.DecideAsync(context: null!, CancellationToken.None);

        Assert.Equal(CheckpointChoice.Defer, decision.Choice);
        Assert.True(decision.AutoDeferred);
        Assert.False(decision.DiscardChanges);
    }

    [Fact]
    public async Task Non_interactive_gate_declines_discarding_partial_changes()
    {
        var gate = new SpectreCheckpointGate(interactive: false);

        var discard = await gate.ConfirmDiscardAfterFailureAsync(context: null!, CancellationToken.None);

        Assert.False(discard);
        // It declined without rendering the failure evidence — nothing was written.
        Assert.False(gate.RenderedInvocationFailure);
    }

    [Fact]
    public async Task Non_interactive_gate_declines_spending_quota_on_pending_verification()
    {
        var gate = new SpectreCheckpointGate(interactive: false);

        Assert.False(await gate.ConfirmVerifyPendingAsync(pendingAttempt: 1, CancellationToken.None));
    }

    // --- Exit-code mapping: auto-defer exits 2, a deliberate defer stays 0 ---

    [Fact]
    public void Checkpoint_run_exits_2_when_the_decision_was_auto_deferred()
    {
        var result = new CheckpointSessionResult
        {
            Outcome = CheckpointSessionOutcome.Deferred,
            Preview = null!, // the Deferred branch reads only Outcome + AutoDeferred
            AutoDeferred = true,
        };

        Assert.Equal(2, CheckpointRunCommand.Render(result, gate: null!));
    }

    [Fact]
    public void Checkpoint_run_still_exits_0_for_a_humans_deliberate_defer()
    {
        var result = new CheckpointSessionResult
        {
            Outcome = CheckpointSessionOutcome.Deferred,
            Preview = null!,
            AutoDeferred = false,
        };

        Assert.Equal(0, CheckpointRunCommand.Render(result, gate: null!));
    }

    // --- plan-generate up-front strategy refusal on a non-interactive terminal ---

    [Fact]
    public void Plan_generate_refuses_up_front_without_a_strategy_on_a_non_interactive_terminal()
    {
        Assert.False(PlanGenerateCommand.RouteStrategyToHuman(strategyFlag: null, interactive: false));
    }

    [Fact]
    public void Plan_generate_with_an_explicit_single_strategy_does_not_refuse_on_a_non_interactive_terminal()
    {
        Assert.True(PlanGenerateCommand.RouteStrategyToHuman(strategyFlag: "single", interactive: false));
    }

    [Fact]
    public void Plan_generate_still_refuses_multi_even_when_given_explicitly()
    {
        // Multi-candidate is deferred regardless of terminal; the refusal is unchanged.
        Assert.False(PlanGenerateCommand.RouteStrategyToHuman(strategyFlag: "multi", interactive: false));
    }
}
