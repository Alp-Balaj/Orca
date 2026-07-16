using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>Collects every §3.2 run observation in arrival order — the assertion
/// surface for the role-tagged progress seam.</summary>
internal sealed class RecordingRunObserver : IRunObserver
{
    public List<RunObservation> Events { get; } = [];

    public void OnRunEvent(RunObservation observation) => Events.Add(observation);

    /// <summary>The sequence without per-event stream noise — lifecycle assertions
    /// shouldn't break when a fake emits more or fewer stream events.</summary>
    public List<RunObservation> Lifecycle =>
        [.. Events.Where(e => e is not RunObservation.RoleStreamEvent)];
}

/// <summary>The Implementer's run observations (§3.2): branch fact, role lifecycle, and
/// role-tagged stream forwarding — emitted by CheckpointRunner, which knows what the
/// executor cannot.</summary>
public sealed class CheckpointRunnerObserverTests : IDisposable
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
        ---
        Plan body.
        """;

    private readonly TempGitRepo _repo = new();
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-observer-vault-").FullName;

    public CheckpointRunnerObserverTests()
    {
        var planDir = Path.Combine(_vault, "Plans", "recipe-tagging");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "recipe-tagging.md"), PlanText);

        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");
    }

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_vault, recursive: true);
    }

    private OrcaConfig Config() => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = 1,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = [],
        RoleSections = new RolesSection
        {
            Implementer = new RoleSection { AllowedTools = ["Read", "Edit"], MaxTurns = 10, TimeoutSeconds = 300 },
            Reconciler = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
            Verifier = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
        },
    };

    [Fact]
    public async Task Run_emits_branch_and_role_lifecycle_in_order_with_the_tagged_stream()
    {
        var observer = new RecordingRunObserver();
        var executor = new StreamingFakeExecutor(_repo);
        var runner = new CheckpointRunner(Config(), GitWorkspace.Discover(_repo.Root), executor);
        var preview = runner.Prepare(null, null);

        var outcome = await runner.RunAsync(preview, observer);

        Assert.Collection(observer.Events,
            e =>
            {
                var branch = Assert.IsType<RunObservation.BranchReady>(e);
                Assert.Equal("orca/recipe-tagging/01-tag-entity", branch.Branch);
                Assert.True(branch.Created);
            },
            e =>
            {
                var started = Assert.IsType<RunObservation.RoleStarted>(e);
                Assert.Equal(RoleName.Implementer, started.Role);
                Assert.Equal(1, started.Attempt);
                Assert.Equal(1, started.Invocation);
                Assert.Equal(preview.LogPaths.EventLog, started.EventLogPath);
            },
            e =>
            {
                var stream = Assert.IsType<RunObservation.RoleStreamEvent>(e);
                Assert.Equal(RoleName.Implementer, stream.Role);
                Assert.Equal("assistant", stream.Event.Type);
            },
            e =>
            {
                var finished = Assert.IsType<RunObservation.RoleFinished>(e);
                Assert.Equal(RoleName.Implementer, finished.Role);
                Assert.Same(outcome.Execution, finished.Execution);
            });
    }

    [Fact]
    public async Task A_second_attempt_reports_the_branch_as_reused()
    {
        var executor = new StreamingFakeExecutor(_repo);
        var runner = new CheckpointRunner(Config(), GitWorkspace.Discover(_repo.Root), executor);
        var first = await runner.RunAsync(runner.Prepare(null, null));
        runner.Approve(first.Preview, null);

        var observer = new RecordingRunObserver();
        await runner.RunAsync(runner.Prepare(null, "01-tag-entity", overrideCompleteByEvidence: true), observer);

        var branch = Assert.IsType<RunObservation.BranchReady>(observer.Events[0]);
        Assert.False(branch.Created);
        var started = Assert.IsType<RunObservation.RoleStarted>(observer.Lifecycle[1]);
        Assert.Equal(2, started.Attempt);
        Assert.Equal(1, started.Invocation);
    }

    /// <summary>Implementer stand-in that also exercises the step-1 stream seam: one
    /// parsed event is pushed through the observer the runner hands it.</summary>
    private sealed class StreamingFakeExecutor(TempGitRepo repo) : IClaudeExecutor
    {
        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            observer?.OnStreamEvent(new ClaudeStreamEvent
            {
                Type = "assistant",
                RawLine = """{"type":"assistant"}""",
            });
            repo.WriteFile("src/Tag.cs", "public sealed record Tag(string Name);\n");
            return Task.FromResult(new ClaudeExecutionResult
            {
                Outcome = ExecutionOutcome.Completed,
                ExitCode = 0,
                SessionId = "fake-session-id",
                Result = new ClaudeResultSummary
                {
                    Subtype = "success",
                    IsError = false,
                    SessionId = "fake-session-id",
                    NumTurns = 3,
                    DurationMs = 1200,
                    TotalCostUsd = 0.25m,
                    Usage = new TokenUsage
                    {
                        InputTokens = 100,
                        OutputTokens = 50,
                        CacheCreationInputTokens = 0,
                        CacheReadInputTokens = 0,
                    },
                },
                Elapsed = TimeSpan.FromSeconds(1.2),
            });
        }
    }
}

/// <summary>The verification pipeline's run observations (§3.2): gates as they run,
/// Reconciler/Verifier lifecycles with §5.4 invocation numbering, parse-retry entry,
/// and canonical records as they land.</summary>
public sealed class CheckpointVerifierObserverTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-observer-vault-").FullName;

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

    public CheckpointVerifierObserverTests()
    {
        _repo.WriteFile(".gitignore", ".orca/\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-m", "ignore .orca");
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
        var sh = Path.Combine(_repo.Root, $"gate-{exitCode}.sh");
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_repo.Root, $"gate-{exitCode}.cmd");
            if (!File.Exists(cmd))
                File.WriteAllText(cmd, $"@echo off\r\necho gate-output-{exitCode}\r\nexit /b {exitCode}\r\n");
            return [cmd];
        }

        if (!File.Exists(sh))
        {
            File.WriteAllText(sh, $"#!/bin/sh\necho gate-output-{exitCode}\nexit {exitCode}\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return [sh];
    }

    private OrcaConfig Config(int buildExit = 0, int maxParseRetries = 1) => new()
    {
        RepoConfigPath = Path.Combine(_repo.Root, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo.Root, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = GateScript(buildExit),
        TestCommand = GateScript(0),
        GateTimeout = null,
        MaxParseRetries = maxParseRetries,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = [],
        RoleSections = new RolesSection
        {
            Reconciler = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
            Verifier = new RoleSection { AllowedTools = ["Read", "Write"], MaxTurns = 10, TimeoutSeconds = 300 },
        },
    };

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

    [Fact]
    public async Task The_full_pipeline_narrates_gates_roles_and_records_in_order()
    {
        var observer = new RecordingRunObserver();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(ScriptedExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));
        var config = Config();
        var verifier = new CheckpointVerifier(config, GitWorkspace.Discover(_repo.Root), executor);
        var logLayout = new RunLogLayout(_repo.Root);
        var layout = new VaultLayout(_vault, "Plans");

        await verifier.RunAsync(Plan, Checkpoint01, attempt: 1, observer);

        Assert.Collection(observer.Lifecycle,
            e =>
            {
                var gate = Assert.IsType<RunObservation.GateStarted>(e);
                Assert.Equal("build", gate.Gate);
                Assert.Equal(config.BuildCommand, gate.Command);
            },
            e => Assert.Equal(GateOutcome.Pass, Assert.IsType<RunObservation.GateFinished>(e).Result.Outcome),
            e => Assert.Equal("tests", Assert.IsType<RunObservation.GateStarted>(e).Gate),
            e => Assert.Equal(GateOutcome.Pass, Assert.IsType<RunObservation.GateFinished>(e).Result.Outcome),
            e =>
            {
                var started = Assert.IsType<RunObservation.RoleStarted>(e);
                Assert.Equal(RoleName.Reconciler, started.Role);
                Assert.Equal(1, started.Attempt);
                Assert.Equal(1, started.Invocation);
                Assert.Equal(
                    logLayout.RoleAttemptPaths("recipe-tagging", "01-tag-entity", 1, "reconciler", 1).EventLog,
                    started.EventLogPath);
            },
            e => Assert.Equal(RoleName.Reconciler, Assert.IsType<RunObservation.RoleFinished>(e).Role),
            e =>
            {
                var record = Assert.IsType<RunObservation.VaultRecordWritten>(e);
                Assert.Equal("reconciliation", record.Kind);
                Assert.Equal(layout.ReconciliationFile("recipe-tagging", "01-tag-entity", 1), record.Path);
            },
            e =>
            {
                var started = Assert.IsType<RunObservation.RoleStarted>(e);
                Assert.Equal(RoleName.Verifier, started.Role);
                Assert.Equal(1, started.Invocation);
            },
            e => Assert.Equal(RoleName.Verifier, Assert.IsType<RunObservation.RoleFinished>(e).Role),
            e =>
            {
                var record = Assert.IsType<RunObservation.VaultRecordWritten>(e);
                Assert.Equal("verification", record.Kind);
                Assert.Equal(layout.VerificationFile("recipe-tagging", "01-tag-entity", 1), record.Path);
            });
    }

    [Fact]
    public async Task A_parse_retry_is_announced_with_the_error_and_the_next_invocation_number()
    {
        var observer = new RecordingRunObserver();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation(attempt: 9)));
        executor.Enqueue(ScriptedExecutor.Success, () => WriteStaging(StagingReconciliation(), ValidReconciliation()));
        executor.Enqueue(ScriptedExecutor.Success, () => WriteStaging(StagingSemantic(), ValidChecklist()));
        var verifier = new CheckpointVerifier(Config(), GitWorkspace.Discover(_repo.Root), executor);

        await verifier.RunAsync(Plan, Checkpoint01, attempt: 1, observer);

        var retry = Assert.Single(observer.Events.OfType<RunObservation.RoleParseRetry>());
        Assert.Equal(RoleName.Reconciler, retry.Role);
        Assert.Contains("'attempt' must be exactly '1'", retry.Error);
        Assert.Equal(2, retry.NextInvocation);

        var reconcilerStarts = observer.Events.OfType<RunObservation.RoleStarted>()
            .Where(s => s.Role == RoleName.Reconciler).ToList();
        Assert.Equal([1, 2], reconcilerStarts.Select(s => s.Invocation));
    }

    [Fact]
    public async Task A_failed_gate_skips_the_roles_but_still_reports_the_record()
    {
        var observer = new RecordingRunObserver();
        var executor = new ScriptedExecutor();
        var verifier = new CheckpointVerifier(Config(buildExit: 1), GitWorkspace.Discover(_repo.Root), executor);

        await verifier.RunAsync(Plan, Checkpoint01, attempt: 1, observer);

        Assert.Empty(observer.Events.OfType<RunObservation.RoleStarted>());
        var record = Assert.Single(observer.Events.OfType<RunObservation.VaultRecordWritten>());
        Assert.Equal("verification", record.Kind);
        Assert.Equal(4, observer.Events.OfType<RunObservation.GateStarted>().Count()
            + observer.Events.OfType<RunObservation.GateFinished>().Count());
    }

    private sealed class ScriptedExecutor : IClaudeExecutor
    {
        private readonly Queue<(ClaudeExecutionResult Result, Action? SideEffect)> _script = new();

        public void Enqueue(ClaudeExecutionResult result, Action? sideEffect) =>
            _script.Enqueue((result, sideEffect));

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
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
            Result = new ClaudeResultSummary
            {
                Subtype = "success",
                IsError = false,
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
            },
            Elapsed = TimeSpan.FromSeconds(2),
        };
    }
}

/// <summary>The Planner's run observations (§3.2): for this role each §5.4 parse retry
/// advances the planning attempt itself, so attempt and invocation coincide — both
/// match the planning.attempt-&lt;n&gt; run-log stem.</summary>
public sealed class PlanningObserverTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("orca-observer-repo-").FullName;
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-observer-vault-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
        Directory.Delete(_vault, recursive: true);
    }

    private OrcaConfig Config() => new()
    {
        RepoConfigPath = Path.Combine(_repo, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = 1,
        MaxCheckpointAttempts = null,
        PlanningCandidates = null,
        DeniedTools = [],
        RoleSections = new RolesSection
        {
            Planner = new RoleSection { AllowedTools = ["Read", "Write", "Edit"], MaxTurns = 30, TimeoutSeconds = 600 },
        },
    };

    private static PlanningRequest Request() => new()
    {
        PlanId = "recipe-tagging",
        Goal = "Add tag support to recipes",
        Title = null,
        TargetRepoName = "RecipeApp",
        BaseBranch = "main",
    };

    private string PlanPath => Path.Combine(_vault, "Plans", "recipe-tagging", "recipe-tagging.md");

    private static string ValidPlanText() => """
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        title: "Recipe tagging"
        status: draft
        target_repo: "RecipeApp"
        base_branch: "main"
        created: 2026-07-06T21:10:00Z
        updated: 2026-07-06T21:10:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-tag-entity
            description: Add the Tag entity
            status: pending
            acceptance_criteria:
              - Tag entity exists
        ---

        Generated by the fake planner.
        """;

    private void WritePlanFile(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlanPath)!);
        File.WriteAllText(PlanPath, text);
    }

    [Fact]
    public async Task A_successful_generation_narrates_the_planner_and_the_landed_plan()
    {
        var observer = new RecordingRunObserver();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success, () => WritePlanFile(ValidPlanText()));
        var strategy = new SingleCandidatePlanningStrategy(Config(), executor, _repo);
        var preview = strategy.Prepare(Request());

        await strategy.GenerateAsync(preview, observer);

        Assert.Collection(observer.Lifecycle,
            e =>
            {
                var started = Assert.IsType<RunObservation.RoleStarted>(e);
                Assert.Equal(RoleName.Planner, started.Role);
                Assert.Equal(1, started.Attempt);
                Assert.Equal(1, started.Invocation);
                Assert.Equal(preview.LogPaths.EventLog, started.EventLogPath);
            },
            e => Assert.Equal(RoleName.Planner, Assert.IsType<RunObservation.RoleFinished>(e).Role),
            e =>
            {
                var record = Assert.IsType<RunObservation.VaultRecordWritten>(e);
                Assert.Equal("plan", record.Kind);
                Assert.Equal(preview.PlanFilePath, record.Path);
            });
    }

    [Fact]
    public async Task A_parse_retry_advances_attempt_and_invocation_together()
    {
        var observer = new RecordingRunObserver();
        var executor = new ScriptedExecutor();
        executor.Enqueue(ScriptedExecutor.Success, () => WritePlanFile("not even frontmatter"));
        executor.Enqueue(ScriptedExecutor.Success, () => WritePlanFile(ValidPlanText()));
        var strategy = new SingleCandidatePlanningStrategy(Config(), executor, _repo);

        await strategy.GenerateAsync(strategy.Prepare(Request()), observer);

        var retry = Assert.Single(observer.Events.OfType<RunObservation.RoleParseRetry>());
        Assert.Equal(RoleName.Planner, retry.Role);
        Assert.Equal(2, retry.NextInvocation);

        var starts = observer.Events.OfType<RunObservation.RoleStarted>().ToList();
        Assert.Equal([(1, 1), (2, 2)], starts.Select(s => (s.Attempt, s.Invocation)));
    }

    private sealed class ScriptedExecutor : IClaudeExecutor
    {
        private readonly Queue<(ClaudeExecutionResult Result, Action? SideEffect)> _script = new();

        public void Enqueue(ClaudeExecutionResult result, Action? sideEffect) =>
            _script.Enqueue((result, sideEffect));

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
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
            Result = new ClaudeResultSummary
            {
                Subtype = "success",
                IsError = false,
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
            },
            Elapsed = TimeSpan.FromSeconds(2),
        };
    }
}
