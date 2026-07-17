using System.Text;
using System.Text.Json;

namespace Orca.Core.Run;

/// <summary>SUG-09: the deterministic excerpting pass over an attempt's NDJSON event
/// log — assistant prose and tool errors in, tool payloads out — so the haiku-tier
/// Summarizer reads cents' worth of signal instead of the full transcript. Plain C#,
/// no LLM, lenient per line: a malformed or unexpected event contributes nothing
/// rather than failing the excerpt (the log is evidence; this is a view).</summary>
public static class TranscriptExcerpt
{
    public const int DefaultMaxBytes = 100_000;

    public static string Extract(string eventLogPath, int maxBytes = DefaultMaxBytes)
    {
        if (!File.Exists(eventLogPath))
            return "(no transcript found)";

        var sb = new StringBuilder();
        foreach (var line in File.ReadLines(eventLogPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (sb.Length >= maxBytes)
            {
                sb.Append("(excerpt truncated — the full transcript is in the run log)\n");
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                AppendEvent(sb, document.RootElement);
            }
            catch (JsonException)
            {
                // Not this view's problem — the raw log keeps the evidence.
            }
        }

        return sb.Length == 0 ? "(transcript carried no extractable prose)" : sb.ToString();
    }

    private static void AppendEvent(StringBuilder sb, JsonElement root)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "assistant":
                foreach (var text in ContentTexts(root, "text"))
                    sb.Append("assistant: ").Append(Cap(text, 2000)).Append('\n');
                break;

            case "user":
                // Tool results ride user events; only errors carry session-note signal.
                foreach (var text in ToolErrorTexts(root))
                    sb.Append("tool_error: ").Append(Cap(text, 1000)).Append('\n');
                break;

            case "result":
                if (root.TryGetProperty("result", out var resultText) && resultText.ValueKind == JsonValueKind.String)
                    sb.Append("final result: ").Append(Cap(resultText.GetString()!, 2000)).Append('\n');
                if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                    sb.Append($"total cost: ${cost.GetDecimal()}\n");
                break;

            case "orca_executor":
                if (root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String)
                    sb.Append("executor: ").Append(subtype.GetString()).Append('\n');
                break;
        }
    }

    private static IEnumerable<string> ContentTexts(JsonElement root, string contentType)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var itemType)
                && itemType.GetString() == contentType
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { Length: > 0 } value)
                yield return value;
        }
    }

    private static IEnumerable<string> ToolErrorTexts(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "tool_result")
                continue;
            if (!item.TryGetProperty("is_error", out var isError) || isError.ValueKind != JsonValueKind.True)
                continue;

            if (item.TryGetProperty("content", out var body))
            {
                var text = body.ValueKind switch
                {
                    JsonValueKind.String => body.GetString(),
                    JsonValueKind.Array => string.Join(" ", body.EnumerateArray()
                        .Where(b => b.TryGetProperty("text", out _))
                        .Select(b => b.GetProperty("text").GetString())),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text!;
            }
        }
    }

    private static string Cap(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
