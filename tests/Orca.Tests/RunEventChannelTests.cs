using Orca.Cli.Tui;
using Orca.Core.Config;
using Orca.Core.Execution;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The IRunObserver → channel adapter (tui-plan §3.6). Headless: no Terminal.Gui
/// shell, just the write/drain contract. Confirms it batches, empties on drain, keeps the
/// final events for the post-completion flush, and — the step-6 change — carries the
/// per-token stream through in arrival order alongside the lifecycle events (the transport
/// no longer drops or filters; the bounded drop policy lives in
/// <see cref="RunActivityBuffer"/>).</summary>
public sealed class RunEventChannelTests
{
    private static RunObservation Lifecycle(string branch) =>
        new RunObservation.BranchReady(branch, Created: true);

    private static RunObservation Stream() =>
        new RunObservation.RoleStreamEvent(
            RoleName.Implementer, new ClaudeStreamEvent { Type = "assistant", RawLine = "{}" });

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
    public void Stream_events_flow_through_interleaved_with_lifecycle_in_arrival_order()
    {
        var channel = new RunEventChannel();

        // The transport is now the high-volume sink (step 6): stream events enter alongside
        // lifecycle events and nothing is dropped or reordered at this layer.
        channel.OnRunEvent(Stream());
        channel.OnRunEvent(Lifecycle("a"));
        channel.OnRunEvent(Stream());

        var batch = new List<RunObservation>();
        Assert.Equal(3, channel.DrainInto(batch));
        Assert.IsType<RunObservation.RoleStreamEvent>(batch[0]);
        Assert.IsType<RunObservation.BranchReady>(batch[1]);
        Assert.IsType<RunObservation.RoleStreamEvent>(batch[2]);
    }

    [Fact]
    public void Events_written_before_complete_are_still_drainable_after_it()
    {
        // The final drain after the session ends must not lose the last events.
        var channel = new RunEventChannel();
        channel.OnRunEvent(Lifecycle("last"));
        channel.Complete();

        var batch = new List<RunObservation>();
        Assert.Equal(1, channel.DrainInto(batch));
        Assert.Equal("last", Assert.IsType<RunObservation.BranchReady>(batch[0]).Branch);
    }
}
