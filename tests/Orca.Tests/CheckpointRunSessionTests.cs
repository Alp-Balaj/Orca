using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>The §3.3 session against a scripted executor and a scripted gate: every
/// CheckpointSessionOutcome branch of the flow that used to live untested inside
/// CheckpointRunCommand — gitignore guard, pending-decision resume, failure branching,
/// verification, and decision application. The gate only answers; every mutation is
/// asserted to have gone through the runner.</summary>
public sealed class CheckpointRunSessionTests : IDisposable
{
    private const string PlanText = """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        title: Add tagging to recipes
        status: approved
        target_repo: RecipeApp
        base_branch: main
        created: 2026-07-06T14:00:00Z
        updated: 2026-07-06T14:00:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-tag-entity
            description: Add Tag entity + EF mapping
            status: pending
            acceptance_criteria:
              - Tag entity exists
              - EF mapping compiles
        ---
        Plan body.
        """;

    private readonly TempGitRepo _repo = new();
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-session-vault-").FullName;

    public CheckpointRunSessionTests()
    {
        var planDir = Path.Combine(_vault, "Plans", "recipe-tagging");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "recipe-tagging.md"), PlanText);

        // Gate scripts are committed repo content so a session starts from a clean tree
        // and IsClean() answers reflect only what the fake Implementer wrote.
        GateScript(0);
        GateScript(1);
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "gate scripts");
    }

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_vault, recursive: true);
    }

    /// <summary>Most tests want the guard already satisfied, exactly as a real repo
    /// after its first refused run.</summary>
    private void CommitOrcaIgnore()
    {
        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");
    }

    private string[] GateScript(int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_repo.Root, $"gate-{exitCode}.cmd");
            if (!File.Exists(cmd))
                File.WriteAllText(cmd, $"@echo off\r\necho gate-output-{exitCode}\r\nexit /b {exitCode}\r\n");
            return [cmd];
        }

        var sh = Path.Combine(_repo.Root, $"gate-{exitCode}.sh");
        if (!File.Exists(sh))
        {
            File.WriteAllText(sh, $"#!/bin/sh\necho gate-output-{exitCode}\nexit {exitCode}\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return [sh];
    }

    private OrcaConfig Config(int buildExit = 0, int testsExit = 0) => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = GateScript(buildExit),
        TestCommand = GateScript(testsExit),
        GateTimeout = null,
        MaxParseRetries = 1,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = ["Bash(git push*)"],
        RoleSections = new RolesSection
        {
            Implementer = new RoleSection { AllowedTools = ["Read", "Edit"], MaxTurns = 10, TimeoutSeconds = 300 },
            Reconciler = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
            Verifier = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
        },
    };

    private CheckpointRunSession Session(ScriptedExecutor executor, int buildExit = 0) =>
        new(Config(buildExit), GitWorkspace.Discover(_repo.Root), executor);

    private string StagingReconciliation(int attempt = 1) => Path.Combine(
        _vault, "Plans", "recipe-tagging", ".staging", $"01-tag-entity.attempt-{attempt}.reconciliation.md");

    private string StagingSemantic(int attempt = 1) => Path.Combine(
        _vault, "Plans", "recipe-tagging", ".staging", $"01-tag-entity.attempt-{attempt}.semantic.md");

    private static void WriteStaging(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ValidReconciliation(int attempt = 1) => $"""
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        checkpoint_id: 01-tag-entity
        attempt: {attempt}
        implements_plan: full
        missing: []
        extra: []
        ---
        Complete.
        """;

    private static string ValidChecklist(int attempt = 1) => $"""
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        checkpoint_id: 01-tag-entity
        attempt: {attempt}
        criteria:
          - criterion: "Tag entity exists"
            verdict: pass
          - criterion: "EF mapping compiles"
            verdict: pass
        ---
        Entity looks right.
        """;

    /// <summary>Scripts a full green attempt: Implementer writes a file, then the
    /// Reconciler and Verifier land their §5.4 staging files.</summary>
    private ScriptedExecutor GreenPipelineExecutor(int attempt = 1)
    {
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success,
            () => _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n"));
        executor.Enqueue(ScriptedExecutor.Success,
            () => WriteStaging(StagingReconciliation(attempt), ValidReconciliation(attempt)));
        executor.Enqueue(ScriptedExecutor.Success,
            () => WriteStaging(StagingSemantic(attempt), ValidChecklist(attempt)));
        return executor;
    }

    [Fact]
    public async Task The_gitignore_guard_stops_the_session_before_anything_runs_or_asks()
    {
        // No .orca/ ignore entry yet — the guard must fire first (it is a workflow rule
        // owned by the session now, not the command).
        var executor = new ScriptedExecutor();
        var gate = new ScriptedGate();
        var session = Session(executor);
        var preview = session.Prepare(null, null);

        var result = await session.ExecuteAsync(preview, gate);

        Assert.Equal(CheckpointSessionOutcome.GitignoreAdded, result.Outcome);
        Assert.Empty(gate.Calls);
        Assert.Equal(0, executor.Invocations);
        Assert.Contains(".orca/", File.ReadAllText(Path.Combine(_repo.Root, ".gitignore")));
        Assert.Null(result.Run);
        // Nothing ran: still on main, no checkpoint branch.
        Assert.Equal("main", GitWorkspace.Discover(_repo.Root).CurrentBranch());
    }

    [Fact]
    public async Task A_failed_invocation_with_a_clean_tree_never_consults_the_gate()
    {
        CommitOrcaIgnore();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Failed, sideEffect: null);
        var gate = new ScriptedGate();
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.InvocationFailed, result.Outcome);
        Assert.Empty(gate.Calls);
        Assert.NotNull(result.Run);
        Assert.False(result.DiscardedChanges);
    }

    [Fact]
    public async Task A_failed_invocation_with_partial_changes_offers_the_discard_and_honors_yes()
    {
        CommitOrcaIgnore();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Failed,
            () => _repo.WriteFile("src/Partial.cs", "// half-finished\n"));
        var gate = new ScriptedGate { DiscardAnswer = true };
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.InvocationFailed, result.Outcome);
        Assert.Equal(["confirm-discard"], gate.Calls);
        Assert.NotNull(gate.DiscardContext!.Run);
        Assert.NotEmpty(gate.DiscardContext.StatusLines);
        Assert.True(result.DiscardedChanges);
        Assert.True(GitWorkspace.Discover(_repo.Root).IsClean());
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/Partial.cs")));
    }

    [Fact]
    public async Task A_declined_discard_leaves_the_partial_changes_in_place()
    {
        CommitOrcaIgnore();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Failed,
            () => _repo.WriteFile("src/Partial.cs", "// half-finished\n"));
        var gate = new ScriptedGate { DiscardAnswer = false };
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.InvocationFailed, result.Outcome);
        Assert.False(result.DiscardedChanges);
        Assert.True(File.Exists(Path.Combine(_repo.Root, "src/Partial.cs")));
    }

    [Fact]
    public async Task A_successful_invocation_that_changed_nothing_is_a_failed_attempt()
    {
        CommitOrcaIgnore();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success, sideEffect: null);
        var gate = new ScriptedGate();
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.NoChanges, result.Outcome);
        Assert.Empty(gate.Calls);
        Assert.NotNull(result.Run);
        Assert.Null(result.Verification);
    }

    [Fact]
    public async Task A_claude_version_mismatch_warns_but_does_not_block_under_the_default_policy()
    {
        CommitOrcaIgnore();
        var executor = GreenPipelineExecutorWithImplementerVersion("9.9.9-test");
        var observer = new RecordingRunObserver();
        var gate = new ScriptedGate { Decision = new CheckpointDecision { Choice = CheckpointChoice.Defer } };
        var session = Session(executor); // default Config() — ClaudeVersionPolicy defaults to Warn

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate, observer);

        // Reached the decision — the mismatch did not stop the pipeline.
        Assert.Equal(CheckpointSessionOutcome.Deferred, result.Outcome);
        var mismatch = Assert.Single(observer.Events.OfType<RunObservation.ClaudeVersionMismatch>());
        Assert.Equal(RoleName.Implementer, mismatch.Role);
        Assert.Equal("9.9.9-test", mismatch.Observed);
        Assert.Equal(ClaudeVersionCheck.PinnedVersion, mismatch.Pinned);
    }

    [Fact]
    public async Task A_claude_version_mismatch_refuses_under_the_opt_in_policy_before_spending_on_verification()
    {
        CommitOrcaIgnore();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success with { ClaudeCodeVersion = "9.9.9-test" },
            () => _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n"));
        // Nothing else is scripted — a Reconciler/Verifier invocation attempted here would
        // throw "invoked more times than scripted", proving the refusal happens first.
        var session = new CheckpointRunSession(
            Config() with { ClaudeVersionPolicy = ClaudeVersionPolicy.Refuse },
            GitWorkspace.Discover(_repo.Root), executor);
        var gate = new ScriptedGate();

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.ClaudeVersionRefused, result.Outcome);
        Assert.Empty(gate.Calls); // never reached a decision
        Assert.Null(result.Verification);
    }

    /// <summary>Like <see cref="GreenPipelineExecutor"/>, but the Implementer's invocation
    /// reports <paramref name="claudeCodeVersion"/> instead of leaving it unset — so the
    /// pipeline still completes green, with a version mismatch riding along.</summary>
    private ScriptedExecutor GreenPipelineExecutorWithImplementerVersion(string claudeCodeVersion, int attempt = 1)
    {
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success with { ClaudeCodeVersion = claudeCodeVersion },
            () => _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n"));
        executor.Enqueue(ScriptedExecutor.Success,
            () => WriteStaging(StagingReconciliation(attempt), ValidReconciliation(attempt)));
        executor.Enqueue(ScriptedExecutor.Success,
            () => WriteStaging(StagingSemantic(attempt), ValidChecklist(attempt)));
        return executor;
    }

    [Fact]
    public async Task A_pipeline_stopped_before_a_record_surfaces_as_incomplete_not_a_decision()
    {
        CommitOrcaIgnore();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success,
            () => _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n"));
        executor.Enqueue(ScriptedExecutor.RateLimited, sideEffect: null); // the Reconciler
        var gate = new ScriptedGate();
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.VerificationIncomplete, result.Outcome);
        Assert.Empty(gate.Calls);
        Assert.Equal("reconciler", result.Verification!.FailureRole);
        Assert.NotNull(result.Run);
        // The Implementer's changes are untouched for the resumed decision.
        Assert.False(GitWorkspace.Discover(_repo.Root).IsClean());
    }

    [Fact]
    public async Task Approve_commits_through_the_runner_with_the_attempt_cost()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate { Decision = new CheckpointDecision { Choice = CheckpointChoice.Approve } };
        var session = Session(GreenPipelineExecutor());

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.Approved, result.Outcome);
        Assert.Equal(["decide"], gate.Calls);
        Assert.Matches("^[0-9a-f]{40}$", result.CommitSha);
        Assert.Equal(0.75m, result.AttemptCostUsd); // 0.25 implementer + 0.50 verification
        Assert.True(GitWorkspace.Discover(_repo.Root).IsClean());

        var message = _repo.Git("log", "-1", "--format=%B");
        Assert.StartsWith("orca: recipe-tagging/01-tag-entity attempt 1", message);
        Assert.Contains("Cost: $0.75", message);

        // The gate saw the full decision moment: run, verification, tree evidence.
        var context = gate.DecisionContext!;
        Assert.NotNull(context.Run);
        Assert.NotNull(context.Verification!.Record);
        Assert.NotEmpty(context.StatusLines);
        Assert.Equal(0.75m, context.AttemptCostUsd);

        // Single-checkpoint fixture plan: this checkpoint is trivially the last one.
        Assert.True(context.IsLastCheckpoint);
        // Approve-only never touches the plan file — the checkpoint stays pending.
        Assert.Equal(CheckpointStatus.Pending, ReadPlan().Checkpoints[0].Status);
    }

    [Fact]
    public async Task Approve_and_mark_complete_flips_the_checkpoint_status_after_the_commit_succeeds()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate
        {
            Decision = new CheckpointDecision { Choice = CheckpointChoice.Approve, MarkCheckpointComplete = true },
        };
        var session = Session(GreenPipelineExecutor());

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.Approved, result.Outcome);
        Assert.Matches("^[0-9a-f]{40}$", result.CommitSha);
        Assert.Equal(CheckpointStatus.Complete, ReadPlan().Checkpoints[0].Status);
        // MarkPlanComplete was never set — the plan itself stays as it was.
        Assert.Equal(PlanStatus.Approved, ReadPlan().Status);
    }

    [Fact]
    public async Task Marking_the_last_checkpoint_complete_also_offers_flipping_the_plan_status()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate
        {
            Decision = new CheckpointDecision
            {
                Choice = CheckpointChoice.Approve,
                MarkCheckpointComplete = true,
                MarkPlanComplete = true,
            },
        };
        var session = Session(GreenPipelineExecutor());

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.Approved, result.Outcome);
        // The gate was told this was the last checkpoint, which is what let it offer
        // (and here, choose) flipping the plan's own status in the same edit.
        Assert.True(gate.DecisionContext!.IsLastCheckpoint);
        var plan = ReadPlan();
        Assert.Equal(CheckpointStatus.Complete, plan.Checkpoints[0].Status);
        Assert.Equal(PlanStatus.Complete, plan.Status);
    }

    [Fact]
    public async Task A_failed_approve_never_applies_the_checkpoint_status_flip()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate
        {
            Decision = new CheckpointDecision { Choice = CheckpointChoice.Approve, MarkCheckpointComplete = true },
            // Approve refuses to commit unless the workspace is on the checkpoint branch —
            // switch away right before the runner acts on the decision (raw git, bypassing
            // GitWorkspace's own clean-tree guard) so CheckpointRunner.Approve throws
            // before the flip is ever reached.
            BeforeDecisionReturns = () => _repo.Git("checkout", "main"),
        };
        var session = Session(GreenPipelineExecutor());
        var preview = session.Prepare(null, null);

        await Assert.ThrowsAsync<CheckpointRunException>(() => session.ExecuteAsync(preview, gate));

        Assert.Equal(CheckpointStatus.Pending, ReadPlan().Checkpoints[0].Status);
    }

    private Plan ReadPlan() => new VaultReader(new VaultLayout(_vault, "Plans")).ReadPlan("recipe-tagging");

    [Fact]
    public async Task Reject_with_confirmed_discard_resets_through_the_runner()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate
        {
            Decision = new CheckpointDecision { Choice = CheckpointChoice.Reject, DiscardChanges = true },
        };
        var session = Session(GreenPipelineExecutor());

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.Rejected, result.Outcome);
        Assert.True(result.DiscardedChanges);
        Assert.True(GitWorkspace.Discover(_repo.Root).IsClean());
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/Tag.cs")));
        // The verification record survives the reset — vault evidence rides attempt 2.
        Assert.True(File.Exists(new VaultLayout(_vault, "Plans")
            .VerificationFile("recipe-tagging", "01-tag-entity", 1)));
    }

    [Fact]
    public async Task Reject_without_discard_keeps_every_change()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate
        {
            Decision = new CheckpointDecision { Choice = CheckpointChoice.Reject, DiscardChanges = false },
        };
        var session = Session(GreenPipelineExecutor());

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.Rejected, result.Outcome);
        Assert.False(result.DiscardedChanges);
        Assert.True(File.Exists(Path.Combine(_repo.Root, "src/Tag.cs")));
    }

    [Fact]
    public async Task Defer_leaves_the_attempt_pending_for_the_next_session()
    {
        CommitOrcaIgnore();
        var gate = new ScriptedGate { Decision = new CheckpointDecision { Choice = CheckpointChoice.Defer } };
        var session = Session(GreenPipelineExecutor());

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate);

        Assert.Equal(CheckpointSessionOutcome.Deferred, result.Outcome);
        Assert.False(result.AutoDeferred);
        Assert.True(File.Exists(Path.Combine(_repo.Root, "src/Tag.cs")));

        // The next session resumes the decision instead of paying for a new invocation.
        var next = Session(new ScriptedExecutor());
        Assert.True(next.Prepare(null, "01-tag-entity").PendingDecision);
    }

    [Fact]
    public async Task Resuming_a_verified_pending_attempt_goes_straight_to_the_decision()
    {
        CommitOrcaIgnore();
        var deferGate = new ScriptedGate { Decision = new CheckpointDecision { Choice = CheckpointChoice.Defer } };
        var setup = Session(GreenPipelineExecutor());
        await setup.ExecuteAsync(setup.Prepare(null, null), deferGate);

        var executor = new ScriptedExecutor(); // nothing may be invoked on resume
        var gate = new ScriptedGate { Decision = new CheckpointDecision { Choice = CheckpointChoice.Approve } };
        var session = Session(executor);
        var preview = session.Prepare(null, "01-tag-entity");
        Assert.True(preview.PendingDecision);

        var result = await session.ExecuteAsync(preview, gate);

        Assert.Equal(CheckpointSessionOutcome.Approved, result.Outcome);
        Assert.Equal(["decide"], gate.Calls); // never asked to verify — the record exists
        Assert.Equal(0, executor.Invocations);
        Assert.True(gate.DecisionContext!.Verification!.RecordPreexisted);
        Assert.Null(gate.DecisionContext.Run);
        // Resume has no per-attempt cost in hand (it lives in run state), as today.
        Assert.Null(result.AttemptCostUsd);
        var message = _repo.Git("log", "-1", "--format=%B");
        Assert.Contains("Cost: unknown", message);
    }

    [Fact]
    public async Task Resuming_an_unverified_attempt_and_declining_verification_still_asks_the_decision()
    {
        CommitOrcaIgnore();
        await RunUntilVerificationIncomplete();

        var executor = new ScriptedExecutor();
        var gate = new ScriptedGate
        {
            VerifyPendingAnswer = false,
            Decision = new CheckpointDecision { Choice = CheckpointChoice.Defer },
        };
        var session = Session(executor);
        var preview = session.Prepare(null, "01-tag-entity");
        Assert.True(preview.PendingDecision);

        var result = await session.ExecuteAsync(preview, gate);

        Assert.Equal(CheckpointSessionOutcome.Deferred, result.Outcome);
        Assert.Equal(["confirm-verify", "decide"], gate.Calls);
        Assert.Equal(1, gate.PendingAttemptAsked);
        Assert.Null(gate.DecisionContext!.Verification); // declining spent nothing
        Assert.Equal(0, executor.Invocations);
    }

    [Fact]
    public async Task Resuming_an_unverified_attempt_and_confirming_runs_the_pipeline_for_that_attempt()
    {
        CommitOrcaIgnore();
        await RunUntilVerificationIncomplete();

        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success,
            () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(ScriptedExecutor.Success,
            () => WriteStaging(StagingSemantic(), ValidChecklist()));
        var gate = new ScriptedGate
        {
            VerifyPendingAnswer = true,
            Decision = new CheckpointDecision { Choice = CheckpointChoice.Approve },
        };
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, "01-tag-entity"), gate);

        Assert.Equal(CheckpointSessionOutcome.Approved, result.Outcome);
        Assert.Equal(["confirm-verify", "decide"], gate.Calls);
        Assert.Equal(1, gate.DecisionContext!.Verification!.Record!.Attempt);
        Assert.Matches("^[0-9a-f]{40}$", result.CommitSha);
    }

    [Fact]
    public async Task A_confirmed_pending_verification_that_stops_early_is_incomplete()
    {
        CommitOrcaIgnore();
        await RunUntilVerificationIncomplete();

        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.RateLimited, sideEffect: null); // still rate-limited
        var gate = new ScriptedGate { VerifyPendingAnswer = true };
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, "01-tag-entity"), gate);

        Assert.Equal(CheckpointSessionOutcome.VerificationIncomplete, result.Outcome);
        Assert.Equal(["confirm-verify"], gate.Calls); // the decision is never reached
        Assert.Null(result.Run);
        Assert.Equal("reconciler", result.Verification!.FailureRole);
    }

    [Fact]
    public async Task Cancellation_maps_to_the_interrupted_outcome()
    {
        CommitOrcaIgnore();
        using var cts = new CancellationTokenSource();
        var executor = new ScriptedExecutor { CancelBeforeReturning = cts };
        executor.Enqueue(ScriptedExecutor.Success,
            () => _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n"));
        var gate = new ScriptedGate();
        var session = Session(executor);

        var result = await session.ExecuteAsync(session.Prepare(null, null), gate, observer: null, cts.Token);

        Assert.Equal(CheckpointSessionOutcome.Interrupted, result.Outcome);
        Assert.Empty(gate.Calls);
        // Run state was written before the invocation — the resume machinery has its
        // anchor and the next session picks the attempt back up.
        Assert.NotNull(new RunStateStore(_repo.Root).Read("recipe-tagging"));
    }

    /// <summary>Attempt 1 that ended VerificationIncomplete: Implementer changes on the
    /// checkpoint branch, run state written, no verification record — the resumable
    /// pending state every resume test starts from.</summary>
    private async Task RunUntilVerificationIncomplete()
    {
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success,
            () => _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n"));
        executor.Enqueue(ScriptedExecutor.RateLimited, sideEffect: null);
        var session = Session(executor);
        var result = await session.ExecuteAsync(session.Prepare(null, null), new ScriptedGate());
        Assert.Equal(CheckpointSessionOutcome.VerificationIncomplete, result.Outcome);
    }

    /// <summary>Answers only what a test scripted; any unscripted consultation is a
    /// test failure — the session must never ask the human more than the flow says.</summary>
    private sealed class ScriptedGate : ICheckpointGate
    {
        public bool? DiscardAnswer { get; init; }
        public bool? VerifyPendingAnswer { get; init; }
        public CheckpointDecision? Decision { get; init; }

        /// <summary>Run immediately before DecideAsync returns its scripted decision — a
        /// hook for tests that need to mutate the workspace between the decision moment
        /// and the runner acting on it (e.g. simulating Approve finding an unexpected
        /// branch).</summary>
        public Action? BeforeDecisionReturns { get; init; }

        public List<string> Calls { get; } = [];
        public CheckpointDecisionContext? DiscardContext { get; private set; }
        public CheckpointDecisionContext? DecisionContext { get; private set; }
        public int? PendingAttemptAsked { get; private set; }

        public Task<bool> ConfirmDiscardAfterFailureAsync(CheckpointDecisionContext context, CancellationToken ct)
        {
            Calls.Add("confirm-discard");
            DiscardContext = context;
            return Task.FromResult(DiscardAnswer
                ?? throw new InvalidOperationException("gate asked about discard without a scripted answer"));
        }

        public Task<bool> ConfirmVerifyPendingAsync(int pendingAttempt, CancellationToken ct)
        {
            Calls.Add("confirm-verify");
            PendingAttemptAsked = pendingAttempt;
            return Task.FromResult(VerifyPendingAnswer
                ?? throw new InvalidOperationException("gate asked about pending verification without a scripted answer"));
        }

        public Task<CheckpointDecision> DecideAsync(CheckpointDecisionContext context, CancellationToken ct)
        {
            Calls.Add("decide");
            DecisionContext = context;
            BeforeDecisionReturns?.Invoke();
            return Task.FromResult(Decision
                ?? throw new InvalidOperationException("gate asked to decide without a scripted decision"));
        }
    }

    private sealed class ScriptedExecutor : IClaudeExecutor
    {
        public int Invocations { get; private set; }

        /// <summary>When set, the token source is cancelled before the (first) result
        /// returns — simulating Ctrl-C landing mid-invocation, after which the real
        /// executor throws for the caller's cancelled token.</summary>
        public CancellationTokenSource? CancelBeforeReturning { get; init; }

        private readonly Queue<(ClaudeExecutionResult Result, Action? SideEffect)> _script = new();

        public void Enqueue(ClaudeExecutionResult result, Action? sideEffect) =>
            _script.Enqueue((result, sideEffect));

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            if (_script.Count == 0)
                throw new InvalidOperationException("fake executor invoked more times than scripted");
            var (result, sideEffect) = _script.Dequeue();
            sideEffect?.Invoke();

            if (CancelBeforeReturning is { } cts)
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(result);
        }

        public static ClaudeExecutionResult Success { get; } = new()
        {
            Outcome = ExecutionOutcome.Completed,
            ExitCode = 0,
            SessionId = "fake-session",
            Result = Summary(isError: false),
            Elapsed = TimeSpan.FromSeconds(2),
        };

        /// <summary>A refusal-style failure: process completed, result event says error.</summary>
        public static ClaudeExecutionResult Failed { get; } = new()
        {
            Outcome = ExecutionOutcome.Completed,
            ExitCode = 1,
            SessionId = "fake-session",
            Result = Summary(isError: true),
            Elapsed = TimeSpan.FromSeconds(1),
        };

        public static ClaudeExecutionResult RateLimited { get; } = new()
        {
            Outcome = ExecutionOutcome.Completed,
            ExitCode = 1,
            SessionId = "fake-session",
            Result = Summary(isError: true),
            LastRateLimit = new RateLimitInfo { Status = "rejected", ResetsAt = 1783361400 },
            Elapsed = TimeSpan.FromSeconds(1),
        };

        private static ClaudeResultSummary Summary(bool isError) => new()
        {
            Subtype = "success",
            IsError = isError,
            SessionId = "fake-session",
            NumTurns = 3,
            DurationMs = 1500,
            TotalCostUsd = 0.25m,
            Usage = new Orca.Core.Domain.TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 50,
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0,
            },
            ResultText = isError ? "something went wrong" : null,
        };
    }
}
