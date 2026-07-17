using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Two [Obsolete] suppressions, both under decision 1's standing rationale (§1):
//  - the legacy static Application.Run/RequestStop, as the rest of the TUI lane uses;
//  - TextView, the multi-line editor, which 2.4.17 marks [Obsolete] (superseded by
//    tui-cs/Editor's EditorView) but still ships and works. The owner chose the multi-line
//    goal box (2026-07-15 step-7 review) over a single-line field, accepting this suppression;
//    the roomier box fits a real multi-paragraph brief. Everything else on the form is the
//    non-obsolete TextField.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>The plan-generate form (build-order step 7) — the TUI's first typed-input ground.
/// A keyboard-driven modal (built like <see cref="TuiModal"/>: a <c>Window</c> + a nested
/// <c>Application.Run</c>) that captures the same inputs <c>plan-generate</c> takes: plan id,
/// the goal brief (a multi-line box) OR a goal-file path (exactly one), an optional title, the
/// base branch (default main), and the single-vs-multi strategy — where multi refuses exactly
/// as the CLI does. It owns its own validation loop via the pure
/// <see cref="PlanFormValidation"/>: on submit it validates, and on any error (including the
/// multi refusal) shows the message inline and stays open so the human fixes it without losing
/// input; a valid submit returns the <see cref="PlanFormResolved"/> inputs, cancel returns
/// null and spends nothing.
///
/// Focus moves between fields with Tab/Shift-Tab, driven explicitly (2.4.17 dropped TextView's
/// AllowsTab, so relying on the widget to release Tab is unsafe). Enter submits from the
/// single-line fields but inserts a newline inside the goal box; Ctrl-Enter submits from
/// anywhere; Esc cancels. The action creates a NEW plan and is not tied to the tree selection,
/// though a selected plan's id pre-fills the plan-id field as an overwritable default (handy
/// for resuming a failed planning run — <c>Prepare</c> still enforces every "already
/// exists/succeeded" refusal, surfaced in an error modal).</summary>
internal static class PlanForm
{
    /// <summary>Show the form and block until the human submits valid inputs or cancels.
    /// Returns the resolved inputs, or null on cancel.</summary>
    public static PlanFormResolved? Show(string? prefillPlanId)
    {
        PlanFormResolved? result = null;

        var window = new Window
        {
            Title = " generate plan ",
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(88),
            Height = Dim.Percent(86),
            BorderStyle = LineStyle.Rounded,
        };
        window.SetScheme(TuiTheme.BaseScheme);
        window.Padding!.Thickness = new Thickness(1, 0, 1, 0);
        window.Border!.GetOrCreateView().SetScheme(TuiTheme.BorderFocused);

        var planId = MakeField(0, prefillPlanId ?? "");
        var goal = new TextView { X = FieldX, Y = 2, Width = Dim.Fill(), Height = 6, Multiline = true, WordWrap = true };
        goal.SetScheme(TuiTheme.Input);
        var goalFile = MakeField(9, "");
        var title = MakeField(12, "");
        var baseBranch = MakeField(14, "main");
        var strategy = new StrategyToggle { X = FieldX, Y = 16 };

        var error = new StyledView { X = 0, Y = 18, Width = Dim.Fill(), Height = 2, CanFocus = false };

        var hints = new StyledView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };
        hints.SetLines(
        [
            StyledLine.Plain(
                "ctrl-enter generate · esc cancel · tab/shift-tab move · space toggles strategy",
                TuiRole.Faint),
        ]);

        window.Add(
            Label(0, "plan id"), planId,
            Label(2, "goal"), goal,
            Label(9, "goal-file"), goalFile,
            Faint(10, "  inline goal above, OR a --goal-file path — exactly one"),
            Label(12, "title"), title,
            Label(14, "base branch"), baseBranch,
            Label(16, "strategy"), strategy,
            error, hints);

        // Explicit focus ring, in visual order — Tab/Shift-Tab walk this rather than trusting
        // the widgets (the goal box would otherwise swallow Tab).
        var ring = new List<View> { planId, goal, goalFile, title, baseBranch, strategy };

        void MoveFocus(View from, int delta)
        {
            var i = ring.IndexOf(from);
            if (i < 0)
                return;
            ring[(i + delta + ring.Count) % ring.Count].SetFocus();
        }

