using System.Text.Json;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

public sealed class RunStateStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    public RunStateStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static RunState State(int attempt = 1) => new()
    {
        SchemaVersion = 1,
        PlanId = "recipe-tagging",
        PlanVersion = 1,
        CheckpointIndex = 0,
        CheckpointId = "01-tag-entity",
        Attempt = attempt,
        Branch = "orca/recipe-tagging/01-tag-entity",
        SessionId = null,
        PromptHashes = new Dictionary<string, string> { ["implementer"] = "abc" },
        AccumulatedCostUsd = 0m,
        LastUsage = null,
        StartedAt = new DateTimeOffset(2026, 7, 6, 18, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 7, 6, 18, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Round_trips_under_the_expected_gitignored_path()
    {
        var store = new RunStateStore(_root);

        var path = store.Write(State());

        Assert.Equal(Path.Combine(_root, ".orca", "state", "recipe-tagging.json"), path);
        // Record equality would compare the hash dictionary by reference; compare the
        // JSON projection instead.
        Assert.Equal(RunStateSerializer.ToJson(State()),
            RunStateSerializer.ToJson(store.Read("recipe-tagging")!));
    }

    [Fact]
    public void Missing_state_reads_as_null_not_an_error() =>
        Assert.Null(new RunStateStore(_root).Read("recipe-tagging"));

    [Fact]
    public void Overwrites_are_the_point_state_is_mutable()
    {
        var store = new RunStateStore(_root);
        store.Write(State(attempt: 1));
        store.Write(State(attempt: 2));

        Assert.Equal(2, store.Read("recipe-tagging")!.Attempt);
    }

    [Fact]
    public void Malformed_state_fails_loudly_with_the_path()
    {
        var store = new RunStateStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".orca", "state"));
        File.WriteAllText(Path.Combine(_root, ".orca", "state", "recipe-tagging.json"), "{ half a state");

        var e = Assert.Throws<JsonException>(() => store.Read("recipe-tagging"));
        Assert.Contains("recipe-tagging.json", e.Message);
    }

    [Fact]
    public void Plan_ids_are_validated_before_touching_paths() =>
        Assert.ThrowsAny<ArgumentException>(() => new RunStateStore(_root).Read("../escape"));
}
