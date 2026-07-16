using Orca.Core.Planning;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>SUG-08: the Interviewer's goal note — strict frontmatter, contracted
/// sections, and the open-questions convention (`- [ ]` task items the human resolves
/// in place; plan-generate refuses while any remain).</summary>
public sealed class GoalNoteParserTests
{
    private const string Path = "/vault/Plans/recipe-tagging/recipe-tagging.goal.md";

    private const string Valid =
        """
        ---
        schema_version: 1
        type: goal
        plan_id: recipe-tagging
        created: 2026-07-17T10:00:00Z
        ---

        ## Outcome

        Tag CRUD exists on the API; recipes filter by tag in the list view.

        ## Constraints

        .NET 10, EF Core, existing controller conventions.

        ## Non-goals

        No tag colors, no tag merging UI.

        ## Checkpoint guidance

        Entity+migration first; criteria checkable from diff + green gates.

        ## Open questions

        - [ ] Should tags be user-scoped or global?
        - [ ] Hard delete or soft delete?
        """;

    [Fact]
    public void Parses_and_surfaces_the_unanswered_questions()
    {
        var note = GoalNoteParser.Parse(Valid, Path);

        Assert.Equal("recipe-tagging", note.PlanId);
        Assert.Equal(
            ["Should tags be user-scoped or global?", "Hard delete or soft delete?"],
            note.UnansweredQuestions);
    }

    [Fact]
    public void Answered_questions_no_longer_count()
    {
        var answered = Valid
            .Replace("- [ ] Should tags be user-scoped or global?", "User-scoped, per the 2026-07-17 decision.")
            .Replace("- [ ] Hard delete or soft delete?", "- [x] Hard delete or soft delete? — hard delete.");

        var note = GoalNoteParser.Parse(answered, Path);

        Assert.Empty(note.UnansweredQuestions);
    }

    [Fact]
    public void Layout_puts_the_goal_note_next_to_the_plan_file()
    {
        var layout = new VaultLayout("/vault", "Plans");
        Assert.Equal(
            System.IO.Path.Combine("/vault", "Plans", "recipe-tagging", "recipe-tagging.goal.md"),
            layout.GoalFile("recipe-tagging"));
    }

    [Fact]
    public void Rejects_a_wrong_type_and_a_missing_section()
    {
        Assert.Contains("goal", Assert.Throws<VaultSchemaException>(() =>
            GoalNoteParser.Parse(Valid.Replace("type: goal", "type: audit"), Path)).Message);
        Assert.Contains("'## Non-goals'", Assert.Throws<VaultSchemaException>(() =>
            GoalNoteParser.Parse(Valid.Replace("## Non-goals", "## Antigoals"), Path)).Message);
    }

    [Fact]
    public void Rejects_a_plan_id_that_does_not_match_the_file_stem()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            GoalNoteParser.Parse(Valid, "/vault/Plans/other/other.goal.md"));
        Assert.Contains("file name stem", ex.Message);
    }

    [Fact]
    public void Interviewer_role_prompt_contracts_the_sections_and_the_checkbox_convention()
    {
        foreach (var section in GoalNoteParser.RequiredSections)
            Assert.Contains(section, InterviewerPrompt.RoleText);
        Assert.Contains("- [ ]", InterviewerPrompt.RoleText);
        Assert.Contains("type: goal", InterviewerPrompt.RoleText);
    }

    [Fact]
    public void Interviewer_prompt_carries_the_raw_goal_and_the_relative_path()
    {
        var prompt = InterviewerPrompt.AssembleInitial(
            "recipe-tagging", "add tags somehow??", "Plans/recipe-tagging/recipe-tagging.goal.md",
            "2026-07-17T10:00:00Z");

        Assert.Contains("# Orca Interviewer", prompt);
        Assert.Contains("add tags somehow??", prompt);
        Assert.Contains("Plans/recipe-tagging/recipe-tagging.goal.md", prompt);
    }
}
