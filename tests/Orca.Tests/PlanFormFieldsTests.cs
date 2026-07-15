using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>The plan-generate form's pure validation + request assembly (build-order step 7,
/// item 4a), headless. Mirrors, field for field, what the CLI enforces across
/// <c>PlanGenerateCommand.Settings.Validate</c>, <c>ReadGoalFile</c>,
/// <c>RouteStrategyToHuman</c>, and <c>Prepare</c>'s empty-input guards — so the form and the
/// CLI accept and reject exactly the same requests. The goal-file read is an injected seam, so
/// that path is covered without touching disk.</summary>
public sealed class PlanFormFieldsTests
{
    private static PlanFormFields Fields(
        string planId = "recipe-tagging", string goal = "Add tag CRUD.", string goalFile = "",
        string title = "", string baseBranch = "main", PlanStrategyChoice strategy = PlanStrategyChoice.Single)
        => new()
        {
            PlanId = planId,
            Goal = goal,
            GoalFile = goalFile,
            Title = title,
            BaseBranch = baseBranch,
            Strategy = strategy,
        };

    private static string? NoFile(string _) => null;

    [Fact]
    public void Inline_goal_resolves_to_a_request()
    {
        var result = PlanFormValidation.Validate(Fields(), NoFile);

        Assert.True(result.Ok);
        Assert.Equal("recipe-tagging", result.Resolved!.PlanId);
        Assert.Equal("Add tag CRUD.", result.Resolved.Goal);
        Assert.Equal("main", result.Resolved.BaseBranch);
        Assert.Null(result.Resolved.Title);
    }

    [Fact]
    public void Plan_id_is_required()
    {
        var result = PlanFormValidation.Validate(Fields(planId: "   "), NoFile);

        Assert.False(result.Ok);
        Assert.Contains("plan id is required", result.Error);
    }

    [Fact]
    public void Exactly_one_of_goal_or_goal_file_is_required()
    {
        var neither = PlanFormValidation.Validate(Fields(goal: "", goalFile: ""), NoFile);
        Assert.False(neither.Ok);
        Assert.Contains("exactly one of goal or goal-file", neither.Error);

        var both = PlanFormValidation.Validate(Fields(goal: "inline", goalFile: "brief.md"), _ => "from file");
        Assert.False(both.Ok);
        Assert.Contains("exactly one of goal or goal-file", both.Error);
    }

    [Fact]
    public void Goal_file_content_becomes_the_goal()
    {
        var result = PlanFormValidation.Validate(
            Fields(goal: "", goalFile: "brief.md"), p => p == "brief.md" ? "Goal from the file." : null);

        Assert.True(result.Ok);
        Assert.Equal("Goal from the file.", result.Resolved!.Goal);
    }

    [Fact]
    public void Missing_goal_file_is_a_clear_error()
    {
        var result = PlanFormValidation.Validate(Fields(goal: "", goalFile: "nope.md"), NoFile);

        Assert.False(result.Ok);
        Assert.Contains("goal file does not exist: nope.md", result.Error);
    }

    [Fact]
    public void Empty_goal_file_content_is_rejected_like_prepare_would()
    {
        var result = PlanFormValidation.Validate(Fields(goal: "", goalFile: "blank.md"), _ => "   \n");

        Assert.False(result.Ok);
        Assert.Contains("goal brief is empty", result.Error);
    }

    [Fact]
    public void Multi_strategy_refuses_and_never_downgrades()
    {
        var result = PlanFormValidation.Validate(Fields(strategy: PlanStrategyChoice.Multi), NoFile);

        Assert.False(result.Ok);
        Assert.Null(result.Resolved);
        Assert.Contains("multi-candidate planning is deferred", result.Error);
        Assert.Equal(PlanFormValidation.MultiDeferred, result.Error);
    }

    [Fact]
    public void Blank_base_branch_defaults_to_main()
    {
        var result = PlanFormValidation.Validate(Fields(baseBranch: "   "), NoFile);

        Assert.True(result.Ok);
        Assert.Equal("main", result.Resolved!.BaseBranch);
    }

    [Fact]
    public void Title_is_trimmed_or_null()
    {
        Assert.Null(PlanFormValidation.Validate(Fields(title: "  "), NoFile).Resolved!.Title);
        Assert.Equal("My Plan", PlanFormValidation.Validate(Fields(title: "  My Plan  "), NoFile).Resolved!.Title);
    }
}
