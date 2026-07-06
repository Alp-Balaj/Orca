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

    /// <summary>Non-null iff <see cref="Type"/> is <c>result</c>.</summary>
    public ClaudeResultSummary? Result { get; init; }

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
            RawLine = line,
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

    internal static int? OptionalInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
