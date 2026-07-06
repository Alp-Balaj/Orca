using Orca.Core.Git;

namespace Orca.Tests;

public class CheckpointCommitMessageTests
{
    [Fact]
    public void Subject_is_greppable_and_the_trailer_carries_attribution()
    {
        var message = CheckpointCommitMessage.Build(
            "recipe-tagging", 1, "01-tag-entity", "Add Tag entity + EF mapping",
            attempt: 2, sessionId: "0f058114-37ac-4e08-a46e-b2e1dfd7b83b", costUsd: 0.16m);

        var lines = message.Split('\n');
        Assert.Equal("orca: recipe-tagging/01-tag-entity attempt 2", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Contains("Plan: recipe-tagging v1", lines);
        Assert.Contains("Checkpoint: 01-tag-entity — Add Tag entity + EF mapping", lines);
        Assert.Contains("Session: 0f058114-37ac-4e08-a46e-b2e1dfd7b83b", lines);
        Assert.Contains("Cost: $0.16", lines);
    }

    [Fact]
    public void Absent_session_and_cost_say_so_rather_than_lying()
    {
        var message = CheckpointCommitMessage.Build(
            "p", 1, "01-c", "desc", attempt: 1, sessionId: null, costUsd: null);

        Assert.Contains("Session: none", message);
        Assert.Contains("Cost: unknown", message);
    }

    [Fact]
    public void Cost_renders_invariantly() =>
        Assert.Contains("Cost: $1234.5678",
            CheckpointCommitMessage.Build("p", 1, "01-c", "d", 1, null, 1234.5678m));

    [Theory]
    [InlineData("Bad Plan")]
    [InlineData("")]
    public void Rejects_invalid_plan_ids(string planId) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            CheckpointCommitMessage.Build(planId, 1, "01-c", "d", 1, null, null));

    [Fact]
    public void Rejects_an_invalid_checkpoint_id() =>
        Assert.ThrowsAny<ArgumentException>(() =>
            CheckpointCommitMessage.Build("p", 1, "no-prefix", "d", 1, null, null));
}

public class CheckpointBranchTests
{
    [Fact]
    public void Uses_the_approved_naming_convention() =>
        Assert.Equal("orca/recipe-tagging/01-tag-entity",
            CheckpointBranch.Name("recipe-tagging", "01-tag-entity"));

    [Theory]
    [InlineData("Recipe", "01-x")]
    [InlineData("recipe", "1-x")]
    [InlineData("recipe", "01_x")]
    public void Rejects_ids_the_schema_rejects(string planId, string checkpointId) =>
        Assert.ThrowsAny<ArgumentException>(() => CheckpointBranch.Name(planId, checkpointId));
}
