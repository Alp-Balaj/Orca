using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Orca.Cli.Tui;

/// <summary>Read-only view over <see cref="StyledLine"/>s: per-run colours from
/// <see cref="TuiTheme"/>, greedy word-wrap to the current width, and arrow/page scrolling.
/// This replaces the stock (and [Obsolete]) <c>TextView</c>, whose read-only role in the
/// default v2 scheme rendered grey-on-grey — invisible text. Drawing is the only
/// Terminal.Gui-specific part; content and wrapping stay pure (<see cref="StyledWrap"/>).</summary>
internal sealed class StyledView : View
{
    private IReadOnlyList<StyledLine> _lines = [];
    private IReadOnlyList<StyledLine> _wrapped = [];
    private int _wrapWidth = -1;
    private int _scroll;

    public StyledView()
    {
        CanFocus = true;
        SetScheme(TuiTheme.BaseScheme);
        KeyDown += OnScrollKey;
    }

    public void SetLines(IReadOnlyList<StyledLine> lines)
    {
        _lines = lines;
        _wrapWidth = -1;
        _scroll = 0;
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
        key.Handled = true;
        SetNeedsDraw();
    }
}
