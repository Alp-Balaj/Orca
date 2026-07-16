using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

public class NextStepAdviceTests
{
    private static Checkpoint Cp(string id, CheckpointStatus status) =>
        new(id, $"Do {id}", status, ["It works"]);

    private static Plan MakePlan(params Checkpoint[] checkpoints) => new(
        1, "recipe-tagging", 1, "Recipe tagging", PlanStatus.InProgress, "RecipeApp", "main",
        DateTimeOffset.Parse("2026-07-06T14:00:00Z"), DateTimeOffset.Parse("2026-07-06T14:00:00Z"),
        new PlanningInfo(PlanningStrategyKind.SingleCandidate, null, null, null),
        checkpoints,
        "Plan body.");

    [Fact]
    public void Describe_names_the_next_pending_checkpoint()
    {
        var plan = MakePlan(
            Cp("01-a", CheckpointStatus.Complete),
            Cp("02-b", CheckpointStatus.Pending));

        Assert.Contains("next: checkpoint '02-b'", NextStepAdvice.Describe(plan, "orca/recipe-tagging/01-a"));
    }

    [Fact]
    public void Describe_reports_all_complete_and_names_the_branch_to_merge()
    {
        var plan = MakePlan(Cp("01-a", CheckpointStatus.Complete));

        var advice = NextStepAdvice.Describe(plan, "orca/recipe-tagging/01-a");

        Assert.Contains("all checkpoints complete", advice);
        Assert.Contains("merge 'orca/recipe-tagging/01-a' into 'main'", advice);
    }

    [Fact]
    public void ChainCandidate_names_the_next_checkpoint_when_a_different_one_is_pending()
    {
        var plan = MakePlan(
            Cp("01-a", CheckpointStatus.Complete),
            Cp("02-b", CheckpointStatus.Pending));

        var candidate = NextStepAdvice.ChainCandidate(plan, justDecidedCheckpointId: "01-a");

        Assert.Equal("02-b", candidate?.Id);
    }

    [Fact]
    public void ChainCandidate_is_null_when_every_checkpoint_is_complete()
    {
        var plan = MakePlan(Cp("01-a", CheckpointStatus.Complete));

        Assert.Null(NextStepAdvice.ChainCandidate(plan, justDecidedCheckpointId: "01-a"));
    }

    [Fact]
    public void ChainCandidate_is_null_when_the_just_decided_checkpoint_is_still_the_only_pending_one()
    {
        // A forgotten MarkCheckpointComplete: the plan file's status was never flipped, so
        // the just-approved checkpoint is still "next" by NextPending's own logic. Offering
        // to "run" it again would just re-trip Prepare's complete-by-evidence guard.
        var plan = MakePlan(Cp("01-a", CheckpointStatus.Pending));

        Assert.Null(NextStepAdvice.ChainCandidate(plan, justDecidedCheckpointId: "01-a"));
    }
}
