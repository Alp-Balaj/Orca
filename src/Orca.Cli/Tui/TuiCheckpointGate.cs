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
            return TuiModal.Ask($"verify pending attempt {pendingAttempt}?", body,
                new TuiModal.Choice<bool>(Key.Y, "y verify", true),
                new TuiModal.Choice<bool>(Key.N, "n skip", false));
        });

    public Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct) =>
        OnUiThread(ct, () => Decide(context));

    private static CheckpointDecision Decide(CheckpointDecisionContext context)
    {
        var body = new List<StyledLine>(RunDecisionText.Describe(context));
        var choice = TuiModal.Ask($"checkpoint {context.Preview.Checkpoint.Id} — your call", body,
            new TuiModal.Choice<CheckpointChoice>(Key.A, "a approve", CheckpointChoice.Approve),
            new TuiModal.Choice<CheckpointChoice>(Key.R, "r reject", CheckpointChoice.Reject),
            new TuiModal.Choice<CheckpointChoice>(Key.D, "d defer", CheckpointChoice.Defer));

        return choice switch
        {
            CheckpointChoice.Approve => new CheckpointDecision { Choice = CheckpointChoice.Approve },
            // Reject chains the discard confirmation exactly as the CLI does — never automatic.
            CheckpointChoice.Reject => new CheckpointDecision
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
