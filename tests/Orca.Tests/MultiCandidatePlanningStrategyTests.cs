using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Planning;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>SUG-13: candidates → critic → synthesis against a routing fake executor
/// (candidates run concurrently, so the fake routes by prompt content, never by call
/// order). No process spawns, no quota.</summary>
public sealed class MultiCandidatePlanningStrategyTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("orca-multi-repo-").FullName;
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-multi-vault-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
        Directory.Delete(_vault, recursive: true);
    }

    private OrcaConfig Config(int candidates = 2, int maxParseRetries = 1) => new()
    {
        RepoConfigPath = Path.Combine(_repo, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = maxParseRetries,
        MaxCheckpointAttempts = null,
        PlanningCandidates = candidates,
        DeniedTools = [],
        RoleSections = new RolesSection
        {
            Planner = new RoleSection { AllowedTools = ["Read", "Write", "Edit"], MaxTurns = 30, TimeoutSeconds = 600 },
        },
    };

    private static PlanningRequest Request() => new()
    {
        PlanId = "app-layer",
        Goal = "Pick and wire the application-layer pattern.",
        Title = null,
        TargetRepoName = "RecipeApp",
        BaseBranch = "main",
    };

    private static string CandidatePlan(string body) => $"""
        ---
        schema_version: 1
        plan_id: app-layer
        plan_version: 1
        title: "App layer"
        status: draft
        target_repo: "RecipeApp"
        base_branch: "main"
        created: 2026-07-06T21:10:00Z
        updated: 2026-07-06T21:10:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-seam
            description: Establish the seam
            status: pending
            acceptance_criteria:
              - seam exists
        ---

        {body}
        """;

    private const string CritiqueText = """
        ---
        schema_version: 1
        type: critique
        plan_id: app-layer
        selected: 2
        rationale: "Candidate 2 proves the risky seam first."
        ---

        ## Candidate 1

        Solid but defers the risky decision.

        ## Candidate 2

        Wins on sequencing.
        """;

    private const string FinalPlan = """
        ---
        schema_version: 1
        plan_id: app-layer
        plan_version: 1
        title: "App layer"
        status: draft
        target_repo: "RecipeApp"
        base_branch: "main"
        created: 2026-07-06T21:10:00Z
        updated: 2026-07-06T21:10:00Z
        planning:
          strategy: multi_candidate
          candidates: 2
          selected: 2
          rationale: "Candidate 2 proves the risky seam first."
        checkpoints:
          - id: 01-seam
            description: Establish the seam
            status: pending
            acceptance_criteria:
              - seam exists
        ---

        Synthesized from candidate 2, absorbing candidate 1's naming.
        """;

    /// <summary>Routes each invocation by markers in its prompt — order-independent, so
    /// concurrent candidate invocations can't race the script.</summary>
    private sealed class RoutingExecutor(Func<ClaudeInvocationOptions, Action?> route) : IClaudeExecutor
    {
        public List<ClaudeInvocationOptions> Invocations { get; } = [];
        private readonly Lock _sync = new();

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Invocations.Add(options);
            }

            route(options)?.Invoke();
            return Task.FromResult(new ClaudeExecutionResult
            {
                Outcome = ExecutionOutcome.Completed,
                ExitCode = 0,
                SessionId = "s",
                Result = new ClaudeResultSummary
                {
                    Subtype = "success",
                    IsError = false,
                    SessionId = "s",
                    NumTurns = 2,
                    DurationMs = 500,
                    TotalCostUsd = 0.10m,
                    Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1, CacheCreationInputTokens = 0, CacheReadInputTokens = 0 },
                },
                Elapsed = TimeSpan.FromSeconds(1),
            });
        }
    }

    private string StagingPath(params string[] parts) =>
        Path.Combine([_vault, "Plans", "app-layer", ".staging", .. parts]);

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Candidates_critic_and_synthesis_produce_the_final_multi_plan()
    {
        var executor = new RoutingExecutor(options =>
        {
            if (options.Prompt.Contains("candidate 1 of 2"))
                return () => Write(StagingPath("candidate-1", "app-layer.md"), CandidatePlan("Simplest framing."));
            if (options.Prompt.Contains("candidate 2 of 2"))
                return () => Write(StagingPath("candidate-2", "app-layer.md"), CandidatePlan("Testable framing."));
            if (options.Prompt.Contains("# Orca Critic"))
                return () => Write(StagingPath("critique.md"), CritiqueText);
            if (options.Prompt.Contains("## Synthesis"))
                return () => Write(Path.Combine(_vault, "Plans", "app-layer", "app-layer.md"), FinalPlan);
            throw new InvalidOperationException("unroutable prompt");
        });
        var strategy = new MultiCandidatePlanningStrategy(Config(), executor, _repo);

        var outcome = await strategy.GenerateAsync(strategy.Prepare(Request()));

        Assert.Null(outcome.FailureDetail);
        Assert.Equal(PlanningRunStatus.Succeeded, outcome.State.Status);
        Assert.Equal(PlanningStrategyKind.MultiCandidate, outcome.Plan!.Planning.Strategy);
        Assert.Equal(2, outcome.Plan.Planning.Selected);

        Assert.Equal(4, executor.Invocations.Count); // 2 candidates + critic + synthesis
        Assert.Equal(0.40m, outcome.State.AccumulatedCostUsd);
        Assert.Equal(
            ["candidate-1", "candidate-2", "critic", "synthesis"],
            outcome.State.Attempts.Select(a => a.Role).Order(StringComparer.Ordinal));

        // Diversity comes from framing: the two candidate prompts carry different lenses.
        var candidatePrompts = executor.Invocations.Where(i => i.Prompt.Contains("Candidate framing")).ToList();
        Assert.Contains(candidatePrompts, p => p.Prompt.Contains("SIMPLEST POSSIBLE"));
        Assert.Contains(candidatePrompts, p => p.Prompt.Contains("TESTABILITY"));

        // The synthesis prompt names the winner and pins the provenance block.
        var synthesis = executor.Invocations.Single(i => i.Prompt.Contains("## Synthesis"));
        Assert.Contains("Candidate 2 won the critique", synthesis.Prompt);
        Assert.Contains("selected: 2", synthesis.Prompt);
    }

    [Fact]
    public async Task A_critic_verdict_out_of_range_burns_its_retries_then_fails_loudly()
    {
        var badCritique = CritiqueText.Replace("selected: 2", "selected: 7");
        var executor = new RoutingExecutor(options =>
        {
            if (options.Prompt.Contains("candidate 1 of 2"))
                return () => Write(StagingPath("candidate-1", "app-layer.md"), CandidatePlan("A."));
            if (options.Prompt.Contains("candidate 2 of 2"))
                return () => Write(StagingPath("candidate-2", "app-layer.md"), CandidatePlan("B."));
            if (options.Prompt.Contains("# Orca Critic"))
                return () => Write(StagingPath("critique.md"), badCritique);
            throw new InvalidOperationException("unroutable prompt");
        });
        var strategy = new MultiCandidatePlanningStrategy(Config(maxParseRetries: 1), executor, _repo);

        var outcome = await strategy.GenerateAsync(strategy.Prepare(Request()));

        Assert.Equal(PlanningRunStatus.Failed, outcome.State.Status);
        Assert.Contains("exhausting maxParseRetries", outcome.FailureDetail);
        Assert.Contains("'selected' must be a 1-based index between 1 and 2", outcome.FailureDetail);
        // The retry carried the exact error back to the critic.
        Assert.Contains(executor.Invocations,
            i => i.Prompt.Contains("# Orca Critic") && i.Prompt.Contains("failed validation"));
    }
}
