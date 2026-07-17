using Orca.Core.Run;
using Spectre.Console;

namespace Orca.Cli.Commands;

/// <summary>Today's AnsiConsole prompts behind the ICheckpointGate seam (§3.3). Renders
/// each decision moment exactly as CheckpointRunCommand did inline, then asks the same
/// prompts.
///
/// On a non-interactive terminal (§3.6) there is no human to answer and Spectre's prompts
/// would throw <em>after</em> the pipeline already spent quota. Instead this gate takes
/// the safe, resumable path the existing machinery already recovers for free — without
/// touching AnsiConsole:
/// <list type="bullet">
/// <item><c>DecideAsync</c> auto-defers (<c>Defer</c> with <c>AutoDeferred = true</c>) — the
///   one place the CLI lane ever sets that flag; the command maps it to exit 2.</item>
/// <item><c>ConfirmDiscardAfterFailureAsync</c> declines — partial changes are never
///   discarded without a human (§8 decision 6).</item>
/// <item><c>ConfirmVerifyPendingAsync</c> declines — no quota is spent unattended
///   (§8 decision 6).</item>
/// </list>
/// On an interactive terminal every method behaves byte-for-byte as before.</summary>
internal sealed class SpectreCheckpointGate(bool interactive) : ICheckpointGate
{
    /// <summary>True once ConfirmDiscardAfterFailureAsync has rendered the failure
    /// evidence (invocation result + changes), so the command doesn't render it a
    /// second time after the session returns.</summary>
    public bool RenderedInvocationFailure { get; private set; }

    public Task<bool> ConfirmDiscardAfterFailureAsync(CheckpointDecisionContext context, CancellationToken ct)
    {
        // §8 decision 6: on a non-interactive terminal, never discard partial changes —
        // leave everything in the deferred/resumable state, render nothing.
        if (!interactive)
            return Task.FromResult(false);

        CheckpointRenderer.RenderExecution(context.Run!);
        CheckpointRenderer.RenderInvocationFailedLine();
        RenderedInvocationFailure = true;
        CheckpointRenderer.RenderChanges(context.StatusLines, context.DiffStat);
        return Task.FromResult(AnsiConsole.Prompt(new ConfirmationPrompt(
                "Discard the partial changes it left behind (hard reset + clean)?")
            { DefaultValue = false }));
    }

    public Task<bool> ConfirmVerifyPendingAsync(int pendingAttempt, CancellationToken ct)
    {
        // §8 decision 6: no quota is spent unattended — decline verification on a
        // non-interactive terminal and fall through to the auto-defer.
        if (!interactive)
            return Task.FromResult(false);

        // BF-17: bare Enter must not spend quota — explicit consent, like the TUI twin.
        return Task.FromResult(AnsiConsole.Prompt(new ConfirmationPrompt(
                $"Attempt {pendingAttempt} has not been verified — run gates + reconcile + verify now?")
            { DefaultValue = false }));
    }

    public Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct)
    {
        // §3.6: no human to answer — auto-defer into the resumable state (the command maps
        // AutoDeferred to exit 2). This is the one place the CLI lane sets AutoDeferred.
        if (!interactive)
            return Task.FromResult(new CheckpointDecision
            {
                Choice = CheckpointChoice.Defer,
                AutoDeferred = true,
            });

        if (context.Run is { } run)
            CheckpointRenderer.RenderExecution(run);
        if (context.Verification is { } verification)
            CheckpointRenderer.RenderVerification(verification);
        CheckpointRenderer.RenderChanges(context.StatusLines, context.DiffStat);

        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Checkpoint [bold]{context.Preview.Checkpoint.Id.EscapeMarkup()}[/] — your call:")
            .AddChoices(
                "Approve — commit these changes",
                "Approve & mark complete",
                "Reject",
                "Defer — decide later"));

        if (choice == "Approve & mark complete")
        {
            var markPlanComplete = context.IsLastCheckpoint && AnsiConsole.Prompt(new ConfirmationPrompt(
                    "This is the last checkpoint in the plan — also mark the plan complete?")
                { DefaultValue = true });
            return Task.FromResult(new CheckpointDecision
            {
                Choice = CheckpointChoice.Approve,
                MarkCheckpointComplete = true,
                MarkPlanComplete = markPlanComplete,
            });
        }

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
