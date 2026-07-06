using System.Text.Json;
using Orca.Core.Domain;

namespace Orca.Core.Execution;

/// <summary>The fields Orca consumes from the final <c>result</c> event. Everything here
/// is required in the observed 2.1.201 contract and parsed fail-loud.
/// <para><b>The verdict field is <see cref="IsError"/>, never <see cref="Subtype"/>:</b>
/// the CLI reports <c>subtype: "success"</c> even for authentication failures (see the
/// logged-out and invalid-api-key fixtures).</para></summary>
public sealed record ClaudeResultSummary
{
    public required string Subtype { get; init; }
    public required bool IsError { get; init; }
    public required string SessionId { get; init; }
    public required int NumTurns { get; init; }
    public required long DurationMs { get; init; }
    public required decimal TotalCostUsd { get; init; }
    public required TokenUsage Usage { get; init; }

    /// <summary>HTTP status on API-level failures (401 in the invalid-api-key fixture);
    /// JSON null on success.</summary>
    public int? ApiErrorStatus { get; init; }

    /// <summary>The CLI's final text — on auth failures this carries the human-readable
    /// reason ("Not logged in · Please run /login").</summary>
    public string? ResultText { get; init; }

    internal static ClaudeResultSummary Parse(JsonElement root, string line)
    {
        var usage = StreamJson.RequireObject(root, "usage", line);
        return new ClaudeResultSummary
        {
            Subtype = StreamJson.RequireString(root, "subtype", line),
            IsError = StreamJson.RequireBool(root, "is_error", line),
            SessionId = StreamJson.RequireString(root, "session_id", line),
            NumTurns = StreamJson.RequireInt(root, "num_turns", line),
            DurationMs = StreamJson.RequireLong(root, "duration_ms", line),
            TotalCostUsd = StreamJson.RequireDecimal(root, "total_cost_usd", line),
            Usage = new TokenUsage
            {
                InputTokens = StreamJson.RequireLong(usage, "input_tokens", line),
                OutputTokens = StreamJson.RequireLong(usage, "output_tokens", line),
                CacheCreationInputTokens = StreamJson.RequireLong(usage, "cache_creation_input_tokens", line),
                CacheReadInputTokens = StreamJson.RequireLong(usage, "cache_read_input_tokens", line),
            },
            ApiErrorStatus = StreamJson.OptionalInt(root, "api_error_status"),
            ResultText = StreamJson.OptionalString(root, "result"),
        };
    }
}
