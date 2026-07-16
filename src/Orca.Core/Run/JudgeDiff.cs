using System.IO.Enumeration;
using System.Text;

namespace Orca.Core.Run;

/// <summary>SUG-12 §1: the judge-facing view of the diff. The Reconciler and Verifier
/// each pay the full diff's input tokens, so generated files (lockfiles, minified
/// bundles) are replaced by explicit markers — the judges still SEE that the file
/// changed (an unexpected lockfile edit stays flaggable as extra work), they just don't
/// pay thousands of lines to see it — and any single file's section is capped. Filters
/// text, never git state; the human's own review always has the real diff.</summary>
public static class JudgeDiff
{
    /// <summary>Applied when the config carries no <c>judgeDiffExcludes</c> — the
    /// well-known generated files nobody needs a judge to read. An explicit empty list
    /// in the config disables exclusion entirely.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludes =
    [
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb",
        "Cargo.lock", "composer.lock", "Gemfile.lock", "poetry.lock", "uv.lock",
        "*.min.js", "*.min.css", "*.map", "*.snap",
    ];

    /// <summary>Per-file ceiling on any single section's text — matches the untracked
    /// per-file budget in <c>GitWorkspace.FullDiff</c>, extended here to tracked
    /// sections too (one giant generated-but-tracked file must not dominate the spend).</summary>
    public const int PerFileByteCap = 64 * 1024;

    public static string Filter(string fullDiff, IReadOnlyList<string> excludePatterns)
    {
        var sb = new StringBuilder(fullDiff.Length);
        foreach (var (header, path, body) in Sections(fullDiff))
        {
            sb.Append(header);
            if (path is not null && MatchedPattern(path, excludePatterns) is { } pattern)
            {
                var lines = body.Count(c => c == '\n');
                sb.Append($"(content excluded from the judge diff by pattern '{pattern}' — {lines} line(s) omitted; the file DID change, and the full diff is in git)\n");
                continue;
            }

            if (Encoding.UTF8.GetByteCount(body) > PerFileByteCap)
            {
                var keep = body[..PerFileByteCap];
                var lastNewline = keep.LastIndexOf('\n');
                if (lastNewline > 0)
                    keep = keep[..(lastNewline + 1)];
                sb.Append(keep);
                sb.Append($"(truncated for the judges at {PerFileByteCap} bytes — the full diff is in git)\n");
                continue;
            }

            sb.Append(body);
        }

        return sb.ToString();
    }

    private static string? MatchedPattern(string path, IReadOnlyList<string> patterns)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        foreach (var pattern in patterns)
        {
            var target = pattern.Contains('/') ? path : fileName;
            if (FileSystemName.MatchesSimpleExpression(pattern, target, ignoreCase: false))
                return pattern;
        }

        return null;
    }

    /// <summary>Splits FullDiff output into (header line, file path, body) sections: a
    /// leading preamble (path null, never filtered), tracked sections starting at
    /// <c>diff --git</c>, and untracked sections starting at
    /// <c>## New file (untracked):</c>. The header line always survives filtering —
    /// visibility of WHAT changed is never traded away.</summary>
    private static List<(string Header, string? Path, string Body)> Sections(string fullDiff)
    {
        var sections = new List<(string, string?, string)>();
        var lines = fullDiff.Split('\n');
        var header = "";
        string? path = null;
        var body = new StringBuilder();
        var open = false;

        void Close()
        {
            if (open || body.Length > 0)
                sections.Add((header, path, body.ToString()));
            body.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isLast = i == lines.Length - 1;
            var trackedStart = line.StartsWith("diff --git a/", StringComparison.Ordinal);
            var untrackedStart = line.StartsWith("## New file (untracked): ", StringComparison.Ordinal);

            if (trackedStart || untrackedStart)
            {
                Close();
                open = true;
                header = isLast ? line : line + "\n";
                path = trackedStart
                    ? line[(line.IndexOf(" b/", StringComparison.Ordinal) + 3)..]
                    : line["## New file (untracked): ".Length..];
                continue;
            }

            body.Append(line);
            if (!isLast)
                body.Append('\n');
        }

        Close();
        return sections;
    }
}
