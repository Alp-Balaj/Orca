using Orca.Cli.Tui;
using Orca.Core.Domain;

namespace Orca.Tests;

/// <summary>SUG-17's status-edit confirm bodies. Every one must name the exact transition
/// and the file being edited (Obsidian stays a fallback, never a requirement), and the
/// mark-complete body must draw the evidence/no-evidence distinction the CLI draws with
/// --anyway.</summary>
public sealed class StatusEditTextTests
{
    private const string Path = "/vault/Plans/recipe-tagging/recipe-tagging.md";

    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    private static Plan MakePlan(PlanStatus status) =>
        TuiTestData.MakePlan("recipe-tagging", status,
            [TuiTestData.MakeCheckpoint("01-tag-entity", CheckpointStatus.Pending)]);

    [Fact]
    public void Approve_names_the_plan_the_transition_and_the_file()
    {
        var text = Plain(StatusEditText.PlanFlip(MakePlan(PlanStatus.Draft), Path, PlanStatus.Approved));
        Assert.Contains("Approve plan recipe-tagging?", text);
        Assert.Contains("status: draft → approved", text);
        Assert.Contains(Path, text);
    }

    [Fact]
    public void Unapprove_is_the_reverse_flip()
    {
        var text = Plain(StatusEditText.PlanFlip(MakePlan(PlanStatus.Approved), Path, PlanStatus.Draft));
        Assert.Contains("Un-approve plan recipe-tagging?", text);
        Assert.Contains("status: approved → draft", text);
    }

    [Fact]
    public void Mark_complete_cites_the_evidence_when_there_is_some()
    {
        var checkpoint = TuiTestData.MakeCheckpoint("02-endpoint", CheckpointStatus.InProgress);
        var text = Plain(StatusEditText.MarkComplete(
            checkpoint, Path, "attempt 2 has a passing verification record"));

        Assert.Contains("Mark checkpoint 02-endpoint complete?", text);
        Assert.Contains("attempt 2 has a passing verification record", text);
        Assert.Contains("status: in_progress → complete", text);
        Assert.DoesNotContain("anyway", text);
    }

    [Fact]
    public void Mark_complete_warns_loudly_when_there_is_no_evidence()
    {
        var checkpoint = TuiTestData.MakeCheckpoint("02-endpoint", CheckpointStatus.Pending);
        var text = Plain(StatusEditText.MarkComplete(checkpoint, Path, null));

        Assert.Contains("no completion evidence found", text);
        Assert.Contains("Mark complete anyway?", text);
    }

    [Fact]
    public void Reopen_states_the_flip_and_the_attempt_numbering_caveat()
    {
        var checkpoint = TuiTestData.MakeCheckpoint("01-tag-entity", CheckpointStatus.Complete);
        var text = Plain(StatusEditText.Reopen(checkpoint, Path));

        Assert.Contains("Re-open checkpoint 01-tag-entity?", text);
        Assert.Contains("status: complete → pending", text);
        Assert.Contains("attempt number continues", text);
        Assert.Contains(Path, text);
    }
}
