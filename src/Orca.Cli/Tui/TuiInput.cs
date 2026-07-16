using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// The static Application.Run/RequestStop are the same [Obsolete] legacy-static surface the
// rest of the TUI lane uses under the standing rationale (§1, decision 1). Same suppression.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>A one-line text-input modal, built from the same primitives as
/// <see cref="TuiModal"/> (Window + nested run-loop) plus the non-obsolete
/// <c>TextField</c> <see cref="PlanForm"/> already proves. Enter returns the trimmed
/// text (null when empty), Esc returns null — so "skip" costs a single keypress and the
/// caller can't tell an escape from a deliberate blank, which is exactly right for an
/// optional note.</summary>
internal static class TuiInput
{
    public static string? AskLine(string title, IReadOnlyList<StyledLine> body)
    {
        string? result = null;

        var window = new Window
        {
            Title = $" {title} ",
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(88),
            Height = body.Count + 6,
            BorderStyle = LineStyle.Rounded,
        };
        window.SetScheme(TuiTheme.BaseScheme);
        window.Padding!.Thickness = new Thickness(1, 0, 1, 0);
        window.Border!.GetOrCreateView().SetScheme(TuiTheme.BorderFocused);

        var content = new StyledView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = body.Count, CanFocus = false,
        };
        content.SetLines(body);

        var field = new TextField { X = 0, Y = body.Count + 1, Width = Dim.Fill(), Height = 1, Text = "" };
        field.SetScheme(TuiTheme.Input);

        var hints = new StyledView
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false,
        };
        hints.SetLines([StyledLine.Plain("enter save · esc skip", TuiRole.Faint)]);

        window.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                var text = field.Text?.Trim();
                result = string.IsNullOrEmpty(text) ? null : text;
                key.Handled = true;
                Application.RequestStop();
            }
            else if (key == Key.Esc)
            {
                result = null;
                key.Handled = true;
                Application.RequestStop();
            }
        };

        window.Add(content, field, hints);
        field.SetFocus();
        Application.Run(window);
        window.Dispose();
        return result;
    }
}
