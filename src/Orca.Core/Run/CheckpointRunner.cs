using System.Text;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>A checkpoint run was refused — wrong plan/checkpoint state, not a bug.</summary>
public class CheckpointRunException(string message) : Exception(message);

/// <summary>Everything one checkpoint run would do, computed without mutating anything —
/// what <c>--dry-run</c> prints and what <see cref="CheckpointRunner.RunAsync"/> executes.</summary>
public sealed record CheckpointRunPreview
{
    public required Plan Plan { get; init; }
    public required Checkpoint Checkpoint { get; init; }
    public required int CheckpointIndex { get; init; }
    public required int Attempt { get; init; }
    public required string Branch { get; init; }
    public required bool BranchExists { get; init; }

    /// <summary>True when the previous invocation for this checkpoint is still awaiting
    /// the human's approve/reject decision (checkpoint branch checked out, uncommitted
    /// changes, matching run state). The CLI must resume that decision instead of
    /// launching — and paying for — another invocation.</summary>
    public required bool PendingDecision { get; init; }

    public required string Prompt { get; init; }
    public required string PromptSha256 { get; init; }

    /// <summary>UTF-8 byte length of <see cref="Prompt"/> — the same figure the executor's
    /// stdin size guard (<see cref="ClaudeCodeExecutor.MaxPromptBytes"/>) checks against,
    /// surfaced here so <c>--dry-run</c> and the TUI preview can show it before spending.</summary>
    public required int PromptBytes { get; init; }

    public required ClaudeInvocationOptions Options { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public required string? SettingsJson { get; init; }
    public required RunLogPaths LogPaths { get; init; }
}

public sealed record CheckpointRunOutcome
{
    public required CheckpointRunPreview Preview { get; init; }
    public required ClaudeExecutionResult Execution { get; init; }
    public required RunState RunState { get; init; }
}

/// <summary>Orchestrates exactly one Implementer invocation per call (spec §6 — the
/// one-invocation-per-checkpoint boundary lives here, not in the executor): clean tree →
/// checkpoint branch → assembled prompt → executor → run state. Approval and rejection
/// are separate, human-triggered calls; nothing here loops over checkpoints.</summary>
public sealed class CheckpointRunner(
    OrcaConfig config,
    GitWorkspace workspace,
    IClaudeExecutor executor,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);
    private readonly RunLogLayout _logLayout = new(workspace.RepoRoot);
    private readonly RunStateStore _stateStore = new(workspace.RepoRoot);

