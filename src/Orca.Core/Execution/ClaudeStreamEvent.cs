using System.Text.Json;

namespace Orca.Core.Execution;

/// <summary>One parsed NDJSON event from the <c>stream-json</c> output.
/// <para>The type union is <b>open</b>: 2.1.201 emits undocumented types
/// (<c>rate_limit_event</c> appears in perfectly successful runs — see the success
/// fixture), so unknown types pass through untouched with only <see cref="Type"/>
/// populated. Fail-loud strictness applies to the fields Orca actually consumes: a
/// <c>system</c>/<c>init</c> must carry a session id, a <c>result</c> must carry the
/// full <see cref="ClaudeResultSummary"/> field set.</para></summary>
public sealed record ClaudeStreamEvent
{
    public required string Type { get; init; }
    public string? Subtype { get; init; }
    public string? SessionId { get; init; }

    /// <summary>Top-level <c>claude_code_version</c> on a <c>system</c>/<c>init</c> event
    /// (sibling to <see cref="SessionId"/>) — read leniently like every other init field
    /// here: absence yields null, never a parse failure (SUG-15).</summary>
    public string? ClaudeCodeVersion { get; init; }

    /// <summary>Top-level <c>model</c> on a <c>system</c>/<c>init</c> event — the model
    /// the CLI actually resolved to for this invocation, not merely what
    /// <c>ClaudeInvocationOptions.Model</c> requested (SUG-05: attributable actuals).
    /// Read leniently like every other init field here.</summary>
    public string? Model { get; init; }

    /// <summary>Non-null iff <see cref="Type"/> is <c>result</c>.</summary>
    public ClaudeResultSummary? Result { get; init; }

    /// <summary>Top-level <c>error</c> field — observed as <c>"authentication_failed"</c>
    /// on the synthetic <c>assistant</c> event both auth fixtures carry. Machine-readable
    /// auth evidence, unlike the human-facing <c>result</c> text.</summary>
    public string? Error { get; init; }

    /// <summary>Non-null iff <see cref="Type"/> is <c>rate_limit_event</c> and the event
    /// carried a readable <c>rate_limit_info.status</c>.</summary>
    public RateLimitInfo? RateLimit { get; init; }

    /// <summary>The line exactly as received; the run log holds it verbatim.</summary>
    public required string RawLine { get; init; }

    public bool IsInit => Type == "system" && Subtype == "init";

    public static ClaudeStreamEvent Parse(string line)
    {
        using var document = StreamJson.ParseObject(line);
        var root = document.RootElement;

        var type = StreamJson.RequireString(root, "type", line);
        var subtype = StreamJson.OptionalString(root, "subtype");
        var sessionId = StreamJson.OptionalString(root, "session_id");

        if (type == "system" && subtype == "init" && string.IsNullOrEmpty(sessionId))
            throw new StreamParseException(line, "system/init event is missing 'session_id'");

        return new ClaudeStreamEvent
        {
            Type = type,
            Subtype = subtype,
            SessionId = sessionId,
            Result = type == "result" ? ClaudeResultSummary.Parse(root, line) : null,
            Error = StreamJson.OptionalString(root, "error"),
            RateLimit = type == "rate_limit_event" ? RateLimitInfo.Parse(root) : null,
            ClaudeCodeVersion = StreamJson.OptionalString(root, "claude_code_version"),
            Model = StreamJson.OptionalString(root, "model"),
            RawLine = line,
        };
    }
}

/// <summary>The fields Orca reads from a <c>rate_limit_event</c>'s <c>rate_limit_info</c>
/// block. This event is undocumented diagnostic output that appears in perfectly healthy
/// runs (<c>status: "allowed"</c> in every capture), so unlike the <c>result</c> contract
/// it is read leniently: a shape drift yields no signal rather than crashing a successful
/// run — the raw line is in the run log either way.</summary>
public sealed record RateLimitInfo
{
    public required string Status { get; init; }
    public long? ResetsAt { get; init; }
    public string? RateLimitType { get; init; }

    internal static RateLimitInfo? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;
        if (StreamJson.OptionalString(info, "status") is not { } status)
            return null;
        return new RateLimitInfo
        {
            Status = status,
            ResetsAt = StreamJson.OptionalLong(info, "resetsAt"),
            RateLimitType = StreamJson.OptionalString(info, "rateLimitType"),
        };
    }
}

/// <summary>Strict field accessors shared by the stream-event parsers. Every failure
/// names the field and carries the offending line.</summary>
internal static class StreamJson
{
    internal static JsonDocument ParseObject(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException e)
        {
            throw new StreamParseException(line, "not valid JSON", e);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            var kind = document.RootElement.ValueKind;
            document.Dispose();
            throw new StreamParseException(line, $"expected a JSON object, got {kind}");
        }

        return document;
    }

    internal static JsonElement RequireObject(JsonElement parent, string name, string line) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new StreamParseException(line, $"missing or non-object '{name}'");

    internal static string RequireString(JsonElement parent, string name, string line) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : throw new StreamParseException(line, $"missing or empty '{name}'");

    internal static bool RequireBool(JsonElement parent, string name, string line) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new StreamParseException(line, $"missing or non-boolean '{name}'");

    internal static int RequireInt(JsonElement parent, string name, string line) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : throw new StreamParseException(line, $"missing or non-integer '{name}'");

    internal static long RequireLong(JsonElement parent, string name, string line) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : throw new StreamParseException(line, $"missing or non-integer '{name}'");

    internal static decimal RequireDecimal(JsonElement parent, string name, string line) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number)
            ? number
            : throw new StreamParseException(line, $"missing or non-numeric '{name}'");

    internal static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static long? OptionalLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    internal static int? OptionalInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
