using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>The plan-generate preview-modal body (build-order step 7, item 4a/step 2) — the
/// same facts <c>plan-generate</c> and its <c>--dry-run</c> print (strategy, attempt, plan
/// path, argv, --settings, assembled prompt). A light check that the authorize-the-spend
/// moment shows what the human is authorizing.</summary>
public sealed class PlanPreviewTextTests
{
    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    [Fact]
    public void Preview_shows_strategy_plan_path_argv_and_prompt_with_sha()
    {
        var text = Plain(PlanPreviewText.Describe(TuiTestData.MakePlanningPreview()));

        Assert.Contains("single_candidate", text);
        Assert.Contains("recipe-tagging", text);
        Assert.Contains("Plans/recipe-tagging/recipe-tagging.md", text);
        Assert.Contains("argv (one element per line", text);
        Assert.Contains("stream-json", text);
        Assert.Contains("parse retries", text);
        Assert.Contains("assembled prompt (sha256 abcdef012345)", text);
        Assert.Contains("Write the plan.", text);
    }

    [Fact]
    public void Settings_payload_is_shown_only_when_present()
    {
        Assert.DoesNotContain("--settings payload", Plain(PlanPreviewText.Describe(TuiTestData.MakePlanningPreview())));

        var withSettings = Plain(PlanPreviewText.Describe(
            TuiTestData.MakePlanningPreview(settingsJson: "{\n  \"permissions\": {}\n}")));
        Assert.Contains("--settings payload", withSettings);
        Assert.Contains("permissions", withSettings);
        Assert.Contains("via --settings permissions.deny", withSettings);
    }

    [Fact]
    public void Model_falls_back_to_cli_default_when_unset()
    {
        Assert.Contains("CLI default", Plain(PlanPreviewText.Describe(TuiTestData.MakePlanningPreview())));
        Assert.Contains("claude-opus-4-8", Plain(PlanPreviewText.Describe(
            TuiTestData.MakePlanningPreview(model: "claude-opus-4-8"))));
    }
}
