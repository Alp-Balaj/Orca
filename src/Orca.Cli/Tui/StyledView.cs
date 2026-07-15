using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Orca.Cli.Tui;

/// <summary>Read-only view over <see cref="StyledLine"/>s: per-run colours from
/// <see cref="TuiTheme"/>, greedy word-wrap to the current width, and arrow/page scrolling.
/// This replaces the stock (and [Obsolete]) <c>TextView</c>, whose read-only role in the
/// default v2 scheme rendered grey-on-grey — invisible text. Drawing is the only
/// Terminal.Gui-specific part; content and wrapping stay pure (<see cref="StyledWrap"/>).
///
/// For the live run pane (step 6) it also owns tail-follow: while <see cref="IsFollowing"/>
/// each content update sticks to the bottom, but scrolling up pauses the follow so the
/// reader can review scrollback without being yanked back down every drain tick; reaching
/// the bottom again (or <c>End</c>) resumes it. This is built entirely on the view's own
/// <c>_scroll</c>/<c>Viewport</c> model — no Terminal.Gui scroll widget, so there is no
/// external scroll API to drift.</summary>
internal sealed class StyledView : View
{
    private IReadOnlyList<StyledLine> _lines = [];
    private IReadOnlyList<StyledLine> _wrapped = [];
    private int _wrapWidth = -1;
    private int _scroll;
    private bool _follow = true;

    public StyledView()
    {
        CanFocus = true;
        SetScheme(TuiTheme.BaseScheme);
        KeyDown += OnScrollKey;
    }

    /// <summary>True while the view sticks to the tail; false once the reader has scrolled up
    /// into scrollback. "At the bottom" (everything visible, or scrolled to the last page)
    /// always reads as following.</summary>
    public bool IsFollowing => _follow;

    /// <summary>Raised when <see cref="IsFollowing"/> flips as a result of a scroll key, so a
    /// host (the run screen's pane header) can repaint its follow indicator. Not raised by
    /// <see cref="SetLines"/> — the host repaints its own chrome on each drain anyway.</summary>
    public event Action? FollowChanged;

    public void SetLines(IReadOnlyList<StyledLine> lines) => SetLines(lines, scrollToEnd: false);

    /// <summary>Replace the content. <paramref name="scrollToEnd"/> marks a tail view (the
    /// live log): while following it snaps to the newest line, but if the reader has paused
    /// the follow by scrolling up, the parked position is preserved as content grows.
    /// Non-tail callers (detail, modals) pass false and start at the top. int.MaxValue is
    /// clamped to the last page by <see cref="OnDrawingContent"/>.</summary>
    public void SetLines(IReadOnlyList<StyledLine> lines, bool scrollToEnd)
    {
        _lines = lines;
        _wrapWidth = -1;
        if (!scrollToEnd)
        {
            // A fresh non-tail view: top, and follow re-armed for reuse.
            _scroll = 0;
            _follow = true;
        }
        else if (_follow)
        {
            _scroll = int.MaxValue;   // stick to the tail
        }
        // else: paused in scrollback — leave _scroll where the reader parked it. The clamp
        // in OnDrawingContent keeps it valid as _wrapped grows.

        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
            return true;

        if (width != _wrapWidth)
        {
            _wrapped = StyledWrap.Wrap(_lines, width);
            _wrapWidth = width;
        }

        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _wrapped.Count - height));

        for (var row = 0; row < height; row++)
        {
            var index = _scroll + row;
            if (index >= _wrapped.Count)
                break;

            Move(0, row);
            var remaining = width;
            foreach (var run in _wrapped[index].Runs)
            {
                if (remaining <= 0)
                    break;
                var text = run.Text.Length > remaining ? run.Text[..remaining] : run.Text;
                SetAttribute(TuiTheme.For(run.Role));
                AddStr(text);
                remaining -= text.Length;
            }
        }

        SetAttribute(TuiTheme.Normal);
        return true;
    }

    private void OnScrollKey(object? sender, Key key)
    {
        var page = Math.Max(1, Viewport.Height - 1);
        var maxScroll = Math.Max(0, _wrapped.Count - Viewport.Height);

        int? target = null;
        if (key == Key.CursorUp)
            target = _scroll - 1;
        else if (key == Key.CursorDown)
            target = _scroll + 1;
        else if (key == Key.PageUp)
            target = _scroll - page;
        else if (key == Key.PageDown)
            target = _scroll + page;
        else if (key == Key.Home)
            target = 0;
        else if (key == Key.End)
            target = maxScroll;

        if (target is not { } t)
            return;

        _scroll = Math.Clamp(t, 0, maxScroll);

        // Follow == "am I at the bottom?" after this keypress: scrolling up into scrollback
        // pauses the tail, returning to the last page (or End) resumes it. When everything
        // fits (maxScroll == 0) we are always at the bottom, so follow stays on.
        var following = _scroll >= maxScroll;
        if (following != _follow)
        {
            _follow = following;
            FollowChanged?.Invoke();
        }

        key.Handled = true;
        SetNeedsDraw();
    }
}
