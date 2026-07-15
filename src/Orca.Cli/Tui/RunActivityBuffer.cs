namespace Orca.Cli.Tui;

/// <summary>The bounded, ordered display model behind the run view's activity pane — where
/// the owner-approved "count-and-coalesce-on-drain" drop policy lives (tui-plan §4;
/// 2026-07-15 review). The <see cref="RunEventChannel"/> transport never drops, so once the
/// per-token stream became the high-volume sink (step 6) the memory/repaint bound had to
/// land somewhere: here. Lines arrive already projected (via <see cref="RunActivityLine"/>)
/// and are appended in arrival order, keeping lifecycle and stream lines interleaved exactly
/// as they happened.
///
/// The one load-bearing invariant: LIFECYCLE lines are never evicted. Only display STREAM
/// lines are bounded — when they exceed <see cref="MaxStreamLines"/> the oldest stream line
/// is dropped and <see cref="Elided"/> counts it, so a flood of stream events can never push
/// a gate, a role's start/finish, or a vault record out of the pane. The run log on disk
/// keeps every raw line regardless, which is what lets the pane honestly say "N events
/// elided — the log has everything." Pure (no Terminal.Gui types), so the invariant is
/// unit-tested headless.</summary>
internal sealed class RunActivityBuffer
{
    /// <summary>How many stream lines the pane retains for scrollback. Generous — far more
    /// than a screen — but bounded so a long implementer run neither grows without limit nor
    /// makes the per-tick re-wrap (which is O(retained lines)) sluggish under a flood. The
    /// on-disk run log, not this window, is the complete record.</summary>
    private const int MaxStreamLines = 2000;

    private readonly LinkedList<Entry> _entries = new();
    private int _streamLines;

    private readonly record struct Entry(StyledLine Line, bool IsStream);

    /// <summary>Total display events dropped oldest-first under load. Rendered in the pane
    /// header; the run log on disk remains complete.</summary>
    public int Elided { get; private set; }

    public int Count => _entries.Count;

    /// <summary>The pane's content, flattened in arrival order. Materialized per call — the
    /// drain calls this once per coalesced batch, not per event.</summary>
    public IReadOnlyList<StyledLine> Lines
    {
        get
        {
            var lines = new List<StyledLine>(_entries.Count);
            foreach (var entry in _entries)
                lines.Add(entry.Line);
            return lines;
        }
    }

    /// <summary>Append the per-token stream's projected lines. These are the only droppable
    /// content: appending past the cap evicts the oldest stream line and counts it.</summary>
    public void AppendStream(IReadOnlyList<StyledLine> lines)
    {
        foreach (var line in lines)
        {
            _entries.AddLast(new Entry(line, IsStream: true));
            _streamLines++;
        }

        TrimStream();
    }

    /// <summary>Append lifecycle content — role starts/finishes, gates, records, and the
    /// screen's own framing (the launching placeholder, the outcome summary). Never evicted,
    /// never counted against the cap.</summary>
    public void AppendLifecycle(IReadOnlyList<StyledLine> lines)
    {
        foreach (var line in lines)
            _entries.AddLast(new Entry(line, IsStream: false));
    }

    private void TrimStream()
    {
        while (_streamLines > MaxStreamLines)
        {
            // Walk from the oldest end for the first STREAM line — lifecycle lines are
            // skipped, so they survive any flood. Leading lifecycle lines are few (a role
            // start, a branch), so this is effectively O(1) in the steady state.
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (!node.Value.IsStream)
                    continue;
                _entries.Remove(node);
                _streamLines--;
                Elided++;
                break;
            }
        }
    }
}
