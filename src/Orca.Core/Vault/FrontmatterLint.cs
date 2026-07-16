using System.Text.RegularExpressions;

namespace Orca.Core.Vault;

/// <summary>BF-14 §1: a raw-text pre-lint of the plan frontmatter, run BEFORE the YAML
/// load, for the traps hand-editors actually hit. YAML's comment semantics silently
/// truncate <c>title: Fix #12 in parser</c> to "Fix" — the Verifier then judges the
/// truncated criterion and its byte-exact echo hides the loss — and a value starting
/// with <c>@</c>/<c>`</c>/<c>%</c> dies later with YAML's cryptic "found character that
/// cannot start any token". Both become loud, quoted-value-shaped errors here. Applied
/// to PLAN files only — the human-edited surface; records are Orca-written.</summary>
public static class FrontmatterLint
{
    // `key: value` or `- value` (including `- id: x`); captures the raw value text.
    private static readonly Regex ValueLine =
        new(@"^\s*(?:-\s+)?(?:[A-Za-z_][A-Za-z0-9_]*:\s*)?(?<val>\S.*)$", RegexOptions.Compiled);

    public static void Check(string fileText, string filePath)
    {
        var lines = fileText.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
            return; // Frontmatter.Parse produces the canonical error for that.

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimEnd() == "---")
                break;

            var match = ValueLine.Match(line);
            if (!match.Success)
                continue;

            var value = match.Groups["val"].Value;
            if (value.StartsWith('#') || value.StartsWith('"') || value.StartsWith('\''))
                continue; // a pure comment, or a quoted value — both safe.

            if (value.Contains(" #"))
                throw new VaultSchemaException(filePath,
                    $"line {i + 1}: unquoted '#' silently truncates the value at the '#' (YAML comment " +
                    $"semantics) — double-quote the whole value: {line.Trim()}");

            if (value[0] is '@' or '`' or '%')
                throw new VaultSchemaException(filePath,
                    $"line {i + 1}: YAML cannot start a plain value with '{value[0]}' — double-quote the " +
                    $"whole value: {line.Trim()}");
        }
    }
}
