using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>Status as a query (§3.4): the data both front ends render — the CLI's
/// status command and the TUI tree's entire backing model. No file watcher; the report
/// is rebuilt on demand.</summary>
public static class StatusReportBuilder
{
    /// <summary>planId null = all plans in the vault (the TUI tree); an explicit id or a
    /// single-plan vault narrows to one (today's StatusCommand behavior).</summary>
    public static StatusReport Build(OrcaConfig config, GitWorkspace workspace, string? planId)
    {
        var layout = new VaultLayout(config.VaultRoot, config.PlansDir);
        var reader = new VaultReader(layout);
        var stateStore = new RunStateStore(workspace.RepoRoot);

        IReadOnlyList<string> planIds = planId is null
            ? PlanLocator.ListPlanIds(layout)
            : [PlanLocator.ResolvePlanId(layout, planId)];

        var plans = new List<PlanStatusEntry>(planIds.Count);
        foreach (var id in planIds)
        {
            Plan plan;
            try
            {
                plan = reader.ReadPlan(id);
            }
            catch (VaultException e) when (planId is null)
            {
                // BF-13/SUG-14 §1: in the all-plans view (the TUI), one malformed plan —
                // an Obsidian sync artifact, a hand edit, a partially synced file — must
                // become an error ENTRY, not a crash out of a key handler or a blocked
                // tree. Single-plan resolution (the subcommands) stays fail-loud: the
                // guard above only catches when no explicit plan was asked for.
                plans.Add(PlanStatusEntry.Unparseable(id, e.Message));
                continue;
            }

            var state = stateStore.Read(id);

            // Evidence for every checkpoint the plan declares — plus the run state's
            // checkpoint if the plan no longer lists it (a hand-edited plan must not
            // hide the evidence the run state points at).
            var checkpointIds = plan.Checkpoints.Select(c => c.Id).ToList();
            if (state is not null && !checkpointIds.Contains(state.CheckpointId))
                checkpointIds.Add(state.CheckpointId);

            var evidence = new Dictionary<string, CheckpointEvidence>(StringComparer.Ordinal);
            foreach (var checkpointId in checkpointIds)
            {
                var verifications = reader.ReadVerificationRecords(id, checkpointId);
                var reconciliations = reader.ReadReconciliationRecords(id, checkpointId);
                evidence[checkpointId] = new CheckpointEvidence
                {
                    LatestVerification = verifications.Count > 0 ? verifications[^1] : null,
                    LatestReconciliation = reconciliations.Count > 0 ? reconciliations[^1] : null,
                };
            }

            plans.Add(new PlanStatusEntry
            {
                Plan = plan,
                RunState = state,
                Evidence = evidence,
            });
        }

        return new StatusReport
        {
            RepoRoot = workspace.RepoRoot,
            CurrentBranch = workspace.CurrentBranch(),
            WorkingTreeClean = workspace.IsClean(),
            VaultRoot = layout.VaultRoot,
            PlansDir = config.PlansDir,
            Plans = plans,
        };
    }
}

public sealed record StatusReport
{
    public required string RepoRoot { get; init; }
    public required string CurrentBranch { get; init; }
    public required bool WorkingTreeClean { get; init; }
    public required string VaultRoot { get; init; }
    public required string PlansDir { get; init; }
    public required IReadOnlyList<PlanStatusEntry> Plans { get; init; }
}

public sealed record PlanStatusEntry
{
    public required Plan Plan { get; init; }
    public required RunState? RunState { get; init; }

    /// <summary>Checkpoint id → latest vault evidence (either record may be absent).</summary>
    public required IReadOnlyDictionary<string, CheckpointEvidence> Evidence { get; init; }

    /// <summary>Non-null when the plan file did not parse (BF-13): <see cref="Plan"/> is
    /// then a display-only placeholder — consumers must treat this entry as an error
    /// node, never as plan truth.</summary>
    public string? LoadError { get; init; }

    /// <summary>The error entry for a malformed plan file: a placeholder spine (empty
    /// checkpoints, the folder's id) that exists purely so the tree can render a red
    /// node naming the reason instead of crashing or blocking.</summary>
    public static PlanStatusEntry Unparseable(string planId, string reason) => new()
    {
        Plan = new Plan(
            1, planId, 0, "(unparseable)", PlanStatus.Draft, "", "",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new PlanningInfo(PlanningStrategyKind.SingleCandidate, null, null, null),
            [], ""),
        RunState = null,
        Evidence = new Dictionary<string, CheckpointEvidence>(StringComparer.Ordinal),
        LoadError = reason,
    };
}

public sealed record CheckpointEvidence
{
    public required VerificationRecord? LatestVerification { get; init; }
    public required ReconciliationRecord? LatestReconciliation { get; init; }
}
