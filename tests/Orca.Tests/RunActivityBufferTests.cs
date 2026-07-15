using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>The bounded display model behind the live activity pane (tui-plan §4, the
/// owner-approved count-and-coalesce-on-drain drop policy). Headless — StyledLine is pure —
/// so the load-bearing invariant is tested directly: a flood of stream lines never evicts a
/// lifecycle line, drops are counted, and arrival order is preserved.</summary>
public sealed class RunActivityBufferTests
{
    private static StyledLine Stream(string text) => StyledLine.Plain(text, TuiRole.Dim);
    private static StyledLine Lifecycle(string text) => StyledLine.Plain(text, TuiRole.Text);

    private static IReadOnlyList<string> Texts(RunActivityBuffer buffer) =>
        buffer.Lines.Select(l => l.PlainText()).ToList();

    [Fact]
    public void Arrival_order_is_preserved_across_stream_and_lifecycle()
    {
        var buffer = new RunActivityBuffer();
        buffer.AppendStream([Stream("a")]);
        buffer.AppendLifecycle([Lifecycle("L")]);
        buffer.AppendStream([Stream("b")]);

        Assert.Equal(["a", "L", "b"], Texts(buffer));
        Assert.Equal(0, buffer.Elided);
    }

    [Fact]
    public void Under_the_cap_nothing_is_dropped()
    {
        var buffer = new RunActivityBuffer();
        for (var i = 0; i < 50; i++)
            buffer.AppendStream([Stream($"s{i}")]);

        Assert.Equal(0, buffer.Elided);
        Assert.Equal(50, buffer.Count);
    }

    [Fact]
    public void A_stream_flood_never_evicts_a_lifecycle_line_and_counts_the_drops()
    {
        const int flood = 10_000;
        var buffer = new RunActivityBuffer();

        // A lifecycle event surrounded on both sides by a flood — the exact interleaving the
        // step-6 pane sees when a role's stream buries a gate/branch/record line.
        for (var i = 0; i < flood / 2; i++)
            buffer.AppendStream([Stream($"pre{i}")]);
        buffer.AppendLifecycle([Lifecycle("GATE build pass")]);
        for (var i = 0; i < flood / 2; i++)
            buffer.AppendStream([Stream($"post{i}")]);

        var texts = Texts(buffer);

        // The invariant: the lifecycle line survives the flood.
        Assert.Contains("GATE build pass", texts);

        // Only stream lines were dropped, oldest-first, and every drop is counted — so the
        // pane header can say "N events elided" honestly while the run log keeps everything.
        var streamRetained = buffer.Count - 1;   // minus the one surviving lifecycle line
        Assert.Equal(flood - streamRetained, buffer.Elided);
        Assert.True(buffer.Elided > 0, "a 10k flood must overflow the retention cap");
        Assert.True(buffer.Count < flood, "the pane must stay bounded under load");

        // Oldest-first: the earliest stream lines are the ones gone; recent ones remain.
        Assert.DoesNotContain("pre0", texts);
        Assert.Contains($"post{flood / 2 - 1}", texts);
    }

    [Fact]
    public void Lifecycle_lines_do_not_count_against_the_stream_cap()
    {
        var buffer = new RunActivityBuffer();

        // Thousands of lifecycle lines never trigger a drop — only the stream is bounded.
        for (var i = 0; i < 5000; i++)
            buffer.AppendLifecycle([Lifecycle($"L{i}")]);

        Assert.Equal(0, buffer.Elided);
        Assert.Equal(5000, buffer.Count);
    }
}
