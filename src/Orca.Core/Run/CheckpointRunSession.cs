using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Vault;

namespace Orca.Core.Run;

public enum CheckpointSessionOutcome
{
    GitignoreAdded,          // stopped before running: commit the .gitignore change, re-run
    InvocationFailed,        // implementer stop reason != succeeded; gate already consulted about partial changes
    NoChanges,               // invocation succeeded but tree is clean — a failed attempt
    ClaudeVersionRefused,    // observed claude_code_version mismatched the pin under the opt-in refuse policy
    VerificationIncomplete,  // pipeline stopped before a record (auth/rate-limit/budget/timeout/parse ceiling)
    Approved,                // committed; CommitSha is set
    Rejected,                // DiscardedChanges says whether the confirmed reset ran
    Deferred,                // human deferred, or AutoDeferred on a non-interactive terminal
    Interrupted,             // cancellation killed the invocation; run state resumes it, nothing re-paid
}

public sealed record CheckpointSessionResult
{
    public required CheckpointSessionOutcome Outcome { get; init; }
    public required CheckpointRunPreview Preview { get; init; }
    public CheckpointRunOutcome? Run { get; init; }
    public CheckpointVerificationOutcome? Verification { get; init; }
    public string? CommitSha { get; init; }
    public bool DiscardedChanges { get; init; }
    public bool AutoDeferred { get; init; }
    public decimal? AttemptCostUsd { get; init; }

    /// <summary>Set on every human decision (Approve/Reject/Defer, never on the earlier
    /// non-decision outcomes) — <see cref="NextStepAdvice"/> computed from the plan as it
    /// stands right after the decision, so an Approve-and-mark-complete's status flip is
    /// already reflected.</summary>
    public string? NextStep { get; init; }
}

