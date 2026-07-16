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

    // --- SUG-03 stage A: depends_on ---

    private static string PlanWithCheckpoints(string checkpointsYaml) => $"""
        ---
        schema_version: 1
        plan_id: recipe-tagging
        plan_version: 1
        title: "Recipe tagging"
        status: approved
        target_repo: "RecipeApp"
        base_branch: "main"
        created: 2026-07-06T14:00:00Z
        updated: 2026-07-06T14:00:00Z
        planning:
          strategy: single_candidate
        checkpoints:
        {checkpointsYaml}
        ---

        Body.
        """;

    private static Plan ParseText(string text) => PlanParser.Parse(text, "/vault/Plans/recipe-tagging/recipe-tagging.md");

    [Fact]
    public void Depends_on_parses_including_the_explicit_empty_list()
    {
        var plan = ParseText(PlanWithCheckpoints("""
              - id: 01-a
                description: a
                status: pending
                acceptance_criteria: [works]
              - id: 02-b
                description: b
                status: pending
                acceptance_criteria: [works]
                depends_on: []
              - id: 03-c
                description: c
                status: pending
                acceptance_criteria: [works]
                depends_on: [01-a, 02-b]
            """));

        Assert.Null(plan.Checkpoints[0].DependsOn);
        Assert.Equal([], plan.Checkpoints[1].DependsOn);
        Assert.Equal(["01-a", "02-b"], plan.Checkpoints[2].DependsOn);
    }

    [Theory]
    [InlineData("depends_on: [02-b]", "must not name the checkpoint itself")]
    [InlineData("depends_on: [99-nope]", "not an earlier checkpoint")]
    [InlineData("depends_on: [03-c]", "not an earlier checkpoint")]
    [InlineData("depends_on: [01-a, 01-a]", "more than once")]
    public void Depends_on_violations_are_rejected_with_the_reason(string dependsLine, string reason)
    {
        var text = PlanWithCheckpoints($"""
              - id: 01-a
                description: a
                status: pending
                acceptance_criteria: [works]
              - id: 02-b
                description: b
                status: pending
                acceptance_criteria: [works]
                {dependsLine}
              - id: 03-c
                description: c
                status: pending
                acceptance_criteria: [works]
            """);

        var ex = Assert.Throws<VaultSchemaException>(() => ParseText(text));
        Assert.Contains(reason, ex.Message);
    }
}