    /// <summary>Read-only planning of the run: resolves plan and checkpoint, computes
    /// attempt number, branch, prompt, and the exact argv. Throws instead of guessing.
    /// <paramref name="overrideCompleteByEvidence"/> and <paramref name="overrideUnmergedBase"/>
    /// let a human deliberately proceed past the two guards below instead of fixing the
    /// underlying state first.</summary>
    public CheckpointRunPreview Prepare(
        string? planId, string? checkpointId,
        bool overrideCompleteByEvidence = false, bool overrideUnmergedBase = false)
    {
        var reader = new VaultReader(_layout);
        var resolvedPlanId = PlanLocator.ResolvePlanId(_layout, planId);
        var plan = reader.ReadPlan(resolvedPlanId);

        if (plan.Status is not (PlanStatus.Approved or PlanStatus.InProgress))
            throw new CheckpointRunException(
                $"plan '{plan.PlanId}' has status '{SchemaEnums.ToYaml(plan.Status)}' — only approved or in_progress plans can run checkpoints");

        var (checkpoint, index) = SelectCheckpoint(plan, checkpointId);

        // SUG-03 stage A: depends_on is the truth, even for an explicit selection —
        // running a checkpoint whose dependencies aren't done builds on work that
        // doesn't exist yet. "Done" here is evidence-aware, matching the rest of this
        // method: a dependency counts when its plan status says complete OR its branch
        // head is an Orca approval commit (approved, status flip pending — the unmerged
        // guard below still speaks to merged-ness). No override: an overstated
        // dependency is a plan bug to correct, not a wall to climb.
        if (checkpointId is not null)
        {
            var blockers = CheckpointGraph.IncompleteDependencies(plan, checkpoint)
                .Where(dep =>
                {
                    var depBranch = CheckpointBranch.Name(plan.PlanId, dep);
                    return !(workspace.BranchExists(depBranch)
                        && IsApprovalCommit(workspace.CommitSubject(depBranch), plan.PlanId, dep));
                })
                .ToList();
            if (blockers.Count > 0)
                throw new CheckpointRunException(
                    $"checkpoint '{checkpoint.Id}' depends on {string.Join(", ", blockers.Select(b => $"'{b}'"))}, " +
                    "not yet complete (no completed status, no approval commit) — run those first, " +
                    "or correct the plan's depends_on if it overstates the coupling");
        }

        // The Reflexion loop's memory: the latest findings for this checkpoint ride the
        // next attempt's prompt, read from the vault only (spec §8). Each is the latest
        // of its kind — a gate-failed attempt has no reconciliation record. These same
        // records are also this checkpoint's durable evidence for attempt numbering below.
        var verifications = reader.ReadVerificationRecords(plan.PlanId, checkpoint.Id);
        var reconciliations = reader.ReadReconciliationRecords(plan.PlanId, checkpoint.Id);
        var runLogAttempts = ExistingRunLogAttempts(plan.PlanId, checkpoint.Id);

        var state = _stateStore.Read(plan.PlanId);
        // RunState is a cache of the last attempt this machine knows about, not the
        // source of truth — it can be hand-edited, stale after a crash, or simply absent
        // on a different machine. It only gets a say when it actually names this
        // checkpoint; otherwise the vault (plus any orphaned run logs) decides alone.
        var stateAttempt = state?.CheckpointId == checkpoint.Id ? state.Attempt + 1 : 1;
        var vaultAttempt = reader.NextAttemptNumber(plan.PlanId, checkpoint.Id);
        // Evidence only ever pushes the number forward (AttemptTruth), so state sitting
        // at or ahead of the vault is normal — verification simply hasn't caught up yet.
        // The vault standing *ahead* of a state that claims this same checkpoint is the
        // one shape that can't happen honestly: it means the vault holds evidence this
        // machine's state never learned about, and blindly trusting the state's lower
        // number would reuse an attempt the vault already has a record for.
        if (state?.CheckpointId == checkpoint.Id && vaultAttempt > stateAttempt)
            throw new CheckpointRunException(
                $"run state says checkpoint '{checkpoint.Id}' is on attempt {stateAttempt}, but the vault's " +
                $"verification/reconciliation records already show attempt {vaultAttempt} — refusing to guess " +
                "which is right. Likely causes: a different checkpoint ran in between and this state was never " +
                "updated, a second machine advanced this checkpoint independently, or .orca/state was deleted " +
                "and partially restored. Recommended action: inspect the vault records for this checkpoint and " +
                "either restore a consistent .orca/state or remove the stale one — there is no override for " +
                "this check.");
        var attempt = AttemptTruth.NextAttempt(stateAttempt, verifications, reconciliations, runLogAttempts);

        var branch = CheckpointBranch.Name(plan.PlanId, checkpoint.Id);
        var branchExists = workspace.BranchExists(branch);

        if (branchExists && attempt == 1)
            throw new CheckpointRunException(
                $"branch '{branch}' already exists but neither run state nor the vault shows a prior attempt " +
                $"for checkpoint '{checkpoint.Id}' — restore .orca/state if it was removed by mistake, or " +
                "inspect the branch's contents before continuing");

        var pendingDecision = branchExists
            && state?.CheckpointId == checkpoint.Id
            && workspace.CurrentBranch() == branch
            && !workspace.IsClean();

        // Evidence the plan file itself doesn't know about yet: a human approved this
        // checkpoint (its branch head is Orca's own structured commit) or the Verifier
        // already passed its latest attempt, but the checkpoint's status line in the plan
        // was never flipped to complete — almost always a forgotten "mark complete" click
        // rather than an intentional re-run. A checkpoint mid pending-decision (verified,
        // not yet committed) is the ordinary resume path, not forgotten evidence, so it is
        // exempt here. Refuse before anything spends so the human fixes the plan file (or
        // explicitly overrides) instead of paying for a redundant attempt.
        var latestRejection = reader.ReadLatestRejection(plan.PlanId, checkpoint.Id);
        if (checkpoint.Status == CheckpointStatus.Pending && !overrideCompleteByEvidence && !pendingDecision)
        {
            var approvalCommitAtHead = branchExists
                && IsApprovalCommit(workspace.CommitSubject(branch), plan.PlanId, checkpoint.Id);
            // A passing record whose attempt the human explicitly rejected (SUG-11 §4's
            // note proves it was deliberate, not a forgotten status flip) is not
            // "complete by evidence" — it's the reject-on-diff-review retry path, and
            // refusing here would demand an override for the workflow's own outcome.
            var latestVerificationPasses =
                verifications.Count > 0 && verifications[^1].Verdict == VerificationVerdict.Pass
                && latestRejection?.Attempt != verifications[^1].Attempt;

            if (approvalCommitAtHead || latestVerificationPasses)
            {
                var reason = approvalCommitAtHead
                    ? $"branch '{branch}' already has an Orca approval commit for it"
                    : $"attempt {verifications[^1].Attempt} already has a passing verification record";
                throw new CheckpointRunException(
                    $"checkpoint '{checkpoint.Id}' looks complete by evidence ({reason}), but " +
                    $"{_layout.PlanFile(plan.PlanId)} still lists it as status: pending — likely a forgotten " +
                    "status flip rather than an intentional re-run. Edit that file to mark the checkpoint " +
                    "complete, or pass --override-complete-by-evidence to run it again anyway.");
            }
        }

        // A dependency may have been approved without its branch ever being merged into
        // base_branch — this checkpoint would then build on top of work the base branch
        // doesn't have yet. Since SUG-03 stage A the guard walks the checkpoint's
        // EFFECTIVE dependencies (declared depends_on, or the implied previous
        // checkpoint), so an explicitly parallel checkpoint is never blocked by an
        // unrelated sibling. Warn loudly and require an explicit override; this is
        // read-only, so nothing here merges on the human's behalf.
        if (!overrideUnmergedBase)
        {
            foreach (var depId in CheckpointGraph.EffectiveDependsOn(plan, checkpoint))
            {
                var depBranch = CheckpointBranch.Name(plan.PlanId, depId);
                if (workspace.BranchExists(depBranch)
                    && IsApprovalCommit(workspace.CommitSubject(depBranch), plan.PlanId, depId)
                    && !workspace.IsAncestor(depBranch, plan.BaseBranch))
                {
                    throw new CheckpointRunException(
                        $"checkpoint '{depId}' was approved on '{depBranch}', but that branch " +
                        $"is not yet merged into base branch '{plan.BaseBranch}' — this checkpoint would build on " +
                        $"top of unmerged work. Merge it first: git switch {plan.BaseBranch} && git merge " +
                        $"{depBranch} — or pass --override-unmerged-base to proceed anyway.");
                }
            }
        }

        // Since step 7 a checkpoint run includes the verification pipeline, so a repo
        // missing its Reconciler/Verifier config or the two ceilings must refuse here —
        // before the Implementer spends anything — not halfway through.
        _ = config.GetRole(RoleName.Reconciler);
        _ = config.GetRole(RoleName.Verifier);
        _ = config.MaxParseRetries
            ?? throw new ConfigException(
                $"maxParseRetries is required to run checkpoints (the §5.4 parse-retry ceiling) — set it in {config.RepoConfigPath} or {config.GlobalConfigPath}");
        var maxAttempts = config.MaxCheckpointAttempts
            ?? throw new ConfigException(
                $"maxCheckpointAttempts is required to run checkpoints (the spec §8 verification-retry ceiling) — set it in {config.RepoConfigPath} or {config.GlobalConfigPath}");
        if (!pendingDecision && attempt > maxAttempts)
            throw new CheckpointRunException(
                $"attempt {attempt} would exceed maxCheckpointAttempts={maxAttempts} for checkpoint '{checkpoint.Id}' — " +
                $"review the verification findings in the vault and revise the plan or its criteria, or raise the ceiling in {config.RepoConfigPath}");

        var childNotePath = _layout.ChildNoteFile(plan.PlanId, checkpoint.Id);
        var childNote = File.Exists(childNotePath) ? reader.ReadChildNote(plan, checkpoint.Id) : null;

        var feedback = new AttemptFeedback
        {
            Verification = verifications.Count > 0 ? verifications[^1] : null,
            Reconciliation = reconciliations.Count > 0 ? reconciliations[^1] : null,
            Rejection = latestRejection,
        };

        var prompt = CheckpointPrompt.Assemble(
            plan, checkpoint, childNote, feedback.IsEmpty ? null : feedback,
            new GateCommands(config.BuildCommand, config.TestCommand), attempt);
        var role = config.GetRole(RoleName.Implementer);
        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;

        var options = new ClaudeInvocationOptions
        {
            Prompt = prompt,
            WorkingDirectory = workspace.RepoRoot,
            AllowedTools = role.AllowedTools,
            MaxTurns = role.MaxTurns,
            Timeout = role.Timeout,
            // SUG-05: a checkpoint's own model/max_budget_usd (the Planner's escalate-a-
            // hard-checkpoint / downgrade-a-trivial-one lever) wins over the role default;
            // absent on the checkpoint, roles.implementer's own setting is unchanged.
            Model = checkpoint.Model ?? role.Model,
            MaxBudgetUsd = checkpoint.MaxBudgetUsd ?? role.MaxBudgetUsd,
            SettingsFilePath = settingsJson is null
                ? null
                : _logLayout.SettingsFile(plan.PlanId, checkpoint.Id, attempt),
        };

        return new CheckpointRunPreview
        {
            Plan = plan,
            Checkpoint = checkpoint,
            CheckpointIndex = index,
            Attempt = attempt,
            Branch = branch,
            BranchExists = branchExists,
            PendingDecision = pendingDecision,
            Prompt = prompt,
            PromptSha256 = CheckpointPrompt.Sha256(prompt),
            PromptBytes = Encoding.UTF8.GetByteCount(prompt),
            Options = options,
            Argv = ClaudeArguments.Build(options),
            SettingsJson = settingsJson,
            LogPaths = _logLayout.AttemptPaths(plan.PlanId, checkpoint.Id, attempt),
        };
    }

