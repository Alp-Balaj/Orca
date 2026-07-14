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

    public static ReconciliationRecord Reconciliation(
        string planId, string checkpointId, int attempt,
        ReconciliationVerdict verdict, IReadOnlyList<string> missing)
        => new(1, planId, 1, checkpointId, attempt, verdict, missing, [], "Body.\n");
}
