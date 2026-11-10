using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>Which kind of evidence makes a pending checkpoint look already complete.</summary>
public enum CompletionEvidenceKind
{
    /// <summary>The checkpoint branch's head is Orca's own structured approval commit.</summary>
    ApprovalCommit,

    /// <summary>The latest attempt has a passing verification record nobody rejected.</summary>
    PassingVerification,
}

/// <summary>A pending checkpoint that is waiting on dependencies, as
/// <see cref="CheckpointReadiness.NoneReady"/> reports it.</summary>
public sealed record WaitingCheckpoint(string CheckpointId, IReadOnlyList<string> WaitsOn);

/// <summary>What <see cref="CheckpointRunner.Classify"/> concluded about a checkpoint run,
/// derived read-only from the plan, vault records, run logs, run state and git — never
/// from a stored "current step". Either <see cref="Ready"/> (with the facts prompt
/// assembly needs) or one <see cref="Blocked"/> reason. Blocked reasons carry structured
/// fields so front ends can render them without parsing text; <see cref="Blocked.Message"/>
/// is the exact refusal text <see cref="CheckpointRunner.Prepare"/> throws.</summary>
public abstract record CheckpointReadiness
{
    private CheckpointReadiness() { }

    /// <summary>The run may proceed. <see cref="PendingDecision"/> = true is legal: the
    /// session resumes the decision instead of invoking again.</summary>
    public sealed record Ready(
        Plan Plan,
        Checkpoint Checkpoint,
        int CheckpointIndex,
        int Attempt,
        string Branch,
        bool BranchExists,
        bool PendingDecision,
        AttemptFeedback Feedback) : CheckpointReadiness;

    /// <summary>The run is refused. Each subtype is one guard.</summary>
    public abstract record Blocked : CheckpointReadiness
    {
        public abstract string Message { get; }
    }

    public sealed record PlanNotRunnable(string PlanId, PlanStatus Status) : Blocked
    {
        public override string Message =>
            $"plan '{PlanId}' has status '{SchemaEnums.ToYaml(Status)}' — only approved or in_progress plans can run checkpoints";
    }

    public sealed record CheckpointNotFound(string PlanId, string CheckpointId) : Blocked
    {
        public override string Message => $"plan '{PlanId}' has no checkpoint '{CheckpointId}'";
    }

    public sealed record CheckpointNotRunnable(string CheckpointId, CheckpointStatus Status) : Blocked
    {
        public override string Message =>
            $"checkpoint '{CheckpointId}' has status '{SchemaEnums.ToYaml(Status)}' — only pending, in_progress, or rejected checkpoints can run";
    }

    /// <summary>Default selection found pending checkpoints, but every one waits on an
    /// incomplete dependency.</summary>
    public sealed record NoneReady(string PlanId, IReadOnlyList<WaitingCheckpoint> Waiting) : Blocked
    {
        public override string Message =>
            $"plan '{PlanId}' has pending checkpoints but none are ready: " +
            string.Join("; ", Waiting.Select(w => $"'{w.CheckpointId}' waits on {string.Join(", ", w.WaitsOn)}")) +
            " — complete (and merge) a dependency first";
    }

    public sealed record NoPendingCheckpoints(string PlanId) : Blocked
    {
        public override string Message =>
            $"plan '{PlanId}' has no pending checkpoints — pass --checkpoint to re-run a specific one";
    }

    /// <summary>An explicitly selected checkpoint whose dependencies have neither a
    /// completed status nor an approval commit. No override exists.</summary>
    public sealed record DependenciesIncomplete(string CheckpointId, IReadOnlyList<string> Dependencies) : Blocked
    {
        public override string Message =>
            $"checkpoint '{CheckpointId}' depends on {string.Join(", ", Dependencies.Select(b => $"'{b}'"))}, " +
            "not yet complete (no completed status, no approval commit) — run those first, " +
            "or correct the plan's depends_on if it overstates the coupling";
    }

    /// <summary>Run state names this checkpoint but the vault already holds evidence of a
    /// later attempt. No override exists.</summary>
    public sealed record StateConflict(string CheckpointId, int StateAttempt, int VaultAttempt) : Blocked
    {
        public override string Message =>
            $"run state says checkpoint '{CheckpointId}' is on attempt {StateAttempt}, but the vault's " +
            $"verification/reconciliation records already show attempt {VaultAttempt} — refusing to guess " +
            "which is right. Likely causes: a different checkpoint ran in between and this state was never " +
            "updated, a second machine advanced this checkpoint independently, or .orca/state was deleted " +
            "and partially restored. Recommended action: inspect the vault records for this checkpoint and " +
            "either restore a consistent .orca/state or remove the stale one — there is no override for " +
            "this check.";
    }

    /// <summary>The checkpoint branch exists, yet nothing records a prior attempt.</summary>
    public sealed record BranchWithoutHistory(string CheckpointId, string Branch) : Blocked
    {
        public override string Message =>
            $"branch '{Branch}' already exists but neither run state nor the vault shows a prior attempt " +
            $"for checkpoint '{CheckpointId}' — restore .orca/state if it was removed by mistake, or " +
            "inspect the branch's contents before continuing";
    }

    /// <summary>The plan says pending but the evidence says done — usually a forgotten
    /// status flip. Overridable with <c>--override-complete-by-evidence</c>.</summary>
    public sealed record CompleteByEvidence(
        string CheckpointId, string PlanFile, CompletionEvidenceKind Evidence, string Branch, int LatestAttempt) : Blocked
    {
        public override string Message
        {
            get
            {
                var reason = Evidence == CompletionEvidenceKind.ApprovalCommit
                    ? $"branch '{Branch}' already has an Orca approval commit for it"
                    : $"attempt {LatestAttempt} already has a passing verification record";
                return $"checkpoint '{CheckpointId}' looks complete by evidence ({reason}), but " +
                    $"{PlanFile} still lists it as status: pending — likely a forgotten " +
                    "status flip rather than an intentional re-run. Edit that file to mark the checkpoint " +
                    "complete, or pass --override-complete-by-evidence to run it again anyway.";
            }
        }
    }

    /// <summary>A dependency was approved but its branch is not in the base branch yet.
    /// Overridable with <c>--override-unmerged-base</c>.</summary>
    public sealed record UnmergedDependency(string DependencyId, string DependencyBranch, string BaseBranch) : Blocked
    {
        public override string Message =>
            $"checkpoint '{DependencyId}' was approved on '{DependencyBranch}', but that branch " +
            $"is not yet merged into base branch '{BaseBranch}' — this checkpoint would build on " +
            $"top of unmerged work. Merge it first: git switch {BaseBranch} && git merge " +
            $"{DependencyBranch} — or pass --override-unmerged-base to proceed anyway.";
    }

    public sealed record AttemptsExhausted(
        string CheckpointId, int Attempt, int MaxAttempts, string RepoConfigPath) : Blocked
    {
        public override string Message =>
            $"attempt {Attempt} would exceed maxCheckpointAttempts={MaxAttempts} for checkpoint '{CheckpointId}' — " +
            $"review the verification findings in the vault and revise the plan or its criteria, or raise the ceiling in {RepoConfigPath}";
    }
}
