using System.Text.Json;
using Orca.Core.Domain;
using Orca.Core.Planning;

namespace Orca.Tests;

public class PlanningStateTests
{
    private static PlanningState Sample() => new()
    {
        SchemaVersion = 1,
        PlanId = "recipe-tagging",
        Strategy = PlanningStrategyKind.SingleCandidate,
        Status = PlanningRunStatus.InProgress,
        Attempts =
        [
            new PlanningAttempt
            {
                Attempt = 1,
                StopReason = InvocationStopReason.Succeeded,
                ParseFailure = "planning: 'candidates' must be absent when strategy is single_candidate",
                SessionId = "s-1",
                CostUsd = 0.31m,
                PromptSha256 = "9f2c...aa",
                StartedAt = new DateTimeOffset(2026, 7, 6, 21, 10, 0, TimeSpan.Zero),
                EndedAt = new DateTimeOffset(2026, 7, 6, 21, 12, 40, TimeSpan.Zero),
            },
            new PlanningAttempt
            {
                Attempt = 2,
                StopReason = InvocationStopReason.RateLimited,
                ParseFailure = null,
                SessionId = null,
                CostUsd = 0m,
                PromptSha256 = "1b40...7d",
                StartedAt = new DateTimeOffset(2026, 7, 6, 21, 13, 0, TimeSpan.Zero),
                EndedAt = new DateTimeOffset(2026, 7, 6, 21, 13, 5, TimeSpan.Zero),
            },
        ],
        PromptFileHashes = new Dictionary<string, string> { ["planner"] = "3a41...c9" },
        AccumulatedCostUsd = 0.31m,
        CreatedAt = new DateTimeOffset(2026, 7, 6, 21, 10, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 7, 6, 21, 13, 5, TimeSpan.Zero),
    };

    [Fact]
    public void Round_trips_with_snake_case_enum_spellings()
    {
        var json = PlanningStateSerializer.ToJson(Sample());

        // camelCase keys, snake_case enum values — the approved schema.
        Assert.Contains("\"planId\"", json);
        Assert.Contains("\"single_candidate\"", json);
        Assert.Contains("\"in_progress\"", json);
        Assert.Contains("\"rate_limited\"", json);
        Assert.Contains("\"stopReason\": \"succeeded\"", json);

        var restored = PlanningStateSerializer.FromJson(json);

        Assert.Equal(json, PlanningStateSerializer.ToJson(restored));
        Assert.Equal(2, restored.Attempts.Count);
        Assert.Equal(InvocationStopReason.RateLimited, restored.Attempts[1].StopReason);
        Assert.Equal("3a41...c9", restored.PromptFileHashes["planner"]);
    }

    [Fact]
    public void Rejects_unknown_json_members()
    {
        var json = PlanningStateSerializer.ToJson(Sample());
        var withExtra = json[..json.LastIndexOf('}')] + ",\"surprise\": true}";

        Assert.ThrowsAny<JsonException>(() => PlanningStateSerializer.FromJson(withExtra));
    }

    [Fact]
    public void Rejects_an_unknown_enum_spelling()
    {
        var json = PlanningStateSerializer.ToJson(Sample())
            .Replace("\"rate_limited\"", "\"rate-limited\"");

        Assert.ThrowsAny<JsonException>(() => PlanningStateSerializer.FromJson(json));
    }

    [Fact]
    public void Rejects_json_missing_a_required_member()
    {
        var json = PlanningStateSerializer.ToJson(Sample());
        var start = json.IndexOf("\"accumulatedCostUsd\"", StringComparison.Ordinal);
        var end = json.IndexOf('\n', start) + 1;
        var without = json[..start] + json[end..];

        Assert.ThrowsAny<JsonException>(() => PlanningStateSerializer.FromJson(without));
    }
}

public class PlanningStateStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("orca-planning-state-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PlanningState State() => new()
    {
        SchemaVersion = 1,
        PlanId = "recipe-tagging",
        Strategy = PlanningStrategyKind.SingleCandidate,
        Status = PlanningRunStatus.Failed,
        Attempts = [],
        PromptFileHashes = new Dictionary<string, string> { ["planner"] = "3a41...c9" },
        AccumulatedCostUsd = 0m,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Writes_beside_the_run_state_without_colliding()
    {
        var store = new PlanningStateStore(_root);
        var path = store.Write(State());

        Assert.Equal(Path.Combine(_root, ".orca", "state", "recipe-tagging.planning.json"), path);
        Assert.Equal(
            PlanningStateSerializer.ToJson(State()),
            PlanningStateSerializer.ToJson(store.Read("recipe-tagging")!));
    }

    [Fact]
    public void Read_returns_null_when_no_planning_has_started() =>
        Assert.Null(new PlanningStateStore(_root).Read("recipe-tagging"));

    [Fact]
    public void Read_throws_on_malformed_state_naming_the_file()
    {
        var store = new PlanningStateStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".orca", "state"));
        File.WriteAllText(store.PathFor("recipe-tagging"), "{\"not\": \"planning state\"}");

        var e = Assert.ThrowsAny<JsonException>(() => store.Read("recipe-tagging"));

        Assert.Contains("recipe-tagging.planning.json", e.Message);
    }

    [Fact]
    public void Rejects_ids_that_fail_the_schema_regex() =>
        Assert.ThrowsAny<ArgumentException>(() => new PlanningStateStore(_root).Read("../escape"));
}
