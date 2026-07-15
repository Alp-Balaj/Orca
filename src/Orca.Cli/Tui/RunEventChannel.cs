using System.Threading.Channels;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>The <see cref="IRunObserver"/> adapter that bridges the executor/session
/// threads to the UI thread (tui-plan §3.6, §4). <see cref="OnRunEvent"/> runs on a
/// background thread and does one thing — write the observation into a channel; it never
/// touches a widget, never blocks, never throws (the observer contract). The UI thread owns
/// the reading end: a drain (an <c>Application.AddTimeout</c> tick in <see cref="RunScreen"/>)
/// pulls whatever has accumulated in one batch via <see cref="DrainInto"/> and repaints once
/// — coalescing to a single layout pass per batch, at UI cadence rather than event cadence.
///
/// Step-6 scope: the high-volume per-token stream (<see cref="RunObservation.RoleStreamEvent"/>)
/// now enters the channel alongside the lifecycle observations, so the pane can show the
/// invocation as it happens. The transport is deliberately <em>unbounded and ordered</em>:
/// it never drops and never reorders, which makes the load-bearing invariant — a flood of
/// stream events can never evict a lifecycle event — trivially true here, because nothing is
/// evicted at transport at all. The bounded, counted drop policy the plan calls for (§4:
/// "drops display events oldest-first while counting drops visibly in the pane header") lives
/// one layer down, on the UI-thread drain side, in <see cref="RunActivityBuffer"/> — the
/// owner-approved count-and-coalesce-on-drain scheme (2026-07-15). The channel never actually
/// accumulates past one drain interval: the drain empties it every tick and before every
/// gate modal.</summary>
internal sealed class RunEventChannel : IRunObserver
{
    private readonly Channel<RunObservation> _channel;

    public RunEventChannel()
    {
        _channel = Channel.CreateUnbounded<RunObservation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // Two producers write here: the session's background Task emits the lifecycle
            // observations, and the executor's stdout pump thread emits RoleStreamEvent via
            // RoleStreamForwarder. They never race for the same instant (a role's stream is
            // bracketed by its RoleStarted/RoleFinished on the session thread), but the two
            // distinct threads still make this genuinely multi-writer.
            SingleWriter = false,
        });
    }

    /// <summary>Background thread. Non-blocking, never-throwing by contract. Both lifecycle
    /// observations and the per-token stream flow through here in arrival order; nothing is
    /// dropped or reordered at transport (see class remarks) — the bounded drop lives in
    /// <see cref="RunActivityBuffer"/> on the drain side.</summary>
    public void OnRunEvent(RunObservation observation) => _channel.Writer.TryWrite(observation);

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
