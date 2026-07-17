namespace Orca.Core.Domain;

/// <summary>The dependency view over a plan's checkpoint list (SUG-03 stage A). The
/// two-digit prefixes remain presentation order; <c>depends_on</c> is the truth, with
/// the compatibility default "the previous checkpoint" when a checkpoint declares
/// nothing. All pure plan-level judgments — merged-ness of a dependency's branch is
/// git's to answer and stays in <c>CheckpointRunner</c>'s guards.</summary>
public static class CheckpointGraph
{
    /// <summary>The checkpoint ids that must be complete before this one runs: its
    /// declared <c>depends_on</c>, or the flat list's implied default — the previous
    /// checkpoint (nothing for the first).</summary>
    public static IReadOnlyList<string> EffectiveDependsOn(Plan plan, Checkpoint checkpoint)
    {
        if (checkpoint.DependsOn is { } declared)
            return declared;

        var index = IndexOf(plan, checkpoint.Id);
        return index > 0 ? [plan.Checkpoints[index - 1].Id] : [];
    }

    /// <summary>The effective dependencies of <paramref name="checkpoint"/> that are not
    /// complete yet — empty exactly when its dependencies are satisfied plan-side.</summary>
    public static IReadOnlyList<string> IncompleteDependencies(Plan plan, Checkpoint checkpoint) =>
        [.. EffectiveDependsOn(plan, checkpoint)
            .Where(id => ById(plan, id).Status != CheckpointStatus.Complete)];

    /// <summary>Pending and every effective dependency complete — what a human (or the
    /// default selection) can safely start now. Order preserved from the plan.</summary>
    public static IReadOnlyList<Checkpoint> ReadySet(Plan plan) =>
        [.. plan.Checkpoints.Where(c =>
            c.Status == CheckpointStatus.Pending && IncompleteDependencies(plan, c).Count == 0)];

    private static Checkpoint ById(Plan plan, string id) =>
        plan.Checkpoints.First(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    private static int IndexOf(Plan plan, string id)
    {
        for (var i = 0; i < plan.Checkpoints.Count; i++)
        {
            if (string.Equals(plan.Checkpoints[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        throw new ArgumentException($"plan '{plan.PlanId}' has no checkpoint '{id}'");
    }
}
