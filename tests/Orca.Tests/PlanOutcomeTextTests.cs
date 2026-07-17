using Orca.Cli.Tui;
using Orca.Core.Domain;
using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>The pure outcome-summary projection the plan-generate view renders before
/// returning to the shell (build-order step 7, item 4c). Mirrors the CONTENT of
/// <c>PlanGenerateCommand.RenderOutcome</c>: success shows the draft path + review-and-approve
/// pointer (never auto-approves), failures show the per-stop-reason guidance, and the
/// gitignore/interrupt stops render their own way. Same fixture pattern as
/// <see cref="RunOutcomeTextTests"/>.</summary>
public sealed class PlanOutcomeTextTests
{
    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    [Fact]
    public void Success_shows_draft_path_and_review_and_approve_never_auto_approves()
    {
        var text = Plain(PlanOutcomeText.Describe(
            TuiTestData.MakePlanningOutcome(PlanningRunStatus.Succeeded)));

        Assert.Contains("draft plan written", text);
        Assert.Contains("recipe-tagging/recipe-tagging.md", text);
        Assert.Contains("set status to approved when it earns it", text);
        Assert.Contains("orca checkpoint-run", text);
        // Never claims to have approved anything itself.
        Assert.DoesNotContain("approved.", text);
    }

    [Fact]
    public void Rate_limited_names_the_reset_window()
    {
        var reset = new RateLimitInfo
        {
            Status = "exceeded",
            ResetsAt = DateTimeOffset.UnixEpoch.AddYears(56).ToUnixTimeSeconds(),
            RateLimitType = null,
        };
        var text = Plain(PlanOutcomeText.Describe(TuiTestData.MakePlanningOutcome(
            PlanningRunStatus.Failed, InvocationStopReason.RateLimited, rateLimit: reset)));

        // BF-08: an inference from an unrecognized status, never asserted exhaustion —
        // the status is named and the reset window still shown.
        Assert.Contains("possible rate limit", text);
        Assert.Contains("status 'exceeded'", text);
        Assert.DoesNotContain("exhausted", text);
        Assert.Contains("window resets", text);
        Assert.Contains("attempt numbering resumes", text);
    }

    [Fact]
    public void Auth_failed_points_at_the_login()
    {
        var text = Plain(PlanOutcomeText.Describe(TuiTestData.MakePlanningOutcome(
            PlanningRunStatus.Failed, InvocationStopReason.AuthFailed)));

        Assert.Contains("authentication failed", text);
        Assert.Contains("run claude interactively to log in", text);
    }

    [Fact]
    public void Budget_and_timeout_name_the_ceiling_to_raise()
    {
        var budget = Plain(PlanOutcomeText.Describe(TuiTestData.MakePlanningOutcome(
            PlanningRunStatus.Failed, InvocationStopReason.BudgetExceeded)));
        Assert.Contains("budget ceiling hit", budget);
        Assert.Contains("roles.planner.maxBudgetUsd", budget);

        var timeout = Plain(PlanOutcomeText.Describe(TuiTestData.MakePlanningOutcome(
            PlanningRunStatus.Failed, InvocationStopReason.TimedOut)));
        Assert.Contains("timed out", timeout);
        Assert.Contains("roles.planner.timeoutSeconds", timeout);
    }

    [Fact]
    public void Parse_failure_surfaces_the_error_and_the_rejected_file()
    {
        var text = Plain(PlanOutcomeText.Describe(TuiTestData.MakePlanningOutcome(
            PlanningRunStatus.Failed, InvocationStopReason.Succeeded,
            parseFailure: "missing frontmatter key: status",
            failureDetail: "the Planner's output failed parse/validation; last error: missing frontmatter key: status")));

        Assert.Contains("did not produce a valid plan", text);
        Assert.Contains("claude/parser says:", text);
        Assert.Contains("missing frontmatter key: status", text);
        Assert.Contains("rejected file is left at", text);
    }

    [Fact]
    public void Gitignore_added_says_commit_and_rerun_and_nothing_spent()
    {
        var text = Plain(PlanOutcomeText.GitignoreAdded());

        Assert.Contains(".gitignore", text);
        Assert.Contains("commit that change, then re-run", text);
        Assert.Contains("Orca never commits to your branch on its own", text);
        Assert.Contains("Nothing was spent", text);
    }

    [Fact]
    public void Interrupted_resumes_by_attempt_numbering()
    {
        var text = Plain(PlanOutcomeText.Interrupted());

        Assert.Contains("interrupted", text);
        Assert.Contains("attempt numbering continues", text);
        Assert.Contains("nothing completed is re-paid", text);
    }

    [Fact]
    public void Every_outcome_renders_nonempty_without_throwing()
    {
        foreach (var status in Enum.GetValues<PlanningRunStatus>())
            foreach (var stop in Enum.GetValues<InvocationStopReason>())
            {
                var lines = PlanOutcomeText.Describe(TuiTestData.MakePlanningOutcome(status, stop));
                Assert.NotEmpty(lines);
                Assert.All(lines, l => Assert.NotNull(l));
            }
    }
}
