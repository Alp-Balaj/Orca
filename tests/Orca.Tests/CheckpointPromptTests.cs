using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

public class CheckpointPromptTests
{
    [Fact]
    public void Reconciler_role_text_watches_for_scratch_artifacts_as_extras()
    {
        // BF-20: the Implementer's scratch convention (.orca/scratch/) only helps if a
        // forgotten violation still gets flagged — pins that the Reconciler's role prompt
        // actually asks for it, so the guidance can't silently regress unnoticed.
        Assert.Contains(".orca/scratch/", ReconcilerPrompt.RoleText);
        Assert.Contains("scratch", ReconcilerPrompt.RoleText, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Checkpoint Checkpoint01 = new(
        "01-tag-entity",
        "Add Tag entity + EF mapping",
        CheckpointStatus.Pending,
        ["Tag entity exists", "EF mapping compiles"]);

    private static readonly Plan Plan = new(
        1, "recipe-tagging", 1, "Recipe tagging", PlanStatus.InProgress, "RecipeApp", "main",
        DateTimeOffset.Parse("2026-07-06T14:00:00Z"), DateTimeOffset.Parse("2026-07-06T14:00:00Z"),
        new PlanningInfo(PlanningStrategyKind.SingleCandidate, null, null, null),
        [Checkpoint01],
        "Plan body.");

    [Fact]
    public void Assemble_directs_scratch_work_to_the_gitignored_orca_scratch_convention()
    {
        // BF-20: the Implementer left throwaway inspection artifacts (a standalone console
        // project, a "ZZ"-prefixed scratch test) uncleaned, and Approve's `git add -A`
        // committed them — .orca/ is already fully gitignored, so pointing scratch work
        // there makes committing it structurally impossible, not merely discouraged.
        var prompt = CheckpointPrompt.Assemble(Plan, Checkpoint01, childNote: null);

        Assert.Contains(".orca/scratch/", prompt);
    }
}
