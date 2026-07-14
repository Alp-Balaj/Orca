using Orca.Cli.Tui;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The pure styled-text projections behind the TUI's read-only panes (detail,
/// activity, status line). Verified without the Terminal.Gui shell — the shell just hands
/// these lines to a renderer. Content is asserted on the flattened text; the Claude Code
/// colour language (verdict words coloured, labels dim) on the runs' roles.</summary>
public sealed class TuiTextTests
{
    private static TuiTreeNode PlanNode(PlanStatusEntry entry)
        => TuiTreeModel.Build(TuiTestData.MakeReport(true, entry))[0];

    private static TuiTreeNode CheckpointNode(PlanStatusEntry entry, int index)
        => PlanNode(entry).Children[index];

    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    private static TuiRole RoleOf(IReadOnlyList<StyledLine> lines, string runText)
    {
        foreach (var line in lines)
            foreach (var run in line.Runs)
                if (run.Text == runText)
                    return run.Role;
        throw new Xunit.Sdk.XunitException($"no run with text '{runText}' in:\n{Plain(lines)}");
    }

    [Fact]
    public void Plan_detail_shows_title_status_and_facts()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress, [], title: "Alpha Work", version: 3);
        var detail = TuiText.Detail(PlanNode(TuiTestData.MakeEntry(plan)));
        var text = Plain(detail);

        Assert.Contains("Alpha Work", text);
        Assert.Contains("in progress · v3 · base main · repo RecipeApp", text);
        Assert.Contains("no checkpoints in this plan", text);
        Assert.Equal(TuiRole.Warning, RoleOf(detail, "in progress")); // status word carries the status colour
        Assert.Equal(TuiRole.Heading, RoleOf(detail, "Alpha Work"));
    }

    [Fact]
    public void Plan_detail_tallies_checkpoints_as_the_glyph_legend()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
        [
            TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Complete),
            TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.Blocked),
            TuiTestData.MakeCheckpoint("03-c", CheckpointStatus.InProgress),
            TuiTestData.MakeCheckpoint("04-d", CheckpointStatus.Pending),
        ]);
        var detail = TuiText.Detail(PlanNode(TuiTestData.MakeEntry(plan)));
        var text = Plain(detail);

        Assert.Contains("1 done", text);
        Assert.Contains("1 active", text);
        Assert.Contains("1 blocked", text);
        Assert.Contains("1 pending", text);
        Assert.Equal(TuiRole.Success, RoleOf(detail, "✓ "));
        Assert.Equal(TuiRole.Error, RoleOf(detail, "✗ "));
    }

    [Fact]
    public void Checkpoint_detail_shows_description_criteria_and_absent_evidence()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Pending, "Add the endpoint", "returns 200", "has a test")]);
        var detail = TuiText.Detail(CheckpointNode(TuiTestData.MakeEntry(plan), 0));
        var text = Plain(detail);

        Assert.Contains("01-a", text);
        Assert.Contains("Add the endpoint", text);
        Assert.Contains("· returns 200", text);
        Assert.Contains("· has a test", text);
        Assert.Contains("no evidence yet — records land here after a run", text);
        Assert.DoesNotContain("verification", text); // absent evidence is one quiet line, not two headers
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
        var text = Plain(detail);

        Assert.Contains("verification pass · attempt 2", text);
        Assert.Contains("gates  build ✓ · tests ✓", text);
        Assert.Contains("✓ it works", text);
        Assert.Contains("reconciliation partial · attempt 1", text);
        Assert.Contains("missing  the rest", text);
        Assert.Equal(TuiRole.Success, RoleOf(detail, "pass"));
        Assert.Equal(TuiRole.Warning, RoleOf(detail, "partial"));
    }

    [Fact]
    public void Checkpoint_detail_shows_failed_criteria_with_their_notes()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.InProgress)]);
        var evidence = new Dictionary<string, CheckpointEvidence>(StringComparer.Ordinal)
        {
            ["01-a"] = new()
            {
                LatestVerification = TuiTestData.Verification("alpha", "01-a", 1, VerificationVerdict.Fail),
                LatestReconciliation = null,
            },
        };
        var detail = TuiText.Detail(CheckpointNode(TuiTestData.MakeEntry(plan, evidence: evidence), 0));
        var text = Plain(detail);

        Assert.Contains("verification fail · attempt 1", text);
        Assert.Contains("✗ it works", text);
        Assert.Contains("not yet", text); // the semantic note rides under its criterion
        Assert.Contains("reconciliation none yet", text);
        Assert.Equal(TuiRole.Error, RoleOf(detail, "fail"));
    }

    [Fact]
    public void Current_checkpoint_detail_shows_the_attempt_from_run_state()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.InProgress)]);
        var state = TuiTestData.MakeRunState("alpha", "02-b", attempt: 3);
        var detail = TuiText.Detail(CheckpointNode(TuiTestData.MakeEntry(plan, state), 0));

        Assert.Contains("in progress · attempt 3", Plain(detail));
    }

    [Fact]
    public void Activity_is_a_clear_idle_placeholder_when_the_plan_has_no_run_state()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.Approved, []);
        var activity = TuiText.Activity(PlanNode(TuiTestData.MakeEntry(plan)));
        var text = Plain(activity);

        Assert.Contains("idle — no runs yet for this plan", text);
        Assert.Contains("orca checkpoint-run", text);
    }

    [Fact]
    public void Activity_shows_the_last_run_summary_from_run_state()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.InProgress)]);
        var state = TuiTestData.MakeRunState("alpha", "02-b", attempt: 3, cost: 1.25m, stopReason: InvocationStopReason.Succeeded);
        var activity = TuiText.Activity(CheckpointNode(TuiTestData.MakeEntry(plan, state), 0));
        var text = Plain(activity);

        Assert.Contains("last run — 02-b · attempt 3", text);
        Assert.Contains("orca/alpha/02-b", text);
        Assert.Contains("$1.25", text);
        Assert.Contains("succeeded", text);
        Assert.Equal(TuiRole.Success, RoleOf(activity, "succeeded"));
    }

    [Fact]
    public void Activity_marks_an_interrupted_run_that_has_no_stop_reason()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
            [TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.InProgress)]);
        var state = TuiTestData.MakeRunState("alpha", "02-b", stopReason: null);
        var activity = TuiText.Activity(CheckpointNode(TuiTestData.MakeEntry(plan, state), 0));

        Assert.Contains("interrupted mid-run", Plain(activity));
    }

    [Fact]
    public void Status_line_shows_repo_branch_vault_and_the_selected_plans_cost()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress, []);
        var entry = TuiTestData.MakeEntry(plan, TuiTestData.MakeRunState("alpha", "01-a", cost: 3.41m));
        var report = TuiTestData.MakeReport(true, entry);
        var node = TuiTreeModel.Build(report)[0];

        var line = TuiText.StatusLine(report, node);
        var text = line.PlainText();

        Assert.Contains("/repo", text);
        Assert.Contains("main", text);
        Assert.DoesNotContain("*", text);
        Assert.Contains("vault vault", text); // display name is the vault root's basename
        Assert.Contains("spent $3.41", text);
    }

    [Fact]
    public void Status_line_marks_a_dirty_tree_and_shows_zero_cost_for_no_selection()
    {
        var report = TuiTestData.MakeReport(false); // no plans, dirty tree

        var line = TuiText.StatusLine(report, selected: null);

        Assert.Contains("main*", line.PlainText());
        Assert.Contains("spent $0.00", line.PlainText());
        Assert.Equal(TuiRole.Warning, line.Runs.Single(r => r.Text == "*").Role);
    }

    [Fact]
    public void Empty_vault_screen_points_at_plan_init()
    {
        var text = Plain(TuiText.EmptyVault());

        Assert.Contains("No plans in this vault yet.", text);
        Assert.Contains("orca plan-init", text);
    }

    [Theory]
    [InlineData("/repo", "/repo")] // short stays verbatim
    [InlineData("/very/long/path/that/keeps/going/and/going/beyond/forty/chars/work/repo", "…/work/repo")]
    public void Compact_path_keeps_short_paths_and_tail_shortens_long_ones(string path, string expected)
        => Assert.Equal(expected, TuiText.CompactPath(path));

    [Fact]
    public void Status_words_are_lowercase_with_spaces()
    {
        Assert.Equal("awaiting review", TuiText.StatusWord(CheckpointStatus.AwaitingReview));
        Assert.Equal("in progress", TuiText.StatusWord(PlanStatus.InProgress));
        Assert.Equal("succeeded", TuiText.StatusWord(InvocationStopReason.Succeeded));
    }
}
