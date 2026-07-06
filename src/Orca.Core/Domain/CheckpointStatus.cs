namespace Orca.Core.Domain;

/// <summary>Lifecycle of a single checkpoint. <see cref="Complete"/> means human-approved
/// AND committed; the transient approved-but-uncommitted moment lives in run state, never
/// in the vault.</summary>
public enum CheckpointStatus
{
    Pending,
    InProgress,
    AwaitingReview,
    Complete,
    Rejected,
    Blocked,
}
