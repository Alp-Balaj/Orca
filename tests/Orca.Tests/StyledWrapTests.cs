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
}
