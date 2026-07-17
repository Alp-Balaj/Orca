using System.Globalization;
using System.Text;
using Terminal.Gui.Text;

namespace Orca.Cli.Tui;

/// <summary>BF-15: display-column math for the TUI's own text stack. Everything here
/// previously measured UTF-16 code units (<c>string.Length</c>) — CJK wrapped too late,
/// ZWJ emoji far too early, and code-unit slicing could split a surrogate pair into
/// replacement glyphs. Width is computed per GRAPHEME CLUSTER (a ZWJ emoji is one
/// 2-column glyph, not the sum of its runes), with each cluster's width the max of its
/// runes' wcwidth (Terminal.Gui's own tables); slicing only ever lands on cluster
/// boundaries.</summary>
internal static class TuiWidth
{
    public static int Columns(string text)
    {
        var total = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            total += ClusterColumns((string)enumerator.Current);
        return total;
    }

    /// <summary>The longest prefix that fits in <paramref name="columns"/>, cut on a
    /// grapheme-cluster boundary — never mid-surrogate, never mid-emoji.</summary>
    public static string ClipToColumns(string text, int columns)
    {
        var used = 0;
        var end = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var cluster = (string)enumerator.Current;
            var width = ClusterColumns(cluster);
            if (used + width > columns)
                break;
            used += width;
            end += cluster.Length;
        }

        return text[..end];
    }

    private static int ClusterColumns(string cluster)
    {
        var width = 0;
        foreach (var rune in cluster.EnumerateRunes())
            width = Math.Max(width, Math.Max(0, rune.GetColumns()));
        return width;
    }
}
