using System.Threading.Channels;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>The <see cref="IRunObserver"/> adapter that bridges the executor/session
/// threads to the UI thread (tui-plan §3.6, §4). <see cref="OnRunEvent"/> runs on a
/// background thread and does one thing — write the observation into a bounded channel;
/// it never touches a widget, never blocks, never throws (the observer contract). The UI
/// thread owns the reading end: a drain (an <c>Application.AddTimeout</c> tick in
/// <see cref="RunScreen"/>) pulls whatever has accumulated in one batch via
/// <see cref="DrainInto"/> and repaints once — coalescing to a single layout pass per
/// batch, at UI cadence rather than event cadence.
///
/// Step-5 scope: only LIFECYCLE observations enter the channel.
/// <see cref="RunObservation.RoleStreamEvent"/> — the high-volume per-token stream — is
/// dropped here at write time, so the channel stays low-volume and no lifecycle event can
/// be evicted behind a flood of stream events. That keeps the threading model honest to
/// prove first (the step's point). Build-order step 6 removes this filter and routes the
/// stream through <c>StreamEventDisplay</c>; that is also where backpressure/drop-counting
/// earns its place. Until then the bounded capacity is a pure safety backstop.</summary>
internal sealed class RunEventChannel : IRunObserver
{
    private readonly Channel<RunObservation> _channel;

    public RunEventChannel(int capacity = 1024)
    {
        _channel = Channel.CreateBounded<RunObservation>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            // Lifecycle events arrive from the background session Task; RoleStreamEvent
            // (dropped below) would arrive from the executor pump thread — filtering it out
            // leaves a single writer, but false costs nothing and survives future changes.
            SingleWriter = false,
            // A backstop only (step 5 can't overflow a 1024-deep channel with lifecycle
            // events); oldest-first keeps the newest facts if it ever somehow did.
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    /// <summary>Background thread. Non-blocking, never-throwing by contract.</summary>
    public void OnRunEvent(RunObservation observation)
    {
        // Step 5: the live per-event stream is out of scope — keep it out of the channel
        // entirely (see class remarks), rather than draining then discarding it downstream.
        if (observation is RunObservation.RoleStreamEvent)
            return;

        _channel.Writer.TryWrite(observation);
    }

    /// <summary>The session is finished; no more events will be written. Lets a final drain
    /// know the stream is closed.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>UI thread. Move everything currently available into <paramref name="sink"/>
    /// and return how many — one coalesced batch. Never blocks: it reads only what is
    /// already buffered.</summary>
    public int DrainInto(ICollection<RunObservation> sink)
    {
        var count = 0;
        while (_channel.Reader.TryRead(out var observation))
        {
            sink.Add(observation);
            count++;
        }

        return count;
    }
}
