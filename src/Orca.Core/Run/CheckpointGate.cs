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