    /// <summary>The mutating half: branch mechanics, settings file, run state, one
    /// executor invocation. The clean-tree requirement is enforced by
    /// <see cref="GitWorkspace"/> at the branch operations (spec §7.1). The observer
    /// (§3.2) sees the pipeline facts the executor cannot know — branch, role lifecycle —
    /// plus the role-tagged stream; null keeps today's behavior exactly.</summary>
    public async Task<CheckpointRunOutcome> RunAsync(
        CheckpointRunPreview preview, IRunObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        if (preview.PendingDecision)
            throw new CheckpointRunException(
                $"checkpoint '{preview.Checkpoint.Id}' attempt {preview.Attempt - 1} is still awaiting an approve/reject decision — resolve it before running again");

        if (!preview.BranchExists)
            workspace.CreateBranchFrom(preview.Branch, preview.Plan.BaseBranch);
        else
            workspace.SwitchTo(preview.Branch);
        observer?.OnRunEvent(new RunObservation.BranchReady(preview.Branch, Created: !preview.BranchExists));

        if (preview.Options.SettingsFilePath is { } settingsPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, preview.SettingsJson!);
        }

        var previous = _stateStore.Read(preview.Plan.PlanId);
        var now = _clock.GetUtcNow();
        var state = new RunState
        {
            SchemaVersion = 1,
            PlanId = preview.Plan.PlanId,
            PlanVersion = preview.Plan.PlanVersion,
            CheckpointIndex = preview.CheckpointIndex,
            CheckpointId = preview.Checkpoint.Id,
            Attempt = preview.Attempt,
            Branch = preview.Branch,
            // Fresh session per checkpoint attempt is the default (spec §8); --resume
            // would be an explicit, logged exception and is not built yet.
            SessionId = null,
            PromptHashes = new Dictionary<string, string> { ["implementer"] = preview.PromptSha256 },
            AccumulatedCostUsd = previous?.AccumulatedCostUsd ?? 0m,
            LastUsage = previous?.LastUsage,
            LastStopReason = previous?.LastStopReason,
            StartedAt = previous?.StartedAt ?? now,
            UpdatedAt = now,
        };
        _stateStore.Write(state);