/// <summary>One human-triggered checkpoint session, start to decision. Owns the exact
/// sequence CheckpointRunCommand owned before it (§3.3): gitignore guard →
/// pending-decision resume → run → failure branching → verification → decision
/// application. The three points where it consults the human are the
/// <see cref="ICheckpointGate"/> — the gate renders and answers, it never mutates;
/// approval and rejection remain <see cref="CheckpointRunner"/> methods the session
/// calls after the gate has spoken. Both front ends are thin callers of this class, so
/// the sequence can no longer fork between them.</summary>
public sealed class CheckpointRunSession(
    OrcaConfig config, GitWorkspace workspace, IClaudeExecutor executor, TimeProvider? clock = null)
{
    private readonly CheckpointRunner _runner = new(config, workspace, executor);
    private readonly CheckpointVerifier _verifier = new(config, workspace, executor);
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Read-only; delegates to CheckpointRunner.Prepare. Front ends render the
    /// preview (and --dry-run stops here, exactly as today).</summary>
    public CheckpointRunPreview Prepare(
        string? planId, string? checkpointId,
        bool overrideCompleteByEvidence = false, bool overrideUnmergedBase = false) =>
        _runner.Prepare(planId, checkpointId, overrideCompleteByEvidence, overrideUnmergedBase);

    public async Task<CheckpointSessionResult> ExecuteAsync(
        CheckpointRunPreview preview,
        ICheckpointGate gate,
        IRunObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return preview.PendingDecision
                ? await ResumePendingDecisionAsync(preview, gate, observer, cancellationToken)
                : await RunFreshAttemptAsync(preview, gate, observer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The executor already killed the process tree and the existing resume
            // machinery (records + run state) recovers the attempt without re-paying —
            // an interrupt is the Ctrl-C the CLI always had, made explicit and recorded.
            return new CheckpointSessionResult
            {
                Outcome = CheckpointSessionOutcome.Interrupted,
                Preview = preview,
            };
        }
    }

    private async Task<CheckpointSessionResult> RunFreshAttemptAsync(
        CheckpointRunPreview preview, ICheckpointGate gate, IRunObserver? observer,
        CancellationToken cancellationToken)
    {
        // .orca/ must be ignored before run logs exist: approval's `git add -A` must not
        // sweep them into history, and rejection's `git clean -fd` must not delete them.
        // Stopping here is a workflow rule, not presentation — Orca never commits the
        // .gitignore change to the human's branch on its own.
        if (OrcaIgnore.EnsureIgnored(workspace.RepoRoot))
        {
            return new CheckpointSessionResult
            {
                Outcome = CheckpointSessionOutcome.GitignoreAdded,
                Preview = preview,
            };
        }

        var run = await _runner.RunAsync(preview, observer, cancellationToken);

        if (run.Execution.Outcome == ExecutionOutcome.TimedOut || run.Execution.Result is not { IsError: false })
        {
            var discarded = false;
            if (!workspace.IsClean()
                && await gate.ConfirmDiscardAfterFailureAsync(
                    Context(preview, run, verification: null, run.Execution.Result?.TotalCostUsd),
                    cancellationToken))
            {
                _runner.Reject(discardChanges: true);
                discarded = true;
            }

            return new CheckpointSessionResult
            {
                Outcome = CheckpointSessionOutcome.InvocationFailed,
                Preview = preview,
                Run = run,
                DiscardedChanges = discarded,
                AttemptCostUsd = run.Execution.Result?.TotalCostUsd,
            };
        }

        if (workspace.IsClean())
        {
            return new CheckpointSessionResult
            {
                Outcome = CheckpointSessionOutcome.NoChanges,
                Preview = preview,
                Run = run,
                AttemptCostUsd = run.Execution.Result?.TotalCostUsd,
            };
        }

        // SUG-15: cheapest to catch right here — before the gates/Reconciler/Verifier
        // spend anything on an attempt the refuse policy would reject anyway. One check
        // per attempt (the Implementer's invocation) is enough: every role invocation in
        // this attempt runs the same installed claude binary in the same session.
        var versionVerdict = ClaudeVersionCheck.Evaluate(
            run.Execution.ClaudeCodeVersion, ClaudeVersionCheck.PinnedVersion, config.ClaudeVersionPolicy);
        if (versionVerdict == ClaudeVersionVerdict.Refuse)
        {
            return new CheckpointSessionResult
            {
                Outcome = CheckpointSessionOutcome.ClaudeVersionRefused,
                Preview = preview,
                Run = run,
                AttemptCostUsd = run.Execution.Result?.TotalCostUsd,
            };
        }
        if (versionVerdict == ClaudeVersionVerdict.Warn)
        {
            observer?.OnRunEvent(new RunObservation.ClaudeVersionMismatch(
                RoleName.Implementer, run.Execution.ClaudeCodeVersion!, ClaudeVersionCheck.PinnedVersion));
        }

        // The pipeline runs automatically after the Implementer — gates first (machine
        // facts), then Reconciler and semantic Verifier only if the gates pass.
        var verification = await _verifier.RunAsync(
            preview.Plan, preview.Checkpoint, preview.Attempt, observer, cancellationToken);
        var attemptCost = (run.Execution.Result?.TotalCostUsd ?? 0m) + verification.CostUsd;
        if (verification.Record is null)
        {
            return new CheckpointSessionResult
            {
                Outcome = CheckpointSessionOutcome.VerificationIncomplete,
                Preview = preview,
                Run = run,
                Verification = verification,
                AttemptCostUsd = attemptCost,
            };
        }

        return await DecideAsync(preview, run, verification, attemptCost, gate, cancellationToken);
    }

    /// <summary>The deferred-decision resume: a pending attempt with no verification
    /// record is offered the pipeline first — gates re-run (cheap, deterministic) and
    /// only the sub-steps without records pay quota. Declining spends nothing; the
    /// decision is asked either way.</summary>
    private async Task<CheckpointSessionResult> ResumePendingDecisionAsync(
        CheckpointRunPreview preview, ICheckpointGate gate, IRunObserver? observer,
        CancellationToken cancellationToken)
    {
        var pendingAttempt = preview.Attempt - 1;

        CheckpointVerificationOutcome? verification = null;
        if (_verifier.HasRecord(preview.Plan.PlanId, preview.Checkpoint.Id, pendingAttempt))
        {
            verification = await _verifier.RunAsync(
                preview.Plan, preview.Checkpoint, pendingAttempt, observer, cancellationToken);
        }
        else if (await gate.ConfirmVerifyPendingAsync(pendingAttempt, cancellationToken))
        {
            verification = await _verifier.RunAsync(
                preview.Plan, preview.Checkpoint, pendingAttempt, observer, cancellationToken);
            if (verification.Record is null)
            {
                return new CheckpointSessionResult
                {
                    Outcome = CheckpointSessionOutcome.VerificationIncomplete,
                    Preview = preview,
                    Verification = verification,
                    AttemptCostUsd = verification.CostUsd,
                };
            }
        }

        // The resumed attempt's Implementer cost lives in run state, not in hand; the
        // commit message's per-attempt cost is deliberately omitted on resume (as today).
        return await DecideAsync(preview, run: null, verification, attemptCostUsd: null, gate, cancellationToken);
    }

    /// <summary>The per-checkpoint human gate (spec §7.1). Approve commits; Reject offers
    /// — never performs — a hard reset; Defer leaves everything for a later session,
    /// which resumes this decision instead of re-invoking.</summary>
    private async Task<CheckpointSessionResult> DecideAsync(
        CheckpointRunPreview preview,
        CheckpointRunOutcome? run,
        CheckpointVerificationOutcome? verification,
        decimal? attemptCostUsd,
        ICheckpointGate gate,
        CancellationToken cancellationToken)
    {
        var decision = await gate.DecideAsync(
            Context(preview, run, verification, attemptCostUsd), cancellationToken);

        switch (decision.Choice)
        {
            case CheckpointChoice.Approve:
                // BF-19: a verification record only speaks for the tree it was computed
                // against. Between that moment and this one — most realistically, a
                // Defer left the branch checked out and dirty for a later session — the
                // tree can drift, and StageAllAndCommit below would commit whatever is
                // here now under a verdict that certified something else. Refuse rather
                // than let a stale green record wave through an unverified change; old
                // records (no WorkingTreeHash) were never given this guarantee, so they
                // are not retroactively checked.
                if (verification?.Record?.WorkingTreeHash is { } certifiedHash
                    && workspace.WorkingTreeHash() != certifiedHash)
                    throw new CheckpointRunException(
                        $"the working tree has changed since checkpoint '{preview.Checkpoint.Id}' attempt " +
                        $"{verification.Record.Attempt}'s verification ran — the gates, Reconciler, and " +
                        "Verifier certified a different tree than what's here now, so approving would commit " +
                        "an unverified change. Re-run checkpoint-run to verify the current tree before " +
                        "approving, or reject/discard if the extra changes were unintended.");

                var sha = _runner.Approve(preview, attemptCostUsd);

                // The flip is applied only once the commit itself has succeeded — a
                // failed Approve throws before reaching here, so the plan file is
                // never touched for a decision that didn't actually land.
                if (decision.MarkCheckpointComplete)
                {
                    var isLastCheckpoint = preview.CheckpointIndex == preview.Plan.Checkpoints.Count - 1;
                    CheckpointStatusEditor.SetStatus(
                        _layout.PlanFile(preview.Plan.PlanId),
                        preview.Checkpoint.Id,
                        preview.Checkpoint.Status,
                        CheckpointStatus.Complete,
                        _clock.GetUtcNow(),
                        decision.MarkPlanComplete && isLastCheckpoint
                            ? new PlanStatusChange(preview.Plan.Status, PlanStatus.Complete)
                            : null);
                }

                return new CheckpointSessionResult
                {
                    Outcome = CheckpointSessionOutcome.Approved,
                    Preview = preview,
                    Run = run,
                    Verification = verification,
                    CommitSha = sha,
                    AttemptCostUsd = attemptCostUsd,
                    NextStep = NextStep(preview),
                };

            case CheckpointChoice.Reject:
                _runner.Reject(decision.DiscardChanges);

                // SUG-11 §4: persist the human's reason (when they gave one) next to the
                // records it overrules, so the next attempt's prompt carries it. After
                // Reject succeeds — a note for a rejection that didn't land would lie.
                // The decided attempt: the verification record's own number when one
                // exists, else the attempt just run (fresh) or resumed (preview - 1).
                if (!string.IsNullOrWhiteSpace(decision.RejectionNote))
                {
                    var decidedAttempt = verification?.Record?.Attempt
                        ?? (run is not null ? preview.Attempt : preview.Attempt - 1);
                    if (decidedAttempt >= 1)
                    {
                        new VaultWriter(_layout).WriteRejectionNote(
                            preview.Plan.PlanId, preview.Checkpoint.Id, decidedAttempt,
                            decision.RejectionNote.Trim(), _clock.GetUtcNow());
                    }
                }

                return new CheckpointSessionResult
                {
                    Outcome = CheckpointSessionOutcome.Rejected,
                    Preview = preview,
                    Run = run,
                    Verification = verification,
                    DiscardedChanges = decision.DiscardChanges,
                    AttemptCostUsd = attemptCostUsd,
                    NextStep = NextStep(preview),
                };

            default:
                return new CheckpointSessionResult
                {
                    Outcome = CheckpointSessionOutcome.Deferred,
                    Preview = preview,
                    Run = run,
                    Verification = verification,
                    AutoDeferred = decision.AutoDeferred,
                    AttemptCostUsd = attemptCostUsd,
                    NextStep = NextStep(preview),
                };
        }
    }

    /// <summary>Re-reads the plan so a status flip the decision just applied (Approve +
    /// MarkCheckpointComplete) is reflected before deciding what comes next.</summary>
    private string NextStep(CheckpointRunPreview preview) =>
        NextStepAdvice.Describe(new VaultReader(_layout).ReadPlan(preview.Plan.PlanId), preview.Branch);

    /// <summary>SUG-16 proposal A: the checkpoint a "run it now?" offer should name after
    /// <paramref name="decidedPreview"/>'s Approve, or null when there's nothing genuinely
    /// new to chain to. Re-reads the plan for the same reason <see cref="NextStep"/> does.</summary>
    public Checkpoint? ChainCandidate(CheckpointRunPreview decidedPreview) =>
        NextStepAdvice.ChainCandidate(
            new VaultReader(_layout).ReadPlan(decidedPreview.Plan.PlanId), decidedPreview.Checkpoint.Id);

    /// <summary>Fast-forwards the plan's base branch to the just-approved checkpoint
    /// branch — the one merge chaining ever performs, only once the human has confirmed
    /// "run next now?". False when that isn't possible (the base branch moved since the
    /// checkpoint branched); the caller must not proceed to the next checkpoint then.</summary>
    public bool MergeApprovedBranch(CheckpointRunPreview decidedPreview) =>
        workspace.TryFastForwardMerge(decidedPreview.Branch, decidedPreview.Plan.BaseBranch);

    private CheckpointDecisionContext Context(
        CheckpointRunPreview preview,
        CheckpointRunOutcome? run,
        CheckpointVerificationOutcome? verification,
        decimal? attemptCostUsd) => new()
    {
        Preview = preview,
        Run = run,
        Verification = verification,
        StatusLines = workspace.StatusLines(),
        DiffStat = workspace.DiffStat(),
        AttemptCostUsd = attemptCostUsd,
        IsLastCheckpoint = preview.CheckpointIndex == preview.Plan.Checkpoints.Count - 1,
    };
}
