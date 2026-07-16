namespace Orca.Core.Run;

public enum CheckpointChoice { Approve, Reject, Defer }

public sealed record CheckpointDecision
{
    public required CheckpointChoice Choice { get; init; }

    /// <summary>Reject only: the human explicitly confirmed the hard reset
    /// (spec §7.1 — offered, never automatic).</summary>
    public bool DiscardChanges { get; init; }

    /// <summary>True when a non-interactive front end defaulted to Defer because no
    /// human could answer (§3.6 exit-code mapping). Never set by a real choice.</summary>
    public bool AutoDeferred { get; init; }

    /// <summary>Approve only: also flip this checkpoint's status to Complete in the plan
    /// file (via CheckpointStatusEditor), applied only after the commit itself succeeds.</summary>
    public bool MarkCheckpointComplete { get; init; }

    /// <summary>Approve + MarkCheckpointComplete only, and only meaningful when this is the
    /// plan's last checkpoint: also flip the plan's own status to Complete in the same edit.</summary>
    public bool MarkPlanComplete { get; init; }
}

/// <summary>Everything the decision moment can render: the preview, the run outcome (null
/// when resuming a previously deferred attempt), verification, working-tree evidence,
/// and this attempt's cost.</summary>
public sealed record CheckpointDecisionContext
{
    public required CheckpointRunPreview Preview { get; init; }
    public required CheckpointRunOutcome? Run { get; init; }
    public required CheckpointVerificationOutcome? Verification { get; init; }
    public required IReadOnlyList<string> StatusLines { get; init; }
    public required string DiffStat { get; init; }
    public required decimal? AttemptCostUsd { get; init; }

    /// <summary>True when <c>Preview.Checkpoint</c> is the last checkpoint in the plan's
    /// declared order — the gate uses this to decide whether to also offer flipping the
    /// plan's own status to complete alongside the checkpoint.</summary>
    public required bool IsLastCheckpoint { get; init; }
}

/// <summary>The per-checkpoint human gate (spec §7.1), as an interface both front ends
/// implement. Core calls it; nothing in Core ever answers it.</summary>
public interface ICheckpointGate
{
    /// <summary>A failed invocation left partial changes: offer discarding them.</summary>
    Task<bool> ConfirmDiscardAfterFailureAsync(CheckpointDecisionContext context, CancellationToken ct);

    /// <summary>A resumed pending attempt has no verification record: offer running
    /// gates + Reconciler + Verifier now (costs quota).</summary>
    Task<bool> ConfirmVerifyPendingAsync(int pendingAttempt, CancellationToken ct);

    /// <summary>The approve/reject/defer gate itself.</summary>
    Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct);
}
