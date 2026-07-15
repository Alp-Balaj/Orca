using Orca.Cli.Tui;
using Orca.Core.Config;
using Orca.Core.Execution;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The IRunObserver → bounded-channel adapter (tui-plan §3.6). Headless: no
/// Terminal.Gui shell, just the write/drain contract. Confirms it batches, empties on
/// drain, keeps the final events for the post-completion flush, and — the step-5 boundary
/// — drops RoleStreamEvent at write time so the channel stays lifecycle-only.</summary>
public sealed class RunEventChannelTests
{
    private static RunObservation Lifecycle(string branch) =>
        new RunObservation.BranchReady(branch, Created: true);

    [Fact]
    public void Drain_returns_written_lifecycle_events_in_order_then_empties()
    {
        var channel = new RunEventChannel();
        channel.OnRunEvent(Lifecycle("a"));
        channel.OnRunEvent(Lifecycle("b"));

        var batch = new List<RunObservation>();
        Assert.Equal(2, channel.DrainInto(batch));
        Assert.Equal(["a", "b"], batch.OfType<RunObservation.BranchReady>().Select(e => e.Branch));

        // A second drain with nothing new is empty — coalescing means the UI only repaints
        // when there is something to show.
        Assert.Equal(0, channel.DrainInto([]));
    }

    [Fact]
    public void Role_stream_events_are_dropped_at_write_time_this_step()
    {
        var channel = new RunEventChannel();
        var raw = new ClaudeStreamEvent { Type = "assistant", RawLine = "{}" };

        channel.OnRunEvent(new RunObservation.RoleStreamEvent(RoleName.Implementer, raw));
        channel.OnRunEvent(Lifecycle("a"));
        channel.OnRunEvent(new RunObservation.RoleStreamEvent(RoleName.Implementer, raw));

        var batch = new List<RunObservation>();
        Assert.Equal(1, channel.DrainInto(batch));
        Assert.IsType<RunObservation.BranchReady>(Assert.Single(batch));
    }

    [Fact]
    public void Events_written_before_complete_are_still_drainable_after_it()
    {
        // The final drain after the session ends must not lose the last lifecycle events.
        var channel = new RunEventChannel();
        channel.OnRunEvent(Lifecycle("last"));
        channel.Complete();

        var batch = new List<RunObservation>();
        Assert.Equal(1, channel.DrainInto(batch));
        Assert.Equal("last", Assert.IsType<RunObservation.BranchReady>(batch[0]).Branch);
    }
}
