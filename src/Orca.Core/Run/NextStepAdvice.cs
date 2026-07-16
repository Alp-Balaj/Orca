using Orca.Core.Domain;

namespace Orca.Core.Run;

/// <summary>What to do next after a checkpoint decision lands — read straight from the
/// plan's current checkpoint order, never guessed: the first pending checkpoint if one
/// remains, or an all-complete message naming the branch left to merge and the plan's own
/// completion action.</summary>
public static class NextStepAdvice
{
    public static Checkpoint? NextPending(Plan plan) =>
        plan.Checkpoints.FirstOrDefault(c => c.Status == CheckpointStatus.Pending);

    /// <summary>The checkpoint SUG-16 proposal A's "run it now?" offer should name, or
    /// null when there's nothing genuinely new to chain to: either every checkpoint is
    /// complete, or the just-decided one is still pending — a forgotten
    /// <c>MarkCheckpointComplete</c>, not a real next step. Offering to "run" the same
    /// checkpoint again would just re-trip <c>CheckpointRunner.Prepare</c>'s
    /// complete-by-evidence guard.</summary>
    public static Checkpoint? ChainCandidate(Plan plan, string justDecidedCheckpointId)
    {
        var next = NextPending(plan);
        return next is not null && next.Id != justDecidedCheckpointId ? next : null;
    }

    /// <param name="plan">The plan, re-read after the decision so a status flip the
    /// decision just applied (e.g. MarkCheckpointComplete) is reflected.</param>
    /// <param name="decidedBranch">The branch the just-decided checkpoint ran on — named
    /// in the all-complete message as the one still waiting to be merged.</param>
    public static string Describe(Plan plan, string decidedBranch)
    {
        var next = NextPending(plan);
        if (next is not null)
            return $"next: checkpoint '{next.Id}' — {next.Description}";

        return $"all checkpoints complete — merge '{decidedBranch}' into '{plan.BaseBranch}' when ready, " +
               $"then mark the plan complete (status: complete) in its plan file.";
    }
}
