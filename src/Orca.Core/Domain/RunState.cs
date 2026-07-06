using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orca.Core.Domain;

/// <summary>Everything needed to resume a run cold — the §5.3 resumability contract,
/// stored as gitignored JSON at <c>.orca/state/&lt;plan-id&gt;.json</c> in the target repo.
/// <see cref="SessionId"/> supports within-checkpoint <c>--resume</c> only; cross-checkpoint
/// context flows exclusively through the vault.</summary>
public sealed record RunState
{
    public required int SchemaVersion { get; init; }
    public required string PlanId { get; init; }
    public required int PlanVersion { get; init; }
    public required int CheckpointIndex { get; init; }
    public required string CheckpointId { get; init; }
    public required int Attempt { get; init; }
    public required string Branch { get; init; }
    public required string? SessionId { get; init; }

    /// <summary>Role name → SHA-256 of the prompt file in effect, so behavioural changes
    /// stay attributable to a prompt change vs a model change.</summary>
    public required IReadOnlyDictionary<string, string> PromptHashes { get; init; }

    public required decimal AccumulatedCostUsd { get; init; }

    /// <summary>Token usage reported by the most recent executor invocation's
    /// <c>result</c> event; null until the first invocation completes. Added by
    /// checkpoint 04 (criterion: "usage … extracted into run state").</summary>
    public required TokenUsage? LastUsage { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public static class RunStateSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string ToJson(RunState state) => JsonSerializer.Serialize(state, Options);

    public static RunState FromJson(string json) =>
        JsonSerializer.Deserialize<RunState>(json, Options)
        ?? throw new JsonException("run state JSON deserialized to null");
}
