using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>The pure word-wrap behind the TUI's styled panes: greedy wrapping that
/// preserves each run's role and hangs continuations two columns past the logical line's
/// own indent, so wrapped list items read as one item.</summary>
public sealed class StyledWrapTests
{
    private static string[] PlainRows(IReadOnlyList<StyledLine> lines, int width)
        => StyledWrap.Wrap(lines, width).Select(l => l.PlainText()).ToArray();

    [Fact]
    public void Short_lines_pass_through_untouched()
    {
        var line = StyledLine.Of(new("label ", TuiRole.Dim), new("value", TuiRole.Text));

        var wrapped = StyledWrap.Wrap([line], 40);

        Assert.Same(line, Assert.Single(wrapped));
    }

    [Fact]
    public void Long_lines_wrap_at_word_boundaries_within_the_width()
    {
        var rows = PlainRows([StyledLine.Plain("one two three four five six")], 13);

        Assert.All(rows, r => Assert.True(r.Length <= 13, $"'{r}' exceeds width"));
        Assert.Equal("one two three", rows[0]);
        Assert.Equal("  four five", rows[1]); // continuation hangs 2 past the (zero) indent
        Assert.Equal("  six", rows[2]);
    }

    [Fact]
    public void Continuations_hang_past_the_logical_lines_own_indent()
    {
        var rows = PlainRows([StyledLine.Plain("    bullet text that wraps")], 15);

        Assert.Equal("    bullet text", rows[0]);
        Assert.Equal("      that", rows[1]); // 4 + 2 hanging columns
        Assert.Equal("      wraps", rows[2]);
    }

    [Fact]
    public void Roles_survive_the_wrap()
    {
        var line = StyledLine.Of(
            new("✗ ", TuiRole.Error),
            new("a criterion that definitely wraps around", TuiRole.Text));

        var wrapped = StyledWrap.Wrap([line], 20);

        Assert.True(wrapped.Count > 1);
        Assert.Equal(TuiRole.Error, wrapped[0].Runs[0].Role);
        var continuationWords = wrapped[1].Runs.Where(r => r.Text.Trim().Length > 0);
        Assert.All(continuationWords, r => Assert.Equal(TuiRole.Text, r.Role));
    }

    [Fact]
    public void Degenerate_widths_leave_lines_alone()
    {
        var line = StyledLine.Plain("anything at all");

        Assert.Same(line, Assert.Single(StyledWrap.Wrap([line], 3)));
    }

    // --- BF-15: display columns, not UTF-16 code units ---

    [Fact]
    public void CJK_text_wraps_by_display_columns_not_code_units()
    {
        // Ten CJK chars = 10 code units but 20 columns: at width 12 they can't share a
        // row with anything and must split across rows, where the old code-unit math
        // kept them on one row and let the terminal clip mid-line.
        var lines = StyledWrap.Wrap([StyledLine.Plain("汉字汉字汉 汉字汉字汉")], width: 12);

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ZWJ_emoji_count_their_display_width_not_their_code_units()
    {
        // The technologist emoji is 5 UTF-16 code units but 2 display columns — the old
        // math wrapped it far too early.
        var emoji = "\U0001F9D1\u200D\U0001F4BB"; // person + ZWJ + laptop
        var line = StyledLine.Plain($"{emoji} {emoji} {emoji} ok");

        // 3 emoji (2 cols each) + 3 spaces + "ok" = 11 columns — fits in width 12 as one
        // row; code-unit math would have counted 17+ and split it.
        var lines = StyledWrap.Wrap([line], width: 12);

        Assert.Single(lines);
    }

    [Fact]
    public void Column_clipping_never_splits_a_surrogate_pair()
    {
        var text = "ab\U0001F600cd"; // 😀 is a surrogate pair, 2 columns
        // 3 columns: "ab" fits (2), the emoji (2 more) would exceed — the clip must cut
        // BEFORE the pair, not through it.
        var clipped = TuiWidth.ClipToColumns(text, 3);

        Assert.Equal("ab", clipped);
        // And a clip wide enough takes the whole pair intact.
        Assert.Equal("ab\U0001F600", TuiWidth.ClipToColumns(text, 4));
    }
}
