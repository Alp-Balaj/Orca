using System.Text.Json;
using Orca.Core.Domain;

namespace Orca.Tests;

public class RunStateTests
{
    private static RunState Sample() => new()
    {
        SchemaVersion = 1,
        PlanId = "recipe-tagging",
        PlanVersion = 1,
        CheckpointIndex = 0,
        CheckpointId = "01-tag-entity",
        Attempt = 2,
        Branch = "orca/recipe-tagging/01-tag-entity",
        SessionId = "d2f1c9e0-0000-0000-0000-000000000000",
        PromptHashes = new Dictionary<string, string>
        {
            ["implementer"] = "3b8a...c1",
            ["verifier"] = "9f04...7e",
        },
        AccumulatedCostUsd = 1.37m,
        LastUsage = null,
        StartedAt = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 7, 6, 16, 5, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Round_trips_through_camel_case_json()
    {
        var json = RunStateSerializer.ToJson(Sample());

        Assert.Contains("\"planId\"", json);
        Assert.Contains("\"accumulatedCostUsd\"", json);

        var restored = RunStateSerializer.FromJson(json);

        Assert.Equal(json, RunStateSerializer.ToJson(restored));
        Assert.Equal("orca/recipe-tagging/01-tag-entity", restored.Branch);
        Assert.Equal(1.37m, restored.AccumulatedCostUsd);
        Assert.Equal("3b8a...c1", restored.PromptHashes["implementer"]);
    }

    [Fact]
    public void Round_trips_the_last_usage_block()
    {
        var state = Sample() with
        {
            LastUsage = new TokenUsage
            {
                InputTokens = 2716,
                OutputTokens = 206,
                CacheCreationInputTokens = 5289,
                CacheReadInputTokens = 16913,
            },
        };

        var json = RunStateSerializer.ToJson(state);

        Assert.Contains("\"lastUsage\"", json);
        Assert.Contains("\"cacheReadInputTokens\"", json);

        var restored = RunStateSerializer.FromJson(json);

        Assert.Equal(state.LastUsage, restored.LastUsage);
        Assert.Equal(json, RunStateSerializer.ToJson(restored));
    }

    [Fact]
    public void Rejects_unknown_members_inside_last_usage()
    {
        var json = RunStateSerializer.ToJson(Sample() with
        {
            LastUsage = new TokenUsage
            {
                InputTokens = 1,
                OutputTokens = 1,
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0,
            },
        });
        var withExtra = json.Replace("\"inputTokens\": 1", "\"inputTokens\": 1, \"surprise\": true");

        Assert.ThrowsAny<JsonException>(() => RunStateSerializer.FromJson(withExtra));
    }

    [Fact]
    public void Rejects_unknown_json_members()
    {
        var json = RunStateSerializer.ToJson(Sample());
        var withExtra = json[..json.LastIndexOf('}')] + ",\"surprise\": true}";

        Assert.ThrowsAny<JsonException>(() => RunStateSerializer.FromJson(withExtra));
    }

    [Fact]
    public void Rejects_json_missing_a_required_member()
    {
        var json = RunStateSerializer.ToJson(Sample());
        var start = json.IndexOf("\"branch\"", StringComparison.Ordinal);
        var end = json.IndexOf('\n', start) + 1;
        var withoutBranch = json[..start] + json[end..];

        Assert.ThrowsAny<JsonException>(() => RunStateSerializer.FromJson(withoutBranch));
    }
}
