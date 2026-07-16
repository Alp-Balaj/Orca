using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>Whether a checkpoint already looks complete by durable evidence — an Orca
/// approval commit at its branch head, or a passing verification record for its latest
/// attempt. The same two signals <see cref="CheckpointRunner.Prepare"/>'s
/// complete-by-evidence guard reads; SUG-17's mark-complete affordances consult them the
/// other way around, to say whether a manual status flip is backed by evidence or needs
/// an explicit "anyway".</summary>
public sealed record CompletionEvidence(
    bool ApprovalCommitAtHead, int? PassingVerificationAttempt, string Branch)
{
    public bool Any => ApprovalCommitAtHead || PassingVerificationAttempt is not null;

    /// <summary>Human-readable reason when evidence exists; null when there is none.</summary>
    public string? Description => ApprovalCommitAtHead
        ? $"branch '{Branch}' has an Orca approval commit at its head"
        : PassingVerificationAttempt is { } attempt
            ? $"attempt {attempt} has a passing verification record"
            : null;

    /// <summary>Pure core: judge from already-gathered facts (unit-testable without git
    /// or a vault).</summary>
    public static CompletionEvidence From(
        string planId,
        string checkpointId,
        bool branchExists,
        string? branchHeadSubject,
        IReadOnlyList<VerificationRecord> verifications)
    {
        var branch = CheckpointBranch.Name(planId, checkpointId);
        var approval = branchExists
            && CheckpointRunner.IsApprovalCommit(branchHeadSubject, planId, checkpointId);
        var passing = verifications.Count > 0 && verifications[^1].Verdict == VerificationVerdict.Pass
            ? verifications[^1].Attempt
            : (int?)null;
        return new CompletionEvidence(approval, passing, branch);
    }

    /// <summary>Convenience probe that gathers the facts itself.</summary>
    public static CompletionEvidence Probe(
        GitWorkspace workspace, VaultReader reader, string planId, string checkpointId)
    {
        var branch = CheckpointBranch.Name(planId, checkpointId);
        var branchExists = workspace.BranchExists(branch);
        return From(
            planId,
            checkpointId,
            branchExists,
            branchExists ? workspace.CommitSubject(branch) : null,
            reader.ReadVerificationRecords(planId, checkpointId));
    }
}
