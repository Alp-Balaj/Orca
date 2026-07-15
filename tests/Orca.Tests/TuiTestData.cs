using Orca.Core.Execution;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>Builders for the pure TUI-projection tests (TuiTreeModel / TuiText). The
/// projections read <see cref="StatusReport"/> directly, so these construct the domain
/// records in memory — no git repo or vault needed.</summary>
internal static class TuiTestData
{
    public static Checkpoint MakeCheckpoint(
        string id, CheckpointStatus status, string description = "desc", params string[] criteria)
        => new(id, description, status, criteria.Length == 0 ? ["it works"] : criteria);

    public static Plan MakePlan(
        string id, PlanStatus status, IReadOnlyList<Checkpoint> checkpoints,
        string title = "A Plan", int version = 1)
        => new(1, id, version, title, status, "RecipeApp", "main",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new PlanningInfo(PlanningStrategyKind.SingleCandidate, null, null, null),
            checkpoints, "Body.\n");

    public static RunState MakeRunState(
        string planId, string checkpointId, int attempt = 1, decimal cost = 0m,
        InvocationStopReason? stopReason = null)
        => new()
        {
            SchemaVersion = 1,
            PlanId = planId,
            PlanVersion = 1,
            CheckpointIndex = 0,
            CheckpointId = checkpointId,
            Attempt = attempt,
            Branch = $"orca/{planId}/{checkpointId}",
            SessionId = null,
            PromptHashes = new Dictionary<string, string>(),
            AccumulatedCostUsd = cost,
            LastUsage = null,
            LastStopReason = stopReason,
            StartedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    public static PlanStatusEntry MakeEntry(
        Plan plan, RunState? state = null,
        IReadOnlyDictionary<string, CheckpointEvidence>? evidence = null)
        => new() { Plan = plan, RunState = state, Evidence = evidence ?? EmptyEvidence(plan) };

    public static IReadOnlyDictionary<string, CheckpointEvidence> EmptyEvidence(Plan plan)
        => plan.Checkpoints.ToDictionary(
            c => c.Id,
            _ => new CheckpointEvidence { LatestVerification = null, LatestReconciliation = null },
            StringComparer.Ordinal);

    public static StatusReport MakeReport(
        bool workingTreeClean = true, params PlanStatusEntry[] plans)
        => new()
        {
            RepoRoot = "/repo",
            CurrentBranch = "main",
            WorkingTreeClean = workingTreeClean,
            VaultRoot = "/vault",
            PlansDir = "Plans",
            Plans = plans,
        };

    public static VerificationRecord Verification(
        string planId, string checkpointId, int attempt, VerificationVerdict verdict)
        => new(1, planId, 1, checkpointId, attempt, DateTimeOffset.UnixEpoch,
            new GateResults(GateOutcome.Pass, GateOutcome.Pass),
            [new SemanticCriterionResult("it works",
                verdict == VerificationVerdict.Pass ? SemanticVerdict.Pass : SemanticVerdict.Fail,
                verdict == VerificationVerdict.Pass ? null : "not yet")],
            verdict, "Body.\n");

    public static CheckpointRunPreview MakePreview(
        string planId = "alpha", string checkpointId = "01-a", int attempt = 1,
        bool pendingDecision = false, string? settingsJson = null)
    {
        var checkpoint = MakeCheckpoint(checkpointId, CheckpointStatus.InProgress, "Add the endpoint");
        var plan = MakePlan(planId, PlanStatus.InProgress, [checkpoint]);
        return new CheckpointRunPreview
        {
            Plan = plan,
            Checkpoint = checkpoint,
            CheckpointIndex = 0,
            Attempt = attempt,
            Branch = $"orca/{planId}/{checkpointId}",
            BranchExists = false,
            PendingDecision = pendingDecision,
            Prompt = "Implement the checkpoint.\nSecond line.",
            PromptSha256 = "abcdef0123456789",
            Options = new ClaudeInvocationOptions
            {
                Prompt = "Implement the checkpoint.",
                WorkingDirectory = "/repo",
                AllowedTools = ["Read", "Write", "Edit"],
                MaxTurns = 60,
                Timeout = TimeSpan.FromMinutes(30),
                Model = null,
                MaxBudgetUsd = 5m,
            },
            Argv = ["claude", "-p", "--safe-mode", "--output-format", "stream-json"],
            SettingsJson = settingsJson,
            LogPaths = new RunLogPaths { EventLog = ".orca/runs/events.ndjson", Stderr = ".orca/runs/stderr.log" },
        };
    }

    public static ClaudeExecutionResult MakeExecution(
        decimal cost = 0.5m, int turns = 12, long durationMs = 63_000,
        bool error = false, ExecutionOutcome outcome = ExecutionOutcome.Completed)
        => new()
        {
            Outcome = outcome,
            ExitCode = error ? 1 : 0,
            SessionId = "sess-1",
            Result = outcome == ExecutionOutcome.TimedOut ? null : new ClaudeResultSummary
            {
                Subtype = error ? "error_during_execution" : "success",
                IsError = error,
                SessionId = "sess-1",
                NumTurns = turns,
                DurationMs = durationMs,
                TotalCostUsd = cost,
                Usage = new TokenUsage
                {
                    InputTokens = 10,
                    OutputTokens = 20,
                    CacheCreationInputTokens = 0,
                    CacheReadInputTokens = 0,
                },
                ResultText = error ? "Not logged in · Please run /login" : null,
            },
            Elapsed = TimeSpan.FromMilliseconds(durationMs),
        };

    public static GateCommandResult MakeGate(
        string gate, int exitCode = 0, double seconds = 3.2, bool timedOut = false)
        => new()
        {
            Gate = gate,
            Command = ["dotnet", gate == "build" ? "build" : "test"],
            ExitCode = exitCode,
            TimedOut = timedOut,
            OutputPath = $".orca/runs/{gate}.log",
            OutputTail = "…",
            Elapsed = TimeSpan.FromSeconds(seconds),
        };

    public static ReconciliationRecord Reconciliation(
        string planId, string checkpointId, int attempt,
        ReconciliationVerdict verdict, IReadOnlyList<string> missing)
        => new(1, planId, 1, checkpointId, attempt, verdict, missing, [], "Body.\n");
}
