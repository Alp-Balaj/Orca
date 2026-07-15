using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>The preview-modal body (tui-plan build-order step 5, item 1) — the same facts
/// <c>checkpoint-run --dry-run</c> prints (branch, argv, --settings, assembled prompt).
/// A light check that the authorize-the-spend moment shows what the human is authorizing.</summary>
public sealed class RunPreviewTextTests
{
    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    [Fact]
    public void Preview_shows_branch_argv_and_prompt_with_sha()
    {
        var text = Plain(RunPreviewText.Describe(TuiTestData.MakePreview()));

        Assert.Contains("branch", text);
        Assert.Contains("orca/alpha/01-a (new, from main)", text);
        Assert.Contains("argv (one element per line", text);
        Assert.Contains("stream-json", text);
        Assert.Contains("assembled prompt (sha256 abcdef012345)", text);
        Assert.Contains("Implement the checkpoint.", text);
    }

    [Fact]
    public void Settings_payload_is_shown_only_when_present()
    {
        Assert.DoesNotContain("--settings payload", Plain(RunPreviewText.Describe(TuiTestData.MakePreview())));

        var withSettings = Plain(RunPreviewText.Describe(
            TuiTestData.MakePreview(settingsJson: "{\n  \"permissions\": {}\n}")));
        Assert.Contains("--settings payload", withSettings);
        Assert.Contains("permissions", withSettings);
        Assert.Contains("via --settings permissions.deny", withSettings);
    }

    [Fact]
    public void Pending_decision_preview_says_it_is_reviewing_not_launching()
    {
        var text = Plain(RunPreviewText.Describe(
            TuiTestData.MakePreview(attempt: 2, pendingDecision: true)));

        Assert.Contains("attempt 1 is still awaiting your decision", text);
        Assert.Contains("instead of launching", text);
    }
}