        // The Implementer has no §5.4 parse-retry loop, so its invocation number within
        // the attempt is always 1 (log stem: <checkpoint>.attempt-<n>.ndjson).
        observer?.OnRunEvent(new RunObservation.RoleStarted(
            RoleName.Implementer, preview.Attempt, Invocation: 1, preview.LogPaths.EventLog));
        var execution = await executor.ExecuteAsync(
            preview.Options, preview.LogPaths,
            observer is null ? null : new RoleStreamForwarder(observer, RoleName.Implementer),
            cancellationToken);
        observer?.OnRunEvent(new RunObservation.RoleFinished(RoleName.Implementer, execution));

        var updated = RunStateUpdates.Apply(state, execution, _clock.GetUtcNow());
        _stateStore.Write(updated);

        return new CheckpointRunOutcome
        {
            Preview = preview,
            Execution = execution,
            RunState = updated,
        };
    }

    /// <summary>The human said yes: stage everything and commit with the structured
    /// message. Refuses when the workspace isn't on the expected branch — approval must
    /// apply to exactly the reviewed changes.</summary>
    public string Approve(CheckpointRunPreview preview, decimal? attemptCostUsd)
    {
        if (workspace.CurrentBranch() != preview.Branch)
            throw new CheckpointRunException(
                $"current branch '{workspace.CurrentBranch()}' is not the checkpoint branch '{preview.Branch}' — refusing to commit");

        var state = _stateStore.Read(preview.Plan.PlanId);
        var message = CheckpointCommitMessage.Build(
            preview.Plan.PlanId,
            preview.Plan.PlanVersion,
            preview.Checkpoint.Id,
            preview.Checkpoint.Description,
            state?.Attempt ?? preview.Attempt,
            state?.SessionId,
            attemptCostUsd);
        return workspace.StageAllAndCommit(message);
    }

    /// <summary>The human said no. Discarding is the confirmed-choice path only — the
    /// caller must hold an explicit confirmation (spec §7.1: offered, never automatic);
    /// without it the changes stay put for a later decision.</summary>
    public void Reject(bool discardChanges)
    {
        if (discardChanges)
            workspace.DiscardAllChanges();
    }

    /// <summary>Attempt numbers that already have a run log under
    /// <c>.orca/runs/&lt;plan&gt;/</c> for this checkpoint — the third witness
    /// <see cref="AttemptTruth"/> weighs alongside run state and the vault.</summary>
    private IReadOnlySet<int> ExistingRunLogAttempts(string planId, string checkpointId)
    {
        var directory = Path.Combine(_logLayout.RunsRoot, planId);
        if (!Directory.Exists(directory))
            return new HashSet<int>();

        var prefix = $"{checkpointId}.attempt-";
        var attempts = new HashSet<int>();
        foreach (var path in Directory.EnumerateFiles(directory, $"{prefix}*.ndjson"))
        {
            var stem = Path.GetFileName(path)[prefix.Length..^".ndjson".Length];
            if (int.TryParse(stem, out var attemptNumber))
                attempts.Add(attemptNumber);
        }

        return attempts;
    }

    /// <summary>True when <paramref name="commitSubject"/> is the subject line
    /// <see cref="CheckpointCommitMessage.Build"/> writes for this exact plan/checkpoint —
    /// how the two Prepare guards above recognize a real human approval rather than any
    /// commit that merely happens to sit at a checkpoint branch's head.</summary>
    internal static bool IsApprovalCommit(string? commitSubject, string planId, string checkpointId) =>
        commitSubject is not null
        && commitSubject.StartsWith($"orca: {planId}/{checkpointId} attempt ", StringComparison.Ordinal);

    private static (Checkpoint Checkpoint, int Index) SelectCheckpoint(Plan plan, string? checkpointId)
    {
        if (checkpointId is not null)
        {
            var index = plan.Checkpoints.ToList().FindIndex(c => c.Id == checkpointId);
            if (index < 0)
                throw new CheckpointRunException(
                    $"plan '{plan.PlanId}' has no checkpoint '{checkpointId}'");
            var checkpoint = plan.Checkpoints[index];
            if (checkpoint.Status is not (CheckpointStatus.Pending or CheckpointStatus.InProgress or CheckpointStatus.Rejected))
                throw new CheckpointRunException(
                    $"checkpoint '{checkpointId}' has status '{SchemaEnums.ToYaml(checkpoint.Status)}' — only pending, in_progress, or rejected checkpoints can run");

            return (checkpoint, index);
        }

        // Default selection: the first READY pending checkpoint — pending with every
        // effective dependency complete (SUG-03 stage A) — not merely the first pending.
        var ready = CheckpointGraph.ReadySet(plan);
        if (ready.Count > 0)
        {
            var first = ready[0];
            return (first, plan.Checkpoints.ToList().FindIndex(c => c.Id == first.Id));
        }

        var pending = plan.Checkpoints.Where(c => c.Status == CheckpointStatus.Pending).ToList();
        if (pending.Count > 0)
            throw new CheckpointRunException(
                $"plan '{plan.PlanId}' has pending checkpoints but none are ready: " +
                string.Join("; ", pending.Select(c =>
                    $"'{c.Id}' waits on {string.Join(", ", CheckpointGraph.IncompleteDependencies(plan, c))}")) +
                " — complete (and merge) a dependency first");

        throw new CheckpointRunException(
            $"plan '{plan.PlanId}' has no pending checkpoints — pass --checkpoint to re-run a specific one");
    }
}
