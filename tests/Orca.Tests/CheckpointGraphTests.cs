using Orca.Core.Domain;

namespace Orca.Tests;

/// <summary>SUG-03 stage A's dependency view: the compatibility default (previous
/// checkpoint), explicit parallel roots, incomplete-dependency judgment, and the ready
/// set.</summary>
public sealed class CheckpointGraphTests
{
    private static Plan MakePlan(params Checkpoint[] checkpoints) =>
        TuiTestData.MakePlan("recipe-tagging", PlanStatus.Approved, checkpoints);

    private static Checkpoint Cp(
        string id, CheckpointStatus status, IReadOnlyList<string>? dependsOn = null) =>
        new(id, $"do {id}", status, ["it works"], DependsOn: dependsOn);

    [Fact]
    public void Default_dependency_is_the_previous_checkpoint_and_nothing_for_the_first()
    {
        var plan = MakePlan(Cp("01-a", CheckpointStatus.Complete), Cp("02-b", CheckpointStatus.Pending));

        Assert.Empty(CheckpointGraph.EffectiveDependsOn(plan, plan.Checkpoints[0]));
        Assert.Equal(["01-a"], CheckpointGraph.EffectiveDependsOn(plan, plan.Checkpoints[1]));
    }

    [Fact]
    public void An_explicit_empty_list_is_a_parallel_root()
    {
        var plan = MakePlan(
            Cp("01-a", CheckpointStatus.Pending),
            Cp("02-b", CheckpointStatus.Pending, dependsOn: []));

        Assert.Empty(CheckpointGraph.EffectiveDependsOn(plan, plan.Checkpoints[1]));
        Assert.Equal(["01-a", "02-b"], CheckpointGraph.ReadySet(plan).Select(c => c.Id));
    }

    [Fact]
    public void Ready_means_pending_with_every_effective_dependency_complete()
    {
        var plan = MakePlan(
            Cp("01-auth", CheckpointStatus.Complete),
            Cp("02-api", CheckpointStatus.Pending, dependsOn: ["01-auth"]),
            Cp("03-social", CheckpointStatus.Pending, dependsOn: ["01-auth"]),
            Cp("04-ui", CheckpointStatus.Pending, dependsOn: ["02-api", "03-social"]));

        Assert.Equal(["02-api", "03-social"], CheckpointGraph.ReadySet(plan).Select(c => c.Id));
        Assert.Equal(["02-api", "03-social"],
            CheckpointGraph.IncompleteDependencies(plan, plan.Checkpoints[3]));
    }

    [Fact]
    public void A_complete_or_in_progress_checkpoint_is_never_in_the_ready_set()
    {
        var plan = MakePlan(
            Cp("01-a", CheckpointStatus.Complete),
            Cp("02-b", CheckpointStatus.InProgress));

        Assert.Empty(CheckpointGraph.ReadySet(plan));
    }
}