        void Submit()
        {
            var fields = new PlanFormFields
            {
                PlanId = planId.Text ?? string.Empty,
                Goal = goal.Text ?? string.Empty,
                GoalFile = goalFile.Text ?? string.Empty,
                Title = title.Text ?? string.Empty,
                BaseBranch = baseBranch.Text ?? string.Empty,
                Strategy = strategy.Choice,
            };

            PlanFormValidationResult validation;
            try
            {
                validation = PlanFormValidation.Validate(
                    fields, p => File.Exists(p) ? File.ReadAllText(p) : null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // BF-13 §2: an existing-but-unreadable goal file used to throw out of
                // the KeyDown handler and crash the whole TUI (exit 134).
                error.SetLines([StyledLine.Of(
                    new("● ", TuiRole.Error), new($"goal file unreadable: {e.Message}", TuiRole.Error))]);
                return;
            }

            if (!validation.Ok)
            {
                error.SetLines([StyledLine.Of(new("● ", TuiRole.Error), new(validation.Error!, TuiRole.Error))]);
                return;
            }

            result = validation.Resolved;
            Application.RequestStop();
        }

        void OnFieldKey(View field, Key key)
        {
            if (key == Key.Tab)
            {
                MoveFocus(field, +1);
                key.Handled = true;
            }
            else if (key == Key.Tab.WithShift)
            {
                MoveFocus(field, -1);
                key.Handled = true;
            }
            else if (key == Key.Enter.WithCtrl || (key == Key.Enter && !ReferenceEquals(field, goal)))
            {
                // Enter submits from the single-line fields; inside the multi-line goal box it
                // must insert a newline, so only Ctrl-Enter submits from there.
                Submit();
                key.Handled = true;
            }
            else if (key == Key.Esc)
            {
                result = null;
                Application.RequestStop();
                key.Handled = true;
            }
        }

        foreach (var field in ring)
            field.KeyDown += (sender, key) => OnFieldKey((View)sender!, key);

        (prefillPlanId is null ? (View)planId : goal).SetFocus();

        Application.Run(window);
        window.Dispose();
        return result;
    }

    private const int FieldX = 14;

    private static TextField MakeField(int y, string text)
    {
        var field = new TextField { X = FieldX, Y = y, Width = Dim.Fill(), Height = 1, Text = text };
        field.SetScheme(TuiTheme.Input);
        return field;
    }

    private static Label Label(int y, string text)
    {
        var label = new Label { X = 0, Y = y, Text = text };
        label.SetScheme(TuiTheme.BaseScheme);
        return label;
    }

    private static StyledView Faint(int y, string text)
    {
        var view = new StyledView { X = 0, Y = y, Width = Dim.Fill(), Height = 1, CanFocus = false };
        view.SetLines([StyledLine.Plain(text, TuiRole.Faint)]);
        return view;
    }

    /// <summary>A tiny focusable toggle for the single-vs-multi choice — rolled by hand rather
    /// than reaching for v2's churny RadioGroup/CheckBox, in the same spirit as
    /// <see cref="StyledView"/> and <see cref="TuiModal"/>. Space (or ←/→) flips it; the chosen
    /// option is accent, the other faint. Multi is offered but always refuses at submit
    /// (<see cref="PlanFormValidation"/>), never silently downgraded.</summary>
    private sealed class StrategyToggle : View
    {
        public StrategyToggle()
        {
            CanFocus = true;
            Height = 1;
            Width = Dim.Fill();
            SetScheme(TuiTheme.BaseScheme);
            KeyDown += (_, key) =>
            {
                if (key == Key.Space || key == Key.CursorLeft || key == Key.CursorRight)
                {
                    Choice = Choice == PlanStrategyChoice.Single ? PlanStrategyChoice.Multi : PlanStrategyChoice.Single;
                    key.Handled = true;
                    SetNeedsDraw();
                }
            };
        }

        public PlanStrategyChoice Choice { get; private set; } = PlanStrategyChoice.Single;

        protected override bool OnDrawingContent(DrawContext? context)
        {
            Move(0, 0);
            var single = Choice == PlanStrategyChoice.Single;

            SetAttribute(TuiTheme.For(single ? TuiRole.Accent : TuiRole.Faint));
            AddStr(single ? "‹ single ›" : "  single  ");
            SetAttribute(TuiTheme.For(TuiRole.Faint));
            AddStr("   ");
            SetAttribute(TuiTheme.For(!single ? TuiRole.Accent : TuiRole.Faint));
            AddStr(!single ? "‹ multi ›" : "  multi  ");
            SetAttribute(TuiTheme.For(TuiRole.Faint));
            AddStr(HasFocus ? "    (space toggles; multi refuses)" : "");

            SetAttribute(TuiTheme.Normal);
            return true;
        }
    }
}
