using Orca.Core.Run;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Application.Invoke is the [Obsolete] legacy-static marshaling primitive decision 1
// approved (§1); same standing rationale and suppression as the rest of the TUI lane.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary><see cref="ICheckpointGate"/> as Terminal.Gui modal dialogs (tui-plan §4,
/// build-order step 5 item 4). Core calls each method on the background session Task; the
/// gate marshals onto the UI thread with <c>Application.Invoke</c>, renders the decision
/// context, blocks the executor on the human via a <see cref="TaskCompletionSource{T}"/>,
/// and returns the answer. The executor thread never touches a widget; the UI thread runs
/// the modal's nested loop while the session waits — the modal-session invariant made
/// literal.
///
/// Each rendered body mirrors the CONTENT the CLI's <c>SpectreCheckpointGate</c> +
/// <c>CheckpointRenderer</c> show today (via <see cref="RunDecisionText"/>). The gate always
/// has a human here — the TUI only runs on a TTY — so it NEVER auto-defers:
/// <c>AutoDeferred</c> stays a CLI-lane-only flag and is never set.</summary>
internal sealed class TuiCheckpointGate(Action flushPending) : ICheckpointGate
{
    public Task<bool> ConfirmDiscardAfterFailureAsync(CheckpointDecisionContext context, CancellationToken ct) =>
        OnUiThread(ct, () =>
        {
            TuiNotify.GateReached($"{context.Preview.Checkpoint.Id} failed — discard partial changes?");
            var body = new List<StyledLine>(RunDecisionText.Describe(context))
            {
                StyledLine.Empty,
                StyledLine.Plain("The invocation failed and left partial changes on the branch.", TuiRole.Warning),
                StyledLine.Plain("Discard them (hard reset + clean)?", TuiRole.Text),
            };
            return TuiModal.Ask("invocation failed — discard partial changes?", body,
                new TuiModal.Choice<bool>(Key.Y, "y discard", true),
                new TuiModal.Choice<bool>(Key.N, "n keep", false));
        });

    public Task<bool> ConfirmVerifyPendingAsync(int pendingAttempt, CancellationToken ct) =>
        OnUiThread(ct, () =>
        {
            // Deliberately minimal context — a yes/no quota decision on just the attempt
            // number (2026-07-09 review confirmed this is intended, not an omission).
            var body = new List<StyledLine>
            {
                StyledLine.Plain($"Attempt {pendingAttempt} has not been verified.", TuiRole.Text),
                StyledLine.Empty,
                StyledLine.Plain("Run gates + reconcile + verify now? This spends quota.", TuiRole.Dim),
            };
            TuiNotify.GateReached($"attempt {pendingAttempt} awaits verification");
            return TuiModal.Ask($"verify pending attempt {pendingAttempt}?", body,
                new TuiModal.Choice<bool>(Key.Y, "y verify", true),
                new TuiModal.Choice<bool>(Key.N, "n skip", false));
        });

    public Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct) =>
        OnUiThread(ct, () => Decide(context));

    private enum GateChoice { Approve, ApproveAndComplete, Reject, Defer }

    private static CheckpointDecision Decide(CheckpointDecisionContext context)
    {
        TuiNotify.GateReached(
            $"{context.Preview.Checkpoint.Id} ready for review" +
            (context.AttemptCostUsd is { } cost ? $" (${cost:0.00})" : ""));
        var body = new List<StyledLine>(RunDecisionText.Describe(context));
        // Approve & complete is the default gate choice (SUG-02): enter selects it and c is
        // the silent accelerator (same result, no extra footer hint). "Approve only" keeps
        // the commit-without-status-flip path on a. Defer stays LAST so a close without a
        // bound key falls through to the safe no-op (TuiModal returns the last choice).
        var choice = TuiModal.Ask($"checkpoint {context.Preview.Checkpoint.Id} — your call", body,
            new TuiModal.Choice<GateChoice>(Key.Enter, "enter / c approve & complete", GateChoice.ApproveAndComplete),
            new TuiModal.Choice<GateChoice>(Key.C, "", GateChoice.ApproveAndComplete),
            new TuiModal.Choice<GateChoice>(Key.A, "a approve only", GateChoice.Approve),
            new TuiModal.Choice<GateChoice>(Key.R, "r reject", GateChoice.Reject),
            new TuiModal.Choice<GateChoice>(Key.D, "d defer", GateChoice.Defer));

        return choice switch
        {
            GateChoice.Approve => new CheckpointDecision { Choice = CheckpointChoice.Approve },
            GateChoice.ApproveAndComplete => new CheckpointDecision
            {
                Choice = CheckpointChoice.Approve,
                MarkCheckpointComplete = true,
                MarkPlanComplete = context.IsLastCheckpoint && TuiModal.Ask(
                    "last checkpoint — also mark the plan complete?",
                    [
                        StyledLine.Plain("This is the last checkpoint in the plan.", TuiRole.Text),
                        StyledLine.Plain("Also mark the plan complete?", TuiRole.Text),
                    ],
                    new TuiModal.Choice<bool>(Key.Y, "y mark plan complete", true),
                    new TuiModal.Choice<bool>(Key.N, "n leave plan status", false)),
            },
            // Reject chains the discard confirmation exactly as the CLI does — never automatic.
            GateChoice.Reject => new CheckpointDecision
            {
                Choice = CheckpointChoice.Reject,
                DiscardChanges = TuiModal.Ask(
                    $"reject {context.Preview.Branch} — discard all changes?",
                    [
                        StyledLine.Plain($"Hard-reset {context.Preview.Branch}, discarding ALL uncommitted", TuiRole.Warning),
                        StyledLine.Plain("changes (including new files)?", TuiRole.Warning),
                    ],
                    new TuiModal.Choice<bool>(Key.Y, "y discard", true),
                    new TuiModal.Choice<bool>(Key.N, "n keep", false)),
                // SUG-11 §4: the one-line why. Optional — esc skips — but without it a
                // rejection of a green attempt reaches the retry with no usable signal.
                RejectionNote = TuiInput.AskLine("why? (rides the next attempt's prompt)",
                    [
                        StyledLine.Plain("One line on why this attempt did not stand.", TuiRole.Text),
                        StyledLine.Plain("Saved to the vault next to the records; the next attempt is prompted with it.", TuiRole.Dim),
                    ]),
            },
            _ => new CheckpointDecision { Choice = CheckpointChoice.Defer },
        };
    }

    /// <summary>Marshal a modal onto the UI thread and block the caller's (executor) thread
    /// on the human's answer. Flushes any pending lifecycle events first so every line that
    /// preceded this gate is painted before the modal appears (the gate reaches the UI
    /// thread by a different path than the drain timeout, so ordering is made explicit
    /// here).</summary>
    private Task<T> OnUiThread<T>(CancellationToken ct, Func<T> showModal)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetCanceled(ct));

        Application.Invoke(() =>
        {
            // BF-05: if cancellation already resolved the TCS (Ctrl-C confirmed while
            // the gates ran), the queued modal must never open — a "ghost" modal whose
            // answer lands in a completed TCS silently discards the human's decision
            // while they believe they approved.
            if (tcs.Task.IsCompleted)
                return;

            try
            {
                flushPending();
                tcs.TrySetResult(showModal());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });

        _ = tcs.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return tcs.Task;
    }
}
