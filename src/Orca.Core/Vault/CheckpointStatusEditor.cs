using System.Globalization;
using System.Text.RegularExpressions;
using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Requests that a checkpoint's status flip also flip the plan's own status in the
/// same edit — e.g. moving the last checkpoint to complete while marking the plan
/// complete.</summary>
public readonly record struct PlanStatusChange(PlanStatus ExpectedCurrent, PlanStatus New);

/// <summary>Flips one checkpoint's status (and the plan-level <c>updated</c> timestamp) in
/// place, editing only those lines of the plan file's frontmatter. Every other line —
/// including the entire body — is carried over byte-for-byte, so a human's prose and
/// Obsidian's own formatting choices survive an Orca-driven status change untouched.</summary>
public static class CheckpointStatusEditor
{
    /// <summary>Flips <paramref name="checkpointId"/>'s status from
    /// <paramref name="expectedCurrentStatus"/> to <paramref name="newStatus"/> and rewrites
    /// the plan's <c>updated</c> timestamp. Optionally also flips the plan's own status.
    /// Returns the path written. The file is re-validated through <see cref="PlanParser"/>
    /// before anything is written, and the write is atomic (temp file + move).</summary>
    public static string SetStatus(
        string filePath,
        string checkpointId,
        CheckpointStatus expectedCurrentStatus,
        CheckpointStatus newStatus,
        DateTimeOffset updated,
        PlanStatusChange? planStatusChange = null)
    {
        if (!File.Exists(filePath))
            throw new VaultException($"plan file not found: {filePath}");

        var originalText = File.ReadAllText(filePath);
        var plan = PlanParser.Parse(originalText, filePath);

        var checkpoint = plan.Checkpoints.FirstOrDefault(
            c => string.Equals(c.Id, checkpointId, StringComparison.Ordinal));
        if (checkpoint is null)
            throw new VaultException($"plan '{plan.PlanId}' has no checkpoint '{checkpointId}'");

        if (checkpoint.Status != expectedCurrentStatus)
            throw new VaultException(
                $"checkpoint '{checkpointId}' has status '{SchemaEnums.ToYaml(checkpoint.Status)}' " +
                $"but caller expected '{SchemaEnums.ToYaml(expectedCurrentStatus)}'");

        if (planStatusChange is { } change && plan.Status != change.ExpectedCurrent)
            throw new VaultException(
                $"plan '{plan.PlanId}' has status '{SchemaEnums.ToYaml(plan.Status)}' " +
                $"but caller expected '{SchemaEnums.ToYaml(change.ExpectedCurrent)}'");

        var lines = SplitLines(originalText);
        var frontmatterEnd = FindFrontmatterEnd(lines);

        ReplaceCheckpointStatus(lines, frontmatterEnd, checkpointId, newStatus);
        ReplaceTopLevelValue(lines, frontmatterEnd, "updated", FormatTimestamp(updated));
        if (planStatusChange is { } change2)
            ReplaceTopLevelValue(lines, frontmatterEnd, "status", SchemaEnums.ToYaml(change2.New));

        var editedText = string.Concat(lines.Select(l => l.Content + l.Terminator));

        // Fail loud before touching disk if the surgical edit ever produced something the
        // real parser refuses.
        PlanParser.Parse(editedText, filePath);

        WriteAtomic(filePath, editedText);
        return filePath;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    private static void ReplaceTopLevelValue(
        List<(string Content, string Terminator)> lines, int frontmatterEnd, string key, string newValue)
    {
        var prefix = key + ":";
        for (var i = 1; i < frontmatterEnd; i++)
        {
            if (lines[i].Content.StartsWith(prefix, StringComparison.Ordinal))
            {
                lines[i] = (prefix + " " + newValue, lines[i].Terminator);
                return;
            }
        }

        throw new InvalidOperationException(
            $"expected a top-level '{key}:' line after PlanParser validated the frontmatter");
    }

    private static readonly Regex CheckpointIdLine =
        new(@"^(?<indent>[ ]*)-\s+id:\s*(?<id>\S+)\s*$", RegexOptions.Compiled);

    private static void ReplaceCheckpointStatus(
        List<(string Content, string Terminator)> lines, int frontmatterEnd, string checkpointId, CheckpointStatus newStatus)
    {
        var itemStart = -1;
        var itemIndent = 0;
        for (var i = 1; i < frontmatterEnd; i++)
        {
            var match = CheckpointIdLine.Match(lines[i].Content);
            if (match.Success && string.Equals(match.Groups["id"].Value, checkpointId, StringComparison.Ordinal))
            {
                itemStart = i;
                itemIndent = match.Groups["indent"].Value.Length;
                break;
            }
        }

        if (itemStart < 0)
            throw new InvalidOperationException(
                $"expected checkpoint '{checkpointId}' after PlanParser validated it exists");

        var itemEnd = frontmatterEnd;
        for (var i = itemStart + 1; i < frontmatterEnd; i++)
        {
            var content = lines[i].Content;
            if (content.Length > 0 && LeadingSpaces(content) <= itemIndent)
            {
                itemEnd = i;
                break;
            }
        }

        for (var i = itemStart + 1; i < itemEnd; i++)
        {
            var content = lines[i].Content;
            var indent = LeadingSpaces(content);
            if (content[indent..].StartsWith("status:", StringComparison.Ordinal))
            {
                lines[i] = (content[..indent] + "status: " + SchemaEnums.ToYaml(newStatus), lines[i].Terminator);
                return;
            }
        }

        throw new InvalidOperationException(
            $"expected a 'status:' line inside checkpoint '{checkpointId}' after PlanParser validated it");
    }

    private static int LeadingSpaces(string s)
    {
        var i = 0;
        while (i < s.Length && s[i] == ' ')
            i++;
        return i;
    }

    private static int FindFrontmatterEnd(List<(string Content, string Terminator)> lines)
    {
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Content == "---")
                return i;
        }

        throw new InvalidOperationException(
            "expected a closing '---' delimiter after PlanParser validated the frontmatter");
    }

    /// <summary>Splits text into lines, keeping each line's original terminator (<c>"\r\n"</c>,
    /// <c>"\n"</c>, or <c>""</c> for a final unterminated line) so the file can be
    /// reconstructed byte-for-byte except where a line is deliberately replaced.</summary>
    private static List<(string Content, string Terminator)> SplitLines(string text)
    {
        var lines = new List<(string, string)>();
        var pos = 0;
        while (pos < text.Length)
        {
            var start = pos;
            var newline = text.IndexOf('\n', pos);
            if (newline < 0)
            {
                lines.Add((text[start..], ""));
                pos = text.Length;
            }
            else
            {
                var hasCr = newline > start && text[newline - 1] == '\r';
                var contentEnd = hasCr ? newline - 1 : newline;
                lines.Add((text[start..contentEnd], hasCr ? "\r\n" : "\n"));
                pos = newline + 1;
            }
        }

        return lines;
    }

    private static void WriteAtomic(string path, string text)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, text);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException($"failed to write plan file: {path}", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
