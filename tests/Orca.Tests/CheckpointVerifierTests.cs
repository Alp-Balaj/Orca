using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>The step-7 pipeline against a fake executor and stand-in gate commands:
/// gates-first ordering, the skip-LLM-on-gate-failure rule, §5.4 staging + Orca
/// re-serialization, capped Reflexion retries, the no-auto-retry rule for
/// invocation-level failures, and record-based resume. No process spawns beyond the
/// stand-in gate scripts, no quota.</summary>
public sealed class CheckpointVerifierTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-verifier-vault-").FullName;
    private readonly TimeProvider _clock = TimeProvider.System;

    private static readonly Checkpoint Checkpoint01 = new(
        "01-tag-entity",
        "Add Tag entity + EF mapping",
        CheckpointStatus.Pending,
        ["Tag entity exists", "EF mapping compiles"]);

    private static readonly Plan Plan = new(
        1, "recipe-tagging", 1, "Recipe tagging", PlanStatus.InProgress, "RecipeApp", "main",
        DateTimeOffset.Parse("2026-07-06T14:00:00Z"), DateTimeOffset.Parse("2026-07-06T14:00:00Z"),
        new PlanningInfo(PlanningStrategyKind.SingleCandidate, null, null, null),
        [Checkpoint01],
        "Plan body.");

    public CheckpointVerifierTests()
    {
        // .orca/ must be ignored so run logs written mid-pipeline never appear in the
        // diff the roles judge — same precondition checkpoint-run enforces for real.
        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");

        // The Implementer's uncommitted work: one tracked change, one new file.
        _repo.WriteFile("README.md", "seed\nchanged by implementer\n");
        _repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n");

        Directory.CreateDirectory(Path.Combine(_vault, "Plans", "recipe-tagging"));
    }

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_vault, recursive: true);
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

    private OrcaConfig Config(int buildExit = 0, int testsExit = 0, int maxParseRetries = 1) => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = GateScript(buildExit),
        TestCommand = GateScript(testsExit),
        GateTimeout = null,
        MaxParseRetries = maxParseRetries,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = ["Bash(git push*)"],
        RoleSections = new RolesSection
        {
            Reconciler = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
            Verifier = new RoleSection { AllowedTools = ["Read", "Write", "Edit"], MaxTurns = 10, TimeoutSeconds = 300 },
        },
    };

    private CheckpointVerifier Verifier(IClaudeExecutor executor, OrcaConfig config) =>
        new(config, GitWorkspace.Discover(_repo.Root), executor, _clock);

    private string StagingReconciliation(int attempt = 1) => Path.Combine(
        _vault, "Plans", "recipe-tagging", ".staging", $"01-tag-entity.attempt-{attempt}.reconciliation.md");

    private string StagingSemantic(int attempt = 1) => Path.Combine(
        _vault, "Plans", "recipe-tagging", ".staging", $"01-tag-entity.attempt-{attempt}.semantic.md");

    private void WriteStaging(string path, string content)
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

    private static string ValidChecklist(int attempt = 1, string secondVerdict = "pass", string? secondNote = null) => $"""
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
            verdict: {secondVerdict}{(secondNote is null ? "" : $"\n    note: \"{secondNote}\"")}
        ---
        Entity looks right.
        """;

    [Fact]
    public async Task A_failed_gate_writes_the_record_and_skips_both_llm_roles()
    {
        var executor = new FakeRoleExecutor();
        var config = Config(buildExit: 1);

        var outcome = await Verifier(executor, config).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.Empty(executor.Invocations);
        Assert.NotNull(outcome.Record);
        Assert.Equal(VerificationVerdict.Fail, outcome.Record.Verdict);
        Assert.Null(outcome.Record.Semantic);
        Assert.Equal(GateOutcome.Fail, outcome.Record.Gates.Build);
        Assert.Equal(GateOutcome.Pass, outcome.Record.Gates.Tests);
        Assert.Null(outcome.Reconciliation);

        // The record body excerpts the failing gate and points at the full log.
        Assert.Contains("gate-output-1", outcome.Record.Body);
        Assert.Contains(".orca/runs/recipe-tagging/01-tag-entity.attempt-1.build.txt", outcome.Record.Body);

        // Written where the vault schema says, parseable by the strict reader.
        var records = new VaultReader(new VaultLayout(_vault, "Plans"))
            .ReadVerificationRecords("recipe-tagging", "01-tag-entity");
        Assert.Single(records);
    }

    [Fact]
    public async Task The_full_pipeline_composes_the_record_and_cleans_staging()
    {
        var executor = new FakeRoleExecutor();
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));
        var config = Config();

        var outcome = await Verifier(executor, config).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.NotNull(outcome.Record);
        Assert.Equal(VerificationVerdict.Pass, outcome.Record.Verdict);
        Assert.Equal(2, outcome.Record.Semantic!.Count);
        Assert.Contains("Entity looks right.", outcome.Record.Body);
        Assert.NotNull(outcome.Reconciliation);
        Assert.Equal(ReconciliationVerdict.Full, outcome.Reconciliation.ImplementsPlan);

        // BF-19: a green record still carries a tree fingerprint (Approve checks it later)
        // and, unlike before, its gates' own output — not just a pointer to the ephemeral,
        // gitignored run log, which does not reliably survive.
        Assert.Equal(GitWorkspace.Discover(_repo.Root).WorkingTreeHash(), outcome.Record.WorkingTreeHash);
        Assert.Contains("### build output (last 60 lines)", outcome.Record.Body);
        Assert.Contains("### tests output (last 60 lines)", outcome.Record.Body);
        Assert.Contains("gate-output-0", outcome.Record.Body);

        // Staging is gone; the canonical, Orca-serialized records remain.
        Assert.False(File.Exists(StagingReconciliation()));
        Assert.False(File.Exists(StagingSemantic()));
        var layout = new VaultLayout(_vault, "Plans");
        Assert.True(File.Exists(layout.ReconciliationFile("recipe-tagging", "01-tag-entity", 1)));
        Assert.True(File.Exists(layout.VerificationFile("recipe-tagging", "01-tag-entity", 1)));

        // Both invocations are vault-scoped with their own role's settings.
        Assert.Equal(2, executor.Invocations.Count);
        Assert.All(executor.Invocations, i => Assert.Equal(_vault, i.Options.WorkingDirectory));
        Assert.Equal(["Read", "Write"], executor.Invocations[0].Options.AllowedTools);
        Assert.Equal(["Read", "Write", "Edit"], executor.Invocations[1].Options.AllowedTools);

        // The reconciler judges diff-vs-plan-section; the verifier judges criteria over
        // diff + gate evidence.
        var reconcilerPrompt = executor.Invocations[0].Options.Prompt;
        Assert.Contains("01-tag-entity: Add Tag entity + EF mapping", reconcilerPrompt);
        Assert.Contains("changed by implementer", reconcilerPrompt);
        Assert.Contains("## New file (untracked): src/Tag.cs", reconcilerPrompt);
        var verifierPrompt = executor.Invocations[1].Options.Prompt;
        Assert.Contains("- criterion: \"Tag entity exists\"", verifierPrompt);
        Assert.Contains("- criterion: \"EF mapping compiles\"", verifierPrompt);
        Assert.Contains("gate-output-0", verifierPrompt);
    }

    [Fact]
    public async Task A_non_pass_criterion_fails_the_composed_record()
    {
        var executor = new FakeRoleExecutor();
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(
            StagingSemantic(), ValidChecklist(secondVerdict: "unclear", secondNote: "no migration visible")));

        var outcome = await Verifier(executor, Config()).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.Equal(VerificationVerdict.Fail, outcome.Record!.Verdict);
        Assert.Equal(SemanticVerdict.Unclear, outcome.Record.Semantic![1].Verdict);
    }

    [Fact]
    public async Task A_parse_failure_retries_with_the_exact_error_and_the_file_left_in_place()
    {
        var executor = new FakeRoleExecutor();
        // First reconciler output claims the wrong attempt — identity violation.
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation(attempt: 9)));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));

        var outcome = await Verifier(executor, Config()).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.NotNull(outcome.Record);
        Assert.Equal(3, executor.Invocations.Count);
        var retryPrompt = executor.Invocations[1].Options.Prompt;
        Assert.Contains("Retry — your previous output failed validation", retryPrompt);
        Assert.Contains("'attempt' must be exactly '1'", retryPrompt);
    }

    [Fact]
    public async Task Exhausted_retries_surface_and_leave_the_staging_file_for_inspection()
    {
        var executor = new FakeRoleExecutor();
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), "not even frontmatter"));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), "still not frontmatter"));

        var outcome = await Verifier(executor, Config(maxParseRetries: 1)).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.Null(outcome.Record);
        Assert.Contains("exhausting maxParseRetries=1", outcome.FailureDetail);
        Assert.Equal("reconciler", outcome.FailureRole);
        Assert.Null(outcome.FailureStopReason);
        Assert.True(File.Exists(StagingReconciliation()));
        Assert.False(File.Exists(new VaultLayout(_vault, "Plans")
            .ReconciliationFile("recipe-tagging", "01-tag-entity", 1)));
    }

    [Fact]
    public async Task The_staging_directory_exists_before_the_reconciler_is_invoked()
    {
        // Claude Code's sandbox refuses to mkdir or write into a directory that doesn't
        // already exist under its working directory in a non-interactive session — Orca,
        // not the model, must guarantee `.staging` exists before the role runs.
        var stagingDir = Path.Combine(_vault, "Plans", "recipe-tagging", ".staging");
        var executor = new DirectoryCheckingExecutor(stagingDir);

        await Verifier(executor, Config()).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.True(executor.DirectoryExistedOnInvocation);
    }

    [Fact]
    public async Task An_invocation_level_failure_stops_loudly_with_no_auto_retry()
    {
        var executor = new FakeRoleExecutor();
        executor.Enqueue(FakeRoleExecutor.RateLimited, sideEffect: null);

        var outcome = await Verifier(executor, Config()).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.Single(executor.Invocations);
        Assert.Null(outcome.Record);
        Assert.Equal(InvocationStopReason.RateLimited, outcome.FailureStopReason);
        Assert.Equal("reconciler", outcome.FailureRole);
        // The gates already ran — their facts are not lost, just not yet recorded.
        Assert.NotNull(outcome.BuildGate);
        Assert.Equal(GateOutcome.Pass, outcome.BuildGate.Outcome);
    }

    [Fact]
    public async Task A_rerun_resumes_past_substeps_whose_records_already_exist()
    {
        // First run: reconciler succeeds, verifier gets rate-limited.
        var first = new FakeRoleExecutor();
        first.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        first.Enqueue(FakeRoleExecutor.RateLimited, sideEffect: null);
        var config = Config();
        var interrupted = await Verifier(first, config).RunAsync(Plan, Checkpoint01, attempt: 1);
        Assert.Null(interrupted.Record);
        Assert.Equal("verifier", interrupted.FailureRole);

        // Second run: only the verifier is re-invoked; the reconciliation record is reused.
        var second = new FakeRoleExecutor();
        second.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));
        var outcome = await Verifier(second, config).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.NotNull(outcome.Record);
        var invocation = Assert.Single(second.Invocations);
        Assert.Contains("copy each `criterion:` line verbatim", invocation.Options.Prompt);
        Assert.Equal(ReconciliationVerdict.Full, outcome.Reconciliation!.ImplementsPlan);
    }

    [Fact]
    public async Task A_preexisting_record_short_circuits_the_whole_pipeline()
    {
        var executor = new FakeRoleExecutor();
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));
        var config = Config();
        var verifier = Verifier(executor, config);
        var firstRun = await verifier.RunAsync(Plan, Checkpoint01, attempt: 1);
        Assert.NotNull(firstRun.Record);

        var rerun = await verifier.RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.True(rerun.RecordPreexisted);
        Assert.Equal(2, executor.Invocations.Count); // nothing new ran
        Assert.Null(rerun.BuildGate);
        Assert.Equal(firstRun.Record!.Verdict, rerun.Record!.Verdict);
        Assert.NotNull(rerun.Reconciliation);
        Assert.True(verifier.HasRecord("recipe-tagging", "01-tag-entity", 1));
    }

    [Fact]
    public async Task Role_invocations_accumulate_into_run_state_with_their_prompt_hashes()
    {
        var stateStore = new RunStateStore(_repo.Root);
        stateStore.Write(new RunState
        {
            SchemaVersion = 1,
            PlanId = "recipe-tagging",
            PlanVersion = 1,
            CheckpointIndex = 0,
            CheckpointId = "01-tag-entity",
            Attempt = 1,
            Branch = "orca/recipe-tagging/01-tag-entity",
            SessionId = "implementer-session",
            PromptHashes = new Dictionary<string, string> { ["implementer"] = "abc" },
            AccumulatedCostUsd = 1.00m,
            LastUsage = null,
            LastStopReason = InvocationStopReason.Succeeded,
            StartedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var executor = new FakeRoleExecutor();
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(FakeRoleExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));

        var outcome = await Verifier(executor, Config()).RunAsync(Plan, Checkpoint01, attempt: 1);

        Assert.Equal(0.50m, outcome.CostUsd); // 0.25 per fake invocation
        var state = stateStore.Read("recipe-tagging")!;
        Assert.Equal(1.50m, state.AccumulatedCostUsd);
        Assert.Equal("abc", state.PromptHashes["implementer"]);
        Assert.Equal(ReconcilerPrompt.RoleSha256, state.PromptHashes["reconciler"]);
        Assert.Equal(VerifierPrompt.RoleSha256, state.PromptHashes["verifier"]);
        Assert.Equal(InvocationStopReason.Succeeded, state.LastStopReason);
    }

    /// <summary>Records whether <paramref name="directory"/> already existed the moment
    /// it was invoked, then fails loudly — enough to probe the precondition without
    /// needing a parseable staging file.</summary>
    private sealed class DirectoryCheckingExecutor(string directory) : IClaudeExecutor
    {
        public bool DirectoryExistedOnInvocation { get; private set; }

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            DirectoryExistedOnInvocation = Directory.Exists(directory);
            return Task.FromResult(FakeRoleExecutor.RateLimited);
        }
    }

    /// <summary>Scripted <see cref="IClaudeExecutor"/> — the planner tests' fake, reused
    /// for the two verification roles.</summary>
    private sealed class FakeRoleExecutor : IClaudeExecutor
    {
        public List<(ClaudeInvocationOptions Options, RunLogPaths LogPaths)> Invocations { get; } = [];

        private readonly Queue<(ClaudeExecutionResult Result, Action? SideEffect)> _script = new();

        public void Enqueue(ClaudeExecutionResult result, Action? sideEffect) =>
            _script.Enqueue((result, sideEffect));

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            Invocations.Add((options, logPaths));
            if (_script.Count == 0)
                throw new InvalidOperationException("fake executor invoked more times than scripted");
            var (result, sideEffect) = _script.Dequeue();
            sideEffect?.Invoke();
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
            Usage = new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 50,
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0,
            },
            ResultText = isError ? "limit reached" : null,
        };
    }
}
