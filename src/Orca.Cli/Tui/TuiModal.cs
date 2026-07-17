using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// The static Application.Run/RequestStop are the same [Obsolete] legacy-static surface the
// step-4 shell already uses under the standing rationale (§1, decision 1). Same suppression.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>A keyboard-driven modal window, built only from the primitives the step-4 shell
/// already proves (<c>Window</c> + <see cref="StyledView"/> + <c>KeyDown</c> +
/// <c>Application.Run</c>/<c>RequestStop</c>) rather than Terminal.Gui v2's churny
/// <c>Dialog</c>/<c>Button</c> surface. It renders a scrollable styled body (the preview or
/// a gate's decision context) with an accent border and a faint footer of key hints, then
/// blocks on a nested run-loop until the human presses one of the bound keys and returns
/// that key's result.
///
/// Modal by construction: <c>Application.Run</c> pushes it as the running top, so the run
/// screen (and the shell beneath it) receive no input until it closes — the modal-session
/// invariant, made literal.</summary>
internal static class TuiModal
{
    /// <summary>One bound key: its display hint (e.g. "enter run") and the value returned
    /// when pressed. An empty <paramref name="Hint"/> registers a silent accelerator — the
    /// key still works but is left out of the footer, so a second key can share a choice's
    /// result (e.g. <c>c</c> alongside the <c>enter</c> default) without doubling the hint.</summary>
    internal readonly record struct Choice<T>(Key Key, string Hint, T Result);

    /// <summary>Show the modal and block until a bound key is pressed. If the window is
    /// stopped without a bound key (it never should be — there is always a back-out key),
    /// the LAST choice's result is returned, so callers order the safe back-out last.</summary>
    public static T Ask<T>(string title, IReadOnlyList<StyledLine> body, params Choice<T>[] choices)
    {
        var result = choices[^1].Result;

        var window = BuildWindow(title, body, choices);
        window.KeyDown += (_, key) =>
        {
            foreach (var choice in choices)
            {
                // BF-17: Caps-Lock or a habitual shifted letter must still match — a
                // gate modal that ignores 'Y' looks dead.
                if (key == choice.Key || key == choice.Key.WithShift)
                {
                    result = choice.Result;
                    key.Handled = true;
                    Application.RequestStop();
                    return;
                }
            }
        };

        Application.Run(window);
        window.Dispose();
        return result;
    }

    private static Window BuildWindow<T>(string title, IReadOnlyList<StyledLine> body, Choice<T>[] choices)
    {
        var window = new Window
        {
            Title = $" {title} ",
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(88),
            Height = Dim.Percent(86),
            BorderStyle = LineStyle.Rounded,
        };
        window.SetScheme(TuiTheme.BaseScheme);
        window.Padding!.Thickness = new Thickness(1, 0, 1, 0);
        // The modal is the active thing: coral border, matching the focused-pane cue.
        window.Border!.GetOrCreateView().SetScheme(TuiTheme.BorderFocused);

        var content = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        content.SetLines(body);

        var hints = new StyledView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false,
        };
        hints.SetLines([HintLine(choices)]);

        window.Add(content, hints);
        content.SetFocus();
        return window;
    }

    /// <summary>The footer: each choice's hint, faint, joined with the transcript's
    /// middle dot — plus the scroll hint, since the body may be taller than the modal.</summary>
    private static StyledLine HintLine<T>(Choice<T>[] choices)
    {
        var runs = new List<StyledRun>();
        foreach (var choice in choices)
        {
            if (string.IsNullOrEmpty(choice.Hint))
                continue;   // silent accelerator — bound, but not shown in the footer
            if (runs.Count > 0)
                runs.Add(new StyledRun(" · ", TuiRole.Faint));
            runs.Add(new StyledRun(choice.Hint, TuiRole.Faint));
        }

        runs.Add(new StyledRun("   ↑↓/PgUp/PgDn scroll", TuiRole.Faint));
        return new StyledLine(runs);
    }
}
