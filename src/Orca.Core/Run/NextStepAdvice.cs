using Orca.Core.Domain;

namespace Orca.Core.Run;

/// <summary>What to do next after a checkpoint decision lands — read straight from the
/// plan's current checkpoint order, never guessed: the first pending checkpoint if one
/// remains, or an all-complete message naming the branch left to merge and the plan's own
/// completion action.</summary>
public static class NextStepAdvice
{
    /// <param name="plan">The plan, re-read after the decision so a status flip the
    /// decision just applied (e.g. MarkCheckpointComplete) is reflected.</param>
    /// <param name="decidedBranch">The branch the just-decided checkpoint ran on — named
    /// in the all-complete message as the one still waiting to be merged.</param>
    public static string Describe(Plan plan, string decidedBranch)
    {
        var next = plan.Checkpoints.FirstOrDefault(c => c.Status == CheckpointStatus.Pending);
        if (next is not null)
            return $"next: checkpoint '{next.Id}' — {next.Description}";

        return $"all checkpoints complete — merge '{decidedBranch}' into '{plan.BaseBranch}' when ready, " +
               $"then mark the plan complete (status: complete) in its plan file.";
    }
}
