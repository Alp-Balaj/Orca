using Orca.Core.Run;
using Spectre.Console;

namespace Orca.Cli.Commands;

/// <summary>Today's AnsiConsole prompts behind the ICheckpointGate seam (§3.3). Renders
/// each decision moment exactly as CheckpointRunCommand did inline, then asks the same
/// prompts — on a non-interactive terminal the prompts still crash exactly as before
/// (the auto-defer/exit-2 fix is build-order step 3; nothing here ever sets
/// AutoDeferred).</summary>
internal sealed class SpectreCheckpointGate : ICheckpointGate
{
    /// <summary>True once ConfirmDiscardAfterFailureAsync has rendered the failure
    /// evidence (invocation result + changes), so the command doesn't render it a
    /// second time after the session returns.</summary>
    public bool RenderedInvocationFailure { get; private set; }

    public Task<bool> ConfirmDiscardAfterFailureAsync(CheckpointDecisionContext context, CancellationToken ct)
    {
        CheckpointRenderer.RenderExecution(context.Run!);
        CheckpointRenderer.RenderInvocationFailedLine();
        RenderedInvocationFailure = true;
        CheckpointRenderer.RenderChanges(context.StatusLines, context.DiffStat);
        return Task.FromResult(AnsiConsole.Prompt(new ConfirmationPrompt(
                "Discard the partial changes it left behind (hard reset + clean)?")
            { DefaultValue = false }));
    }

    public Task<bool> ConfirmVerifyPendingAsync(int pendingAttempt, CancellationToken ct) =>
        Task.FromResult(AnsiConsole.Prompt(new ConfirmationPrompt(
            $"Attempt {pendingAttempt} has not been verified — run gates + reconcile + verify now?")));

    public Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct)
    {
        if (context.Run is { } run)
            CheckpointRenderer.RenderExecution(run);
        if (context.Verification is { } verification)
            CheckpointRenderer.RenderVerification(verification);
        CheckpointRenderer.RenderChanges(context.StatusLines, context.DiffStat);

        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Checkpoint [bold]{context.Preview.Checkpoint.Id.EscapeMarkup()}[/] — your call:")
            .AddChoices("Approve — commit these changes", "Reject", "Defer — decide later"));

        if (choice.StartsWith("Approve", StringComparison.Ordinal))
            return Task.FromResult(new CheckpointDecision { Choice = CheckpointChoice.Approve });

        if (choice.StartsWith("Reject", StringComparison.Ordinal))
        {
            var discard = AnsiConsole.Prompt(new ConfirmationPrompt(
                    $"Hard-reset [bold]{context.Preview.Branch.EscapeMarkup()}[/], discarding ALL uncommitted changes (including new files)?")
                { DefaultValue = false });
            return Task.FromResult(new CheckpointDecision { Choice = CheckpointChoice.Reject, DiscardChanges = discard });
        }

        return Task.FromResult(new CheckpointDecision { Choice = CheckpointChoice.Defer });
    }
}
