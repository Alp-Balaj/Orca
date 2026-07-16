using Orca.Cli.Tui;
using Orca.Core.Execution;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The decision-modal body. SUG-11 §3's second half lives here: the
/// Implementer's final result text is shown to the deciding human on success too (its
/// summary of what it did, or exactly why it stopped early on a blocked criterion) —
/// previously it was rendered only on IsError and otherwise discarded.</summary>
public sealed class RunDecisionTextTests
{
    private static CheckpointDecisionContext Context(ClaudeExecutionResult execution) => new()
    {
        Preview = TuiTestData.MakePreview(),
        Run = new CheckpointRunOutcome
        {
            Preview = TuiTestData.MakePreview(),
            Execution = execution,
            RunState = TuiTestData.MakeRunState("recipe-tagging", "01-tag-entity"),
        },
        Verification = null,
        StatusLines = ["M src/Tag.cs"],
        DiffStat = "1 file changed",
        AttemptCostUsd = 0.5m,
        IsLastCheckpoint = false,
    };

    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    [Fact]
    public void A_successful_attempts_summary_is_shown_to_the_deciding_human()
    {
        var text = Plain(RunDecisionText.Describe(Context(TuiTestData.MakeExecution(
            resultText: "Added the Tag entity; criterion 2 could not be met because the EF package is pinned."))));

        Assert.Contains("summary", text);
        Assert.Contains("criterion 2 could not be met", text);
        Assert.DoesNotContain("claude says", text);
    }

    [Fact]
    public void A_failed_attempts_result_text_keeps_the_claude_says_label()
    {
        var text = Plain(RunDecisionText.Describe(Context(TuiTestData.MakeExecution(error: true))));

        Assert.Contains("claude says", text);
        Assert.Contains("Not logged in", text);
    }

    [Fact]
    public void No_result_text_renders_no_summary_row()
    {
        var text = Plain(RunDecisionText.Describe(Context(TuiTestData.MakeExecution())));

        Assert.DoesNotContain("summary", text);
        Assert.DoesNotContain("claude says", text);
    }
}
