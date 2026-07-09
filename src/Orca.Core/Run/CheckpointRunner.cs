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
    /// attempt number, branch, prompt, and the exact argv. Throws instead of guessing.</summary>
    public CheckpointRunPreview Prepare(string? planId, string? checkpointId)
    {
        var reader = new VaultReader(_layout);
        var resolvedPlanId = PlanLocator.ResolvePlanId(_layout, planId);
        var plan = reader.ReadPlan(resolvedPlanId);

        if (plan.Status is not (PlanStatus.Approved or PlanStatus.InProgress))
            throw new CheckpointRunException(
                $"plan '{plan.PlanId}' has status '{SchemaEnums.ToYaml(plan.Status)}' — only approved or in_progress plans can run checkpoints");

        var (checkpoint, index) = SelectCheckpoint(plan, checkpointId);

        var state = _stateStore.Read(plan.PlanId);
        var attempt = state?.CheckpointId == checkpoint.Id ? state.Attempt + 1 : 1;
        var branch = CheckpointBranch.Name(plan.PlanId, checkpoint.Id);
        var branchExists = workspace.BranchExists(branch);

        if (branchExists && attempt == 1)
            throw new CheckpointRunException(
                $"branch '{branch}' already exists but run state shows no prior attempt for checkpoint '{checkpoint.Id}' — delete the branch or restore .orca/state before re-running");

        var pendingDecision = branchExists
            && state?.CheckpointId == checkpoint.Id
            && workspace.CurrentBranch() == branch
            && !workspace.IsClean();

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

        // The Reflexion loop's memory: the latest findings for this checkpoint ride the
        // next attempt's prompt, read from the vault only (spec §8). Each is the latest
        // of its kind — a gate-failed attempt has no reconciliation record.
        var verifications = reader.ReadVerificationRecords(plan.PlanId, checkpoint.Id);
        var reconciliations = reader.ReadReconciliationRecords(plan.PlanId, checkpoint.Id);
        var feedback = new AttemptFeedback
        {
            Verification = verifications.Count > 0 ? verifications[^1] : null,
            Reconciliation = reconciliations.Count > 0 ? reconciliations[^1] : null,
        };

        var prompt = CheckpointPrompt.Assemble(plan, checkpoint, childNote, feedback.IsEmpty ? null : feedback);
        var role = config.GetRole(RoleName.Implementer);
        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;

        var options = new ClaudeInvocationOptions
        {
            Prompt = prompt,
            WorkingDirectory = workspace.RepoRoot,
            AllowedTools = role.AllowedTools,
            MaxTurns = role.MaxTurns,
            Timeout = role.Timeout,
            Model = role.Model,
            MaxBudgetUsd = role.MaxBudgetUsd,
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
            Options = options,
            Argv = ClaudeArguments.Build(options),
            SettingsJson = settingsJson,
            LogPaths = _logLayout.AttemptPaths(plan.PlanId, checkpoint.Id, attempt),
        };
    }

    /// <summary>The mutating half: branch mechanics, settings file, run state, one
    /// executor invocation. The clean-tree requirement is enforced by
    /// <see cref="GitWorkspace"/> at the branch operations (spec §7.1).</summary>
    public async Task<CheckpointRunOutcome> RunAsync(
        CheckpointRunPreview preview, CancellationToken cancellationToken = default)
    {
        if (preview.PendingDecision)
            throw new CheckpointRunException(
                $"checkpoint '{preview.Checkpoint.Id}' attempt {preview.Attempt - 1} is still awaiting an approve/reject decision — resolve it before running again");

        if (!preview.BranchExists)
            workspace.CreateBranchFrom(preview.Branch, preview.Plan.BaseBranch);
        else
            workspace.SwitchTo(preview.Branch);

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

        var execution = await executor.ExecuteAsync(
            preview.Options, preview.LogPaths, observer: null, cancellationToken);

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

        for (var i = 0; i < plan.Checkpoints.Count; i++)
        {
            if (plan.Checkpoints[i].Status == CheckpointStatus.Pending)
                return (plan.Checkpoints[i], i);
        }

        throw new CheckpointRunException(
            $"plan '{plan.PlanId}' has no pending checkpoints — pass --checkpoint to re-run a specific one");
    }
}
