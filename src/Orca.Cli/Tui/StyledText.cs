using System.Text;

namespace Orca.Cli.Tui;

/// <summary>Semantic text roles for the TUI panes. Views translate a role to a colour via
/// <c>TuiTheme.For</c>; the projections in <see cref="TuiText"/> only ever speak roles, so
/// they stay pure and unit-testable with no Terminal.Gui types.</summary>
internal enum TuiRole
{
    Text,
    Dim,
    Faint,
    Accent,
    Success,
    Error,
    Warning,
    Heading,
}

/// <summary>One run of same-role text within a line.</summary>
internal readonly record struct StyledRun(string Text, TuiRole Role);

/// <summary>One logical line of styled text — a sequence of runs. Pure data.</summary>
internal sealed record StyledLine(IReadOnlyList<StyledRun> Runs)
{
    public static StyledLine Empty { get; } = new([]);

    public static StyledLine Of(params StyledRun[] runs) => new(runs);

    /// <summary>A whole line in a single role.</summary>
    public static StyledLine Plain(string text, TuiRole role = TuiRole.Text) =>
        new([new StyledRun(text, role)]);

    public string PlainText()
    {
        var sb = new StringBuilder();
        foreach (var run in Runs)
            sb.Append(run.Text);
        return sb.ToString();
    }
}

/// <summary>Greedy word-wrap over styled lines, preserving each run's role across the
/// split. Continuation rows are indented two columns past the logical line's own leading
/// whitespace, so wrapped list items read as one item (the Claude Code transcript idiom).
/// Pure string work — unit-tested without any UI.</summary>
internal static class StyledWrap
{
    public static IReadOnlyList<StyledLine> Wrap(IReadOnlyList<StyledLine> lines, int width)
    {
        if (width < 4)
            return lines;

        var result = new List<StyledLine>(lines.Count);
        foreach (var line in lines)
        {
            if (TuiWidth.Columns(line.PlainText()) <= width)
            {
                result.Add(line);
                continue;
            }

            WrapOne(line, width, result);
        }

        return result;
    }

    private static void WrapOne(StyledLine line, int width, List<StyledLine> result)
    {
        var lead = LeadingWhitespace(line);
        var indent = ContinuationIndent(line, width);

        var current = new List<StyledRun>();
        var currentWidth = 0;
        var first = true;

        void StartRow()
        {
            // The logical line's own leading whitespace on its first row, the hanging
            // indent on continuations — both in the default role.
            var pad = first ? lead : indent;
            if (pad.Length > 0)
            {
                current.Add(new StyledRun(pad, TuiRole.Text));
                currentWidth = TuiWidth.Columns(pad);
            }
        }

        foreach (var (word, role) in Words(line))
        {
            var wordWidth = TuiWidth.Columns(word);
            if (current.Count == 0)
                StartRow();
            else if (currentWidth + 1 + wordWidth > width)
            {
                result.Add(new StyledLine(current));
                current = [];
                currentWidth = 0;
                first = false;
                StartRow();
            }
            else
            {
                current.Add(new StyledRun(" ", role));
                currentWidth += 1;
            }

            current.Add(new StyledRun(word, role));
            currentWidth += wordWidth;
        }

        if (current.Count > 0)
            result.Add(new StyledLine(current));
    }

    private static string ContinuationIndent(StyledLine line, int width)
    {
        var indent = LeadingWhitespace(line).Length + 2;
        // A pathological indent must never eat the whole row.
        return new string(' ', Math.Min(indent, width / 2));
    }

    private static string LeadingWhitespace(StyledLine line)
    {
        var text = line.PlainText();
        var i = 0;
        while (i < text.Length && text[i] == ' ')
            i++;
        return new string(' ', i);
    }

    /// <summary>The line's words with their roles, splitting runs on spaces. A word that
    /// spans a run boundary keeps the role of its first run — a wrap-only simplification
    /// that never bleeds a role across a space.</summary>
    private static IEnumerable<(string Word, TuiRole Role)> Words(StyledLine line)
    {
        var word = new StringBuilder();
        var role = TuiRole.Text;

        foreach (var run in line.Runs)
        {
            foreach (var ch in run.Text)
            {
                if (ch == ' ')
                {
                    if (word.Length > 0)
                    {
                        yield return (word.ToString(), role);
                        word.Clear();
                    }
                }
                else
                {
                    if (word.Length == 0)
                        role = run.Role;
                    word.Append(ch);
                }
            }
        }

        if (word.Length > 0)
            yield return (word.ToString(), role);
    }
}
