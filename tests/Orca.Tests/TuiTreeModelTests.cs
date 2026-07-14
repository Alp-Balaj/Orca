using Orca.Cli.Tui;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The pure StatusReport → node-model projection (build-order step 4, work item 5).
/// The tree's shape, its status→colour mapping, and its glyphs are all verified here without
/// the Terminal.Gui shell — the shell only renders what this returns.</summary>
public sealed class TuiTreeModelTests
{
    [Fact]
    public void Build_makes_one_root_per_plan_in_report_order()
    {
        var report = TuiTestData.MakeReport(true,
            TuiTestData.MakeEntry(TuiTestData.MakePlan("alpha", PlanStatus.Approved, [])),
            TuiTestData.MakeEntry(TuiTestData.MakePlan("beta", PlanStatus.InProgress, [])));

        var roots = TuiTreeModel.Build(report);

        Assert.Equal(["alpha", "beta"], roots.Select(n => n.Label)); // no checkpoints — no count suffix
        Assert.All(roots, n => Assert.Equal(TuiNodeKind.Plan, n.Kind));
    }

    [Fact]
    public void Plan_label_carries_an_at_a_glance_completion_count()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
        [
            TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Complete),
            TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.InProgress),
            TuiTestData.MakeCheckpoint("03-c", CheckpointStatus.Pending),
        ]);

        var root = Assert.Single(TuiTreeModel.Build(TuiTestData.MakeReport(true, TuiTestData.MakeEntry(plan))));

        Assert.Equal("alpha (1/3)", root.Label);
    }

    [Fact]
    public void Plan_node_carries_id_label_kind_status_and_back_reference()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress, []);
        var entry = TuiTestData.MakeEntry(plan);

        var root = Assert.Single(TuiTreeModel.Build(TuiTestData.MakeReport(true, entry)));

        Assert.Equal("alpha", root.Label);
        Assert.Equal(TuiNodeKind.Plan, root.Kind);
        Assert.Equal(TuiNodeStatus.InProgress, root.Status);
        Assert.Same(entry, root.Plan);
        Assert.Null(root.Checkpoint);
    }

    [Fact]
    public void Checkpoint_children_are_built_in_order_with_glyph_labels_and_back_references()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
        [
            TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Complete),
            TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.Pending),
        ]);
        var verification = TuiTestData.Verification("alpha", "01-a", 1, VerificationVerdict.Pass);
        var evidence = new Dictionary<string, CheckpointEvidence>(StringComparer.Ordinal)
        {
            ["01-a"] = new() { LatestVerification = verification, LatestReconciliation = null },
            ["02-b"] = new() { LatestVerification = null, LatestReconciliation = null },
        };
        var entry = TuiTestData.MakeEntry(plan, evidence: evidence);

        var root = Assert.Single(TuiTreeModel.Build(TuiTestData.MakeReport(true, entry)));

        Assert.Equal(["✓ 01-a", "· 02-b"], root.Children.Select(c => c.Label));
        Assert.All(root.Children, c => Assert.Equal(TuiNodeKind.Checkpoint, c.Kind));
        Assert.All(root.Children, c => Assert.False(c.IsCurrent));
        Assert.All(root.Children, c => Assert.Empty(c.Children)); // recursion bottoms out — depth, not two hardcoded levels
        Assert.Same(entry, root.Children[0].Plan);
        Assert.Equal("01-a", root.Children[0].Checkpoint!.Id);
        Assert.Same(verification, root.Children[0].Evidence!.LatestVerification);
        Assert.Equal(TuiNodeStatus.Complete, root.Children[0].Status);
        Assert.Equal(TuiNodeStatus.Pending, root.Children[1].Status);
    }

    [Fact]
    public void Run_states_current_checkpoint_gets_the_active_dot_and_the_IsCurrent_flag()
    {
        var plan = TuiTestData.MakePlan("alpha", PlanStatus.InProgress,
        [
            TuiTestData.MakeCheckpoint("01-a", CheckpointStatus.Complete),
            TuiTestData.MakeCheckpoint("02-b", CheckpointStatus.Pending),
        ]);
        var entry = TuiTestData.MakeEntry(plan, TuiTestData.MakeRunState("alpha", "02-b"));

        var root = Assert.Single(TuiTreeModel.Build(TuiTestData.MakeReport(true, entry)));

        Assert.Equal("✓ 01-a", root.Children[0].Label);
        Assert.Equal("⏺ 02-b", root.Children[1].Label); // current wins over the pending glyph
        Assert.False(root.Children[0].IsCurrent);
        Assert.True(root.Children[1].IsCurrent); // drives the accent colour in the shell
    }

    // Expected is passed as the enum name (a string) so the public test signature does not
    // expose the internal TuiNodeStatus type.
    [Theory]
    [InlineData(CheckpointStatus.Complete, "Complete")]
    [InlineData(CheckpointStatus.Rejected, "Failed")]
    [InlineData(CheckpointStatus.Blocked, "Failed")]
    [InlineData(CheckpointStatus.InProgress, "InProgress")]
    [InlineData(CheckpointStatus.AwaitingReview, "InProgress")]
    [InlineData(CheckpointStatus.Pending, "Pending")]
    public void Checkpoint_status_maps_like_StatusCommand(CheckpointStatus status, string expected)
        => Assert.Equal(expected, TuiTreeModel.MapCheckpointStatus(status).ToString());

    [Theory]
    [InlineData(PlanStatus.Complete, "Complete")]
    [InlineData(PlanStatus.Blocked, "Failed")]
    [InlineData(PlanStatus.Abandoned, "Failed")]
    [InlineData(PlanStatus.InProgress, "InProgress")]
    [InlineData(PlanStatus.Draft, "Pending")]
    [InlineData(PlanStatus.Approved, "Pending")]
    public void Plan_status_maps_analogously(PlanStatus status, string expected)
        => Assert.Equal(expected, TuiTreeModel.MapPlanStatus(status).ToString());

    [Theory]
    [InlineData(CheckpointStatus.Complete, false, "✓")]
    [InlineData(CheckpointStatus.Rejected, false, "✗")]
    [InlineData(CheckpointStatus.Blocked, false, "✗")]
    [InlineData(CheckpointStatus.Pending, false, "·")]
    [InlineData(CheckpointStatus.InProgress, false, "⏺")]
    [InlineData(CheckpointStatus.AwaitingReview, false, "⏺")]
    [InlineData(CheckpointStatus.Complete, true, "⏺")]
    [InlineData(CheckpointStatus.Pending, true, "⏺")]
    public void Glyph_marks_current_first_then_status(CheckpointStatus status, bool isCurrent, string expected)
        => Assert.Equal(expected, TuiTreeModel.CheckpointGlyph(status, isCurrent));
}
