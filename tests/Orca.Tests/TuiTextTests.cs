using Orca.Cli.Tui;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The pure text projections behind the TUI's read-only panes (detail, activity,
/// status line). Verified without the Terminal.Gui shell — the shell just sets these strings
/// on its widgets.</summary>
public sealed class TuiTextTests
{
    private static TuiTreeNode PlanNode(PlanStatusEntry entry)
        => TuiTreeModel.Build(TuiTestData.MakeReport(true, entry))[0];

    private static TuiTreeNode CheckpointNode(PlanStatusEntry entry, int index)
        => PlanNode(entry).Children[index];

    [Fact]
    public void Plan_detail_shows_frontmatter_facts()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress, [], title: "Alpha Work", version: 3);
        var detail = TuiText.Detail(PlanNode(TuiTestData.MakeEntry(plan)));

        Assert.Contains("Alpha Work", detail);
        Assert.Contains("id            alpha", detail);
        Assert.Contains("version       3", detail);
        Assert.Contains("status        inprogress", detail); // enum name lowercased
        Assert.Contains("base branch   main", detail);
    }

    [Fact]
    public void Checkpoint_detail_shows_description_criteria_and_absent_evidence()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Pending, "Add the endpoint", "returns 200", "has a test")]);
        var detail = TuiText.Detail(CheckpointNode(TuiTestData.MakeEntry(plan), 0));

        Assert.Contains("Add the endpoint", detail);
        Assert.Contains("- returns 200", detail);
        Assert.Contains("- has a test", detail);
        Assert.Contains("verification    (no record yet)", detail);
        Assert.Contains("reconciliation  (no record yet)", detail);
    }

    [Fact]
    public void Checkpoint_detail_shows_latest_verification_and_reconciliation()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.AwaitingReview)]);
        var evidence = new Dictionary<string, CheckpointEvidence>(StringComparer.Ordinal)
        {
            ["01-a"] = new()
            {
                LatestVerification = TuiTestData.Verification("alpha", "01-a", 2, VerificationVerdict.Pass),
                LatestReconciliation = TuiTestData.Reconciliation("alpha", "01-a", 1, ReconciliationVerdict.Partial, ["the rest"]),
            },
        };
        var detail = TuiText.Detail(CheckpointNode(TuiTestData.MakeEntry(plan, evidence: evidence), 0));

        Assert.Contains("verification    pass (attempt 2, gates: build pass, tests pass)", detail);
        Assert.Contains("reconciliation  partial (attempt 1)", detail);
        Assert.Contains("missing: the rest", detail);
    }

    [Fact]
    public void Activity_is_a_clear_idle_placeholder_when_the_plan_has_no_run_state()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.Approved, []);
        var activity = TuiText.Activity(PlanNode(TuiTestData.MakeEntry(plan)));

        Assert.Contains("idle", activity);
        Assert.Contains("no runs yet", activity);
    }

    [Fact]
    public void Activity_shows_the_last_run_summary_from_run_state()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.InProgress)]);
        var state = TuiTestData.MakeRunState("alpha", "02-b", attempt: 3, cost: 1.25m, stopReason: InvocationStopReason.Succeeded);
        var activity = TuiText.Activity(CheckpointNode(TuiTestData.MakeEntry(plan, state), 0));

        Assert.Contains("02-b (attempt 3)", activity);
        Assert.Contains("orca/alpha/02-b", activity);
        Assert.Contains("$1.25", activity);
        Assert.Contains("succeeded", activity);
    }

    [Fact]
    public void Status_line_shows_repo_branch_vault_and_the_selected_plans_cost()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress, []);
        var entry = TuiTestData.MakeEntry(plan, TuiTestData.MakeRunState("alpha", "01-a", cost: 3.41m));
        var report = TuiTestData.MakeReport(true, entry);
        var node = TuiTreeModel.Build(report)[0];

        var line = TuiText.StatusLine(report, node);

        Assert.Contains("repo /repo", line);
        Assert.Contains("branch main", line);
        Assert.DoesNotContain("(dirty)", line);
        Assert.Contains("vault /vault", line);
        Assert.Contains("spent $3.41", line);
    }

    [Fact]
    public void Status_line_shows_dirty_flag_and_zero_cost_for_no_selection()
    {
        var report = TuiTestData.MakeReport(false); // no plans, dirty tree

        var line = TuiText.StatusLine(report, selected: null);

        Assert.Contains("branch main (dirty)", line);
        Assert.Contains("spent $0.00", line);
    }
}
