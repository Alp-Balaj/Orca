using Orca.Core.Planning;
using Orca.Core.Run;

namespace Orca.Tests;

public class PlannerPromptTests
{
    private static PlanningRequest Request(string? title = null) => new()
    {
        PlanId = "recipe-tagging",
        Goal = "Add tag support to recipes\nwith filtering in the list view",
        Title = title,
        TargetRepoName = "RecipeApp",
        BaseBranch = "main",
    };

    [Fact]
    public void The_role_prompt_is_embedded_and_hashed()
    {
        // The prompt rides the assembly (the packed tool has no /prompts on disk); the
        // recorded hash is of exactly this content.
        Assert.Contains("Frontmatter contract", PlannerPrompt.RoleText);
        Assert.Contains("status` is exactly `draft", PlannerPrompt.RoleText);
        Assert.Equal(CheckpointPrompt.Sha256(PlannerPrompt.RoleText), PlannerPrompt.RoleSha256);
        Assert.Equal(64, PlannerPrompt.RoleSha256.Length);
    }

    [Fact]
    public void The_role_prompt_documents_the_per_checkpoint_model_and_budget_overrides()
    {
        // SUG-05: the schema accepting `model`/`max_budget_usd` only matters if the
        // Planner is actually told about them — otherwise "no other keys" (the prompt's
        // own older wording) would keep it from ever using the feature.
        Assert.Contains("max_budget_usd", PlannerPrompt.RoleText);
        Assert.Contains("use sparingly", PlannerPrompt.RoleText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_initial_prompt_carries_the_full_request_block()
    {
        var prompt = PlannerPrompt.AssembleInitial(
            Request(), "Plans/recipe-tagging/recipe-tagging.md", "2026-07-06T21:10:00Z");

        Assert.StartsWith("# Orca Planner", prompt);
        Assert.Contains("- plan_id: recipe-tagging", prompt);
        Assert.Contains("- target_repo: RecipeApp", prompt);
        Assert.Contains("- base_branch: main", prompt);
        Assert.Contains("2026-07-06T21:10:00Z", prompt);
        Assert.Contains("Plans/recipe-tagging/recipe-tagging.md", prompt);
        Assert.Contains("Add tag support to recipes\nwith filtering in the list view", prompt);
        Assert.DoesNotContain("title to use verbatim", prompt);
        Assert.DoesNotContain("## Retry", prompt);
    }

    [Fact]
    public void A_requested_title_becomes_a_verbatim_constraint()
    {
        var prompt = PlannerPrompt.AssembleInitial(
            Request(title: "Recipe tagging"), "Plans/recipe-tagging/recipe-tagging.md", "2026-07-06T21:10:00Z");

        Assert.Contains("- title to use verbatim: Recipe tagging", prompt);
    }

    [Fact]
    public void The_retry_prompt_embeds_the_exact_error_and_the_fix_in_place_instruction()
    {
        const string error = "recipe-tagging.md: invalid plan status 'aproved' (expected draft|approved|…)";
        var prompt = PlannerPrompt.AssembleRetry(
            Request(), "Plans/recipe-tagging/recipe-tagging.md", "2026-07-06T21:10:00Z", error);

        Assert.Contains("## Retry — your previous output failed validation", prompt);
        Assert.Contains(error, prompt);
        Assert.Contains("write only that file", prompt, StringComparison.OrdinalIgnoreCase);
        // Identical request block either way — the retry differs only by the appendix.
        Assert.Contains("- plan_id: recipe-tagging", prompt);
    }
}
