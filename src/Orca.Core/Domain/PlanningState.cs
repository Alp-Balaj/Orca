using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orca.Core.Domain;

public enum PlanningRunStatus
{
    /// <summary>Attempts exist but no parsed, validated plan does yet — re-running the
    /// planner resumes from here with continued attempt numbering.</summary>
    InProgress,

    /// <summary>A schema-valid plan file exists in the vault; regenerating over it is
    /// refused.</summary>
    Succeeded,

    /// <summary>The last run stopped without a valid plan (invocation failure or retry
    /// exhaustion); re-running the planner resumes.</summary>
    Failed,
}

/// <summary>One Planner invocation's evidence. <see cref="ParseFailure"/> is non-null when
/// the invocation itself succeeded but the produced file failed parse/validation — the
/// case that feeds the §5.4 Reflexion retry; every other failure is a distinct
/// <see cref="StopReason"/> and is never retried automatically.</summary>
public sealed record PlanningAttempt
{
    /// <summary>Which role this invocation ran — "planner" or "scout" (SUG-01) for the
    /// single-candidate path; multi-candidate stages are "candidate-<i>", "critic",
    /// "synthesis" (SUG-13). Defaults to "planner" so state written before these roles
    /// existed deserializes unchanged.</summary>
    public string Role { get; init; } = "planner";

    public required int Attempt { get; init; }
    public required InvocationStopReason StopReason { get; init; }
    public required string? ParseFailure { get; init; }
    public required string? SessionId { get; init; }
    public required decimal CostUsd { get; init; }

    /// <summary>SUG-06: this invocation's token usage (null when no result event arrived
    /// — a timeout/crash — or for state written before this field existed).</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>SHA-256 of the fully assembled prompt this attempt ran with (retry
    /// prompts differ from the initial one); the role prompt file's own hash lives in
    /// <see cref="PlanningState.PromptFileHashes"/>.</summary>
    public required string PromptSha256 { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
}

/// <summary>Everything needed to resume plan generation cold — the planning twin of
/// <see cref="RunState"/>, stored as gitignored JSON at
/// <c>.orca/state/&lt;plan-id&gt;.planning.json</c> in the target repo (owner-approved
/// schema, 2026-07-06). Attempts are append-only history; a rate-limited or auth-expired
/// run resumes later with continued numbering instead of silently waiting.</summary>
public sealed record PlanningState
{
    public required int SchemaVersion { get; init; }
    public required string PlanId { get; init; }
    public required PlanningStrategyKind Strategy { get; init; }
    public required PlanningRunStatus Status { get; init; }
    public required IReadOnlyList<PlanningAttempt> Attempts { get; init; }

    /// <summary>Role name → SHA-256 of the versioned prompt file in effect, same
    /// attribution contract as <see cref="RunState.PromptHashes"/>.</summary>
    public required IReadOnlyDictionary<string, string> PromptFileHashes { get; init; }

    public required decimal AccumulatedCostUsd { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public static class PlanningStateSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // snake_case enum spellings ("single_candidate", "in_progress", "rate_limited"),
        // matching the vault schema and the run log's stop_reason field.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string ToJson(PlanningState state) => JsonSerializer.Serialize(state, Options);

    public static PlanningState FromJson(string json) =>
        JsonSerializer.Deserialize<PlanningState>(json, Options)
        ?? throw new JsonException("planning state JSON deserialized to null");
}
