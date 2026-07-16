using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>SUG-07: the generated cockpit note — focus line by descending urgency,
/// next-up list, evidence-derived loose ends, spend table, and the generated-never-
/// authoritative banner.</summary>
public sealed class DashboardComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private static string Compose(
        IReadOnlyList<PlanStatusEntry> plans, IReadOnlyList<UnmergedApproval>? unmerged = null) =>
        DashboardComposer.Compose(TuiTestData.MakeReport(true, [.. plans]), unmerged ?? [], Now);

    [Fact]
    public void Banner_frontmatter_and_marker_are_always_present()
    {
        var text = Compose([]);

        Assert.Contains("type: dashboard", text);
        Assert.Contains("updated: 2026-07-17T12:00:00Z", text);
        Assert.Contains(DashboardComposer.Marker, text);
        Assert.Contains("source of truth", text);
    }

    [Fact]
    public void A_stopped_attempt_outranks_everything_in_the_focus_line()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Pending)]);
        var state = TuiTestData.MakeRunState("alpha", "01-a", attempt: 2,
            stopReason: Orca.Core.Domain.InvocationStopReason.RateLimited);

        var text = Compose([TuiTestData.MakeEntry(plan, state)]);

        Assert.Contains("pick up [[alpha]]", text);
        Assert.Contains("stopped rate_limited", text);
    }

    [Fact]
    public void Otherwise_the_first_runnable_checkpoint_is_the_focus()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.Approved,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Complete),
             TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.Pending, "Wire the endpoint")]);

        var text = Compose([TuiTestData.MakeEntry(plan)]);

        Assert.Contains("run `02-b` of [[alpha]]", text);
        Assert.Contains("- [[alpha]] → `02-b` — Wire the endpoint", text);
    }

    [Fact]
    public void A_draft_plan_is_the_focus_when_nothing_is_runnable()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.Draft,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Pending)]);

        var text = Compose([TuiTestData.MakeEntry(plan)]);

        Assert.Contains("review and approve [[alpha]]", text);
    }

    [Fact]
    public void Unmerged_approvals_and_evidence_status_drift_land_in_loose_ends()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Complete),
             TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.Pending)]);
        var evidence = new Dictionary<string, CheckpointEvidence>(StringComparer.Ordinal)
        {
            ["01-a"] = new() { LatestVerification = null, LatestReconciliation = null },
            ["02-b"] = new()
            {
                LatestVerification = TuiTestData.Verification("alpha", "02-b", 1, VerificationVerdict.Pass),
                LatestReconciliation = null,
            },
        };

        var text = Compose(
            [TuiTestData.MakeEntry(plan, evidence: evidence)],
            [new UnmergedApproval("alpha", "01-a", "orca/alpha/01-a", "main")]);

        Assert.Contains("`01-a` is approved on `orca/alpha/01-a` but not merged into `main`", text);
        Assert.Contains("`02-b` has a passing verification (attempt 1) but the plan file still says pending", text);
    }

    [Fact]
    public void Spend_table_lists_every_plan_with_cost_or_a_dash()
    {
        var ran = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Pending)]);
        var idle = TuiTestData.MakePlan("beta", PlanStatus.Draft,
            [TuiTestData.MakeCheckpoint("01-x", CheckpointStatus.Pending)]);

        var text = Compose(
        [
            TuiTestData.MakeEntry(ran, TuiTestData.MakeRunState("alpha", "01-a", cost: 1.14m)),
            TuiTestData.MakeEntry(idle),
        ]);

        Assert.Contains("| [[alpha]] | in_progress | $1.14 |", text);
        Assert.Contains("| [[beta]] | draft | — | — |", text);
    }
}
