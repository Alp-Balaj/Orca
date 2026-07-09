using System.Globalization;
using System.Text.Json;

namespace Orca.Core.Execution;

/// <summary>Lenient projection of one parsed stream event into human-readable lines for
/// live rendering. Never throws, never gates anything: unknown or drifted shapes come
/// back as the bare event type. The strict §6 contract remains <see cref="ClaudeStreamEvent"/>'s —
/// assistant text and tool calls are read here from <see cref="ClaudeStreamEvent.RawLine"/>,
/// deliberately outside the fail-loud surface.</summary>
public static class StreamEventDisplay
{
    /// <summary>Display lines are clipped, never the evidence — the run log holds every
    /// raw line in full.</summary>
    private const int MaxTextLength = 160;

    public static IReadOnlyList<DisplayLine> Describe(ClaudeStreamEvent streamEvent)
    {
        List<DisplayLine>? lines = null;
        try
        {
            lines = DescribeKnown(streamEvent);
        }
        catch
        {
            // Lenient by contract: a drifted or malformed shape must never take down a
            // live pane.
        }

        return lines is { Count: > 0 }
            ? lines
            : [new DisplayLine { Kind = DisplayKind.Other, Text = BareType(streamEvent) }];
    }

    private static List<DisplayLine>? DescribeKnown(ClaudeStreamEvent streamEvent) => streamEvent switch
    {
        { IsInit: true } => [Line(DisplayKind.Init, InitText(streamEvent))],
        { Type: "result", Result: { } result } => [Line(DisplayKind.Result, ResultText(result))],
        { Type: "rate_limit_event", RateLimit: { } info } => [Line(DisplayKind.RateLimit, RateLimitText(info))],
        { Type: "assistant" } => MessageContentLines(streamEvent.RawLine, AssistantBlockLine),
        { Type: "user" } => MessageContentLines(streamEvent.RawLine, ToolResultBlockLine),
        _ => null,
    };

    private static string InitText(ClaudeStreamEvent streamEvent)
    {
        using var document = JsonDocument.Parse(streamEvent.RawLine);
        return StreamJson.OptionalString(document.RootElement, "model") is { } model
            ? $"init · {model}"
            : "init";
    }

    private static string ResultText(ClaudeResultSummary result)
    {
        var facts = string.Join(" · ",
            "$" + result.TotalCostUsd.ToString("0.00", CultureInfo.InvariantCulture),
            result.NumTurns == 1 ? "1 turn" : $"{result.NumTurns} turns",
            DurationText(result.DurationMs));
        if (!result.IsError)
            return facts;
        // Auth failures hide behind subtype "success" (see ClaudeResultSummary); a bare
        // "error" beats echoing that lie.
        var reason = result.Subtype == "success" ? "error" : result.Subtype;
        return $"{reason} · {facts}";
    }

    private static string DurationText(long durationMs) =>
        durationMs >= 60_000
            ? $"{durationMs / 60_000}m{durationMs % 60_000 / 1_000}s"
            : (durationMs / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s";

    private static string RateLimitText(RateLimitInfo info) =>
        info.RateLimitType is null
            ? $"rate limit {info.Status}"
            : $"rate limit {info.Status} · {info.RateLimitType}";

    private static List<DisplayLine> MessageContentLines(
        string rawLine, Func<JsonElement, DisplayLine?> projectBlock)
    {
        using var document = JsonDocument.Parse(rawLine);
        if (!document.RootElement.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return [];

        var lines = new List<DisplayLine>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && projectBlock(block) is { } line)
                lines.Add(line);
        }

        return lines;
    }

    private static DisplayLine? AssistantBlockLine(JsonElement block) =>
        StreamJson.OptionalString(block, "type") switch
        {
            "text" when StreamJson.OptionalString(block, "text") is { Length: > 0 } text =>
                Line(DisplayKind.AssistantText, Clip(text)),
            "tool_use" when StreamJson.OptionalString(block, "name") is { } name =>
                Line(DisplayKind.ToolUse, Clip($"{name}({ToolInputText(block)})")),
            _ => null, // thinking blocks and unrecognized shapes are not display content
        };

    private static string ToolInputText(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return "";

        var values = new List<string>();
        foreach (var property in input.EnumerateObject())
        {
            var text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                    property.Value.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrEmpty(text))
                values.Add(FirstLine(text));
        }

        return string.Join(", ", values);
    }

    private static DisplayLine? ToolResultBlockLine(JsonElement block)
    {
        if (StreamJson.OptionalString(block, "type") != "tool_result"
            || !block.TryGetProperty("content", out var content))
            return null;

        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => FirstTextBlock(content),
            _ => null,
        };
        return text is { Length: > 0 } ? Line(DisplayKind.ToolResult, Clip(text)) : null;
    }

    private static string? FirstTextBlock(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && StreamJson.OptionalString(item, "text") is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    private static string BareType(ClaudeStreamEvent streamEvent) =>
        string.IsNullOrEmpty(streamEvent.Subtype)
            ? streamEvent.Type
            : $"{streamEvent.Type}/{streamEvent.Subtype}";

    private static string Clip(string text)
    {
        var line = FirstLine(text);
        return line.Length <= MaxTextLength ? line : line[..(MaxTextLength - 1)] + "…";
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static DisplayLine Line(DisplayKind kind, string text) => new() { Kind = kind, Text = text };
}

public enum DisplayKind { Init, AssistantText, ToolUse, ToolResult, Result, RateLimit, Other }

public sealed record DisplayLine
{
    public required DisplayKind Kind { get; init; }

    /// <summary>e.g. "Read(src/Foo.cs)" · first line of assistant text · "$1.14 · 23 turns · 4m10s"</summary>
    public required string Text { get; init; }
}
