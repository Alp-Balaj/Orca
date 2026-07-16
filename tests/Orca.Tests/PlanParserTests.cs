using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Tests;

public class PlanParserTests
{
    private static string FixturePath(string subdir, string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plans", subdir, name);

    private static Plan ParseFixture(string subdir, string name)
    {
        var path = FixturePath(subdir, name);
        return PlanParser.Parse(File.ReadAllText(path), path);
    }

    [Fact]
    public void Parses_a_valid_single_candidate_plan()
    {
        var plan = ParseFixture("valid", "recipe-tagging.md");

        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal("recipe-tagging", plan.PlanId);
        Assert.Equal(1, plan.PlanVersion);
        Assert.Equal("Add tagging to recipes", plan.Title);
        Assert.Equal(PlanStatus.Approved, plan.Status);
        Assert.Equal("RecipeApp", plan.TargetRepo);
        Assert.Equal("main", plan.BaseBranch);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero), plan.Created);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 15, 30, 0, TimeSpan.Zero), plan.Updated);

        Assert.Equal(PlanningStrategyKind.SingleCandidate, plan.Planning.Strategy);
        Assert.Null(plan.Planning.Candidates);
        Assert.Null(plan.Planning.Selected);
        Assert.Null(plan.Planning.Rationale);

        Assert.Equal(2, plan.Checkpoints.Count);
        Assert.Equal("01-tag-entity", plan.Checkpoints[0].Id);
        Assert.Equal("Add Tag entity + EF mapping", plan.Checkpoints[0].Description);
        Assert.Equal(CheckpointStatus.Complete, plan.Checkpoints[0].Status);
        Assert.Equal(
            ["Tag entity with unique (UserId, Name)", "dotnet build clean, migration added but never applied"],
            plan.Checkpoints[0].AcceptanceCriteria);
        Assert.Equal("02-tag-endpoints", plan.Checkpoints[1].Id);
        Assert.Equal(CheckpointStatus.InProgress, plan.Checkpoints[1].Status);

        Assert.Contains("Orca never parses this prose", plan.Body);
    }

    [Fact]
    public void Parses_a_valid_multi_candidate_plan_and_tolerates_obsidian_keys()
    {
        var plan = ParseFixture("valid", "multi-endpoints.md");

        Assert.Equal(PlanStatus.Draft, plan.Status);
        Assert.Equal(2, plan.PlanVersion);
        Assert.Equal(PlanningStrategyKind.MultiCandidate, plan.Planning.Strategy);
        Assert.Equal(3, plan.Planning.Candidates);
        Assert.Equal(2, plan.Planning.Selected);
        Assert.Equal("Candidate B isolates EF changes behind the existing repository seam", plan.Planning.Rationale);
        Assert.Equal(TimeSpan.FromHours(2), plan.Created.Offset);
        Assert.Single(plan.Checkpoints);
    }

    [Fact]
    public void Parses_a_per_checkpoint_model_and_budget_override()
    {
        var plan = ParseFixture("valid", "checkpoint-model-override.md");

        Assert.Equal("opus", plan.Checkpoints[0].Model);
        Assert.Equal(8m, plan.Checkpoints[0].MaxBudgetUsd);

        // The ordinary case: no override on the second checkpoint.
        Assert.Null(plan.Checkpoints[1].Model);
        Assert.Null(plan.Checkpoints[1].MaxBudgetUsd);
    }

    [Fact]
    public void Accepts_crlf_line_endings_and_preserves_them_in_the_body()
    {
        var path = FixturePath("valid", "recipe-tagging.md");
        var text = File.ReadAllText(path).Replace("\r\n", "\n").Replace("\n", "\r\n");

        var plan = PlanParser.Parse(text, path);

        Assert.Equal(2, plan.Checkpoints.Count);
        Assert.Contains("\r\n", plan.Body);
    }

    public static TheoryData<string, string> MalformedFixtures => new()
    {
        { "01-no-frontmatter.md", "must start with a '---'" },
        { "02-unclosed-frontmatter.md", "never closed" },
        { "03-unknown-key.md", "unknown key" },
        { "04-missing-title.md", "missing required key 'title'" },
        { "05-bad-status.md", "invalid plan status" },
        { "06-acceptance-paragraph.md", "must be a list" },
        { "07-empty-acceptance.md", "must be a non-empty list" },
        { "08-duplicate-checkpoint-ids.md", "duplicate checkpoint id" },
        { "09-non-increasing-checkpoints.md", "strictly increasing" },
        { "10-plan-id-mismatch.md", "must equal the file name stem" },
        { "11-single-with-candidates.md", "must be absent" },
        { "12-multi-missing-rationale.md", "missing required key 'rationale'" },
        { "13-bad-schema-version.md", "unsupported schema_version" },
        { "14-bad-timestamp.md", "ISO-8601" },
        { "15-bad-checkpoint-id.md", "must match" },
        { "16-duplicate-key.md", "key" },
        { "17-non-positive-checkpoint-budget.md", "max_budget_usd" },
    };

    [Theory]
    [MemberData(nameof(MalformedFixtures))]
    public void Malformed_plans_fail_loudly(string fixture, string expectedMessagePart)
    {
        var path = FixturePath("malformed", fixture);
        var text = File.ReadAllText(path);

        var ex = Assert.Throws<VaultSchemaException>(() => PlanParser.Parse(text, path));

        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture, ex.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    // --- BF-14: hand-editing traps ---

    private static string ValidTextWith(string titleLine) => $"""
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        {titleLine}
        status: approved
        target_repo: "RecipeApp"
        base_branch: "main"
        created: 2026-07-06T14:00:00Z
        updated: 2026-07-06T14:00:00Z
        planning:
          strategy: single_candidate
        checkpoints:
          - id: 01-a
            description: a
            status: pending
            acceptance_criteria: [works]
        ---

        Body.
        """;

    [Fact]
    public void Unquoted_hash_is_caught_with_a_quote_the_value_message()
    {
        // BF-14 §1 (probe-confirmed): 'title: Fix #12 in parser' parsed as "Fix" with no
        // error anywhere, and the Verifier judged the truncated criterion.
        var ex = Assert.Throws<VaultSchemaException>(() =>
            PlanParser.Parse(ValidTextWith("title: Fix #12 in parser"), "/vault/Plans/recipe-tagging/recipe-tagging.md"));

        Assert.Contains("silently truncates", ex.Message);
        Assert.Contains("double-quote", ex.Message);
    }

    [Fact]
    public void A_quoted_hash_and_a_pure_comment_are_fine()
    {
        var plan = PlanParser.Parse(
            ValidTextWith("title: \"Fix #12 in parser\""), "/vault/Plans/recipe-tagging/recipe-tagging.md");
        Assert.Equal("Fix #12 in parser", plan.Title);
    }

    [Fact]
    public void A_value_starting_with_at_gets_a_human_message_not_yamls_cryptic_one()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            PlanParser.Parse(ValidTextWith("title: @scoped/package support"), "/vault/Plans/recipe-tagging/recipe-tagging.md"));

        Assert.Contains("cannot start a plain value with '@'", ex.Message);
    }

    [Fact]
    public void Block_scalar_titles_and_multiline_criteria_are_rejected()
    {
        // BF-14 §2 (probe-confirmed): multi-line scalars parsed fine and broke bullet
        // rendering, the byte-exact echo, and (for base_branch) git itself.
        var blockTitle = ValidTextWith("title: |\n  Two\n  lines");
        var ex = Assert.Throws<VaultSchemaException>(() =>
            PlanParser.Parse(blockTitle, "/vault/Plans/recipe-tagging/recipe-tagging.md"));
        Assert.Contains("single line", ex.Message);
    }

    [Fact]
    public void A_BOM_is_named_instead_of_the_generic_delimiter_error()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            PlanParser.Parse("\uFEFF" + ValidTextWith("title: t"), "/vault/Plans/recipe-tagging/recipe-tagging.md"));

        Assert.Contains("byte-order mark", ex.Message);
    }

    [Fact]
    public void Invariant_lint_flags_contradictions_without_refusing()
    {
        var plan = PlanParser.Parse(
            ValidTextWith("title: t").Replace("status: approved", "status: complete"),
            "/vault/Plans/recipe-tagging/recipe-tagging.md");

        var warnings = PlanParser.LintInvariants(plan);

        Assert.Contains(warnings, w => w.Contains("says complete but has non-complete"));
    }
}
