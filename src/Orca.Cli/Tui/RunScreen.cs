using System.Globalization;
using Orca.Core.Run;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Same [Obsolete] legacy-static Application surface (Run/Invoke/AddTimeout/RequestStop) the
// rest of the TUI lane uses under decision 1's standing rationale (§1).
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>The full-screen run window (build-order step 5, owner-chosen layout): a
/// dedicated <c>Window</c> that takes over the terminal for the duration of one checkpoint
/// session and hands it back to the read-only shell when the session ends. It owns the
/// threading model (tui-plan §4): the session runs on a background <see cref="Task"/>; its
/// <see cref="RunEventChannel"/> observer writes lifecycle observations onto a bounded
/// channel from the executor's thread; a drain (<c>Application.AddTimeout</c>) pulls each
/// batch onto the UI thread and repaints the activity pane once; the three
/// <see cref="ICheckpointGate"/> moments render as modal dialogs via
/// <see cref="TuiCheckpointGate"/>. Executor threads never touch a widget; the UI thread
/// never blocks on the stream.
///
/// While the session is live the run view owns the UI and quit is blocked — there is no
/// Interrupt affordance yet (that is step 6); the session runs modally to its gate
/// decision. When <c>ExecuteAsync</c> returns, the outcome is rendered and the human
/// dismisses back to the shell, which then rebuilds its report.</summary>
internal sealed class RunScreen : Window
{
    private readonly CheckpointRunPreview _preview;
    private readonly RunEventChannel _channel = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<StyledLine> _activityLines = [];
    private readonly StyledView _activity;
    private readonly StyledView _footer;

    private decimal _spentThisRun;
    private bool _done;
    private object? _drainToken;

    private RunScreen(CheckpointRunSession session, CheckpointRunPreview preview)
    {
        _preview = preview;

        Title = string.Empty;
        BorderStyle = LineStyle.None;
        SetScheme(TuiTheme.BaseScheme);
        Padding!.Thickness = new Thickness(1, 0, 1, 0);

        var header = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        header.SetLines(
        [
            StyledLine.Of(
                new("✻ ", TuiRole.Accent),
                new("Orca", TuiRole.Heading),
                new("  running ", TuiRole.Faint),
                new(preview.Checkpoint.Id, TuiRole.Text),
                new($"  ·  {preview.Plan.PlanId} v{preview.Plan.PlanVersion}", TuiRole.Faint)),
        ]);

        var activityPane = MakePane("activity");
        activityPane.X = 0;
        activityPane.Y = 2;
        activityPane.Width = Dim.Fill();
        activityPane.Height = Dim.Fill(2);

        _activity = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        activityPane.Add(_activity);

        _footer = new StyledView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        Add(header, activityPane, _footer);
        KeyDown += OnKey;

        _activityLines.Add(_launching);
        _activity.SetLines(_activityLines, scrollToEnd: true);
        UpdateFooter();
        _activity.SetFocus();

        var gate = new TuiCheckpointGate(FlushChannel);

        // The drain: at UI cadence (not event cadence), pull whatever accumulated in one
        // batch and repaint once. Returns false — self-removing — once the session is done,
        // and is also removed explicitly in OnComplete (the token is Application-global, so
        // a stray tick must not survive into the shell's loop after this screen disposes).
        _drainToken = Application.AddTimeout(TimeSpan.FromMilliseconds(60), () =>
        {
            FlushChannel();
            return !_done;
        });

        // The session runs on a background Task; its observer (the channel) and its gate
        // marshal back to this UI thread. Nothing here blocks the UI thread on the stream.
        var task = Task.Run(() => session.ExecuteAsync(preview, gate, _channel, _cts.Token));
        _ = task.ContinueWith(
            t => Application.Invoke(() => OnComplete(t)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Drive one checkpoint session to its end in a full-screen window, then
    /// return its result (null only if the session Task faulted unexpectedly — the caller
    /// just refreshes the shell either way). Runs a nested Terminal.Gui loop; returns once
    /// the human dismisses the outcome.</summary>
    public static CheckpointSessionResult? Run(CheckpointRunSession session, CheckpointRunPreview preview)
    {
        var screen = new RunScreen(session, preview);
        Application.Run(screen);
        var result = screen.SessionResult;
        screen.Dispose();
        return result;
    }

    public CheckpointSessionResult? SessionResult { get; private set; }

    /// <summary>UI thread. Drain the channel in one coalesced batch, append the projected
    /// lifecycle lines, and repaint once — following the tail. Also used as the gate's
    /// flush hook so events preceding a gate land before its modal appears.</summary>
    private void FlushChannel()
    {
        var batch = new List<RunObservation>();
        if (_channel.DrainInto(batch) == 0)
            return;

        // Drop the transient "launching…" placeholder as soon as real events arrive.
        if (_activityLines is [{ } first] && ReferenceEquals(first, _launching))
            _activityLines.Clear();

        foreach (var observation in batch)
        {
            if (observation is RunObservation.RoleFinished { Execution.Result: { } result })
                _spentThisRun += result.TotalCostUsd;
            _activityLines.AddRange(RunActivityLine.Describe(observation));
        }

        _activity.SetLines(_activityLines, scrollToEnd: true);
        UpdateFooter();
    }

    private void OnComplete(Task<CheckpointSessionResult> task)
    {
        _done = true;
        if (_drainToken is { } token)
            Application.RemoveTimeout(token);   // no stray tick after the screen disposes
        _channel.Complete();
        FlushChannel();               // final drain: no observation lost before the outcome

        _activityLines.Add(StyledLine.Empty);
        if (task.IsFaulted)
        {
            // ExecuteAsync swallows cancellation into an Interrupted result; a fault here is
            // an unexpected error, surfaced rather than crashing the loop.
            SessionResult = null;
            var error = task.Exception?.GetBaseException();
            _activityLines.Add(StyledLine.Of(new("● ", TuiRole.Error), new("run failed unexpectedly", TuiRole.Error)));
            _activityLines.Add(StyledLine.Plain(error?.Message ?? "unknown error", TuiRole.Dim));
        }
        else
        {
            SessionResult = task.Result;
            _activityLines.AddRange(RunOutcomeText.Describe(task.Result));
        }

        _activity.SetLines(_activityLines, scrollToEnd: true);
        UpdateFooter();
        _activity.SetFocus();
    }

    private void OnKey(object? sender, Key key)
    {
        if (!_done)
        {
            // The run view owns the UI until the session completes; quit is blocked (no
            // Interrupt yet). Swallow quit-ish keys; the activity pane still scrolls.
            if (key == Key.Q || key == Key.Esc || key == Key.Q.WithCtrl)
                key.Handled = true;
            return;
        }

        if (key == Key.Enter || key == Key.Esc || key == Key.Q || key == Key.Space)
        {
            Application.RequestStop();
            key.Handled = true;
        }
    }

    private void UpdateFooter()
    {
        var runs = new List<StyledRun>
        {
            new(_preview.Branch, TuiRole.Dim),
        };
        if (_spentThisRun > 0m)
        {
            runs.Add(new StyledRun("  spent ", TuiRole.Faint));
            runs.Add(new StyledRun("$" + _spentThisRun.ToString("0.00", CultureInfo.InvariantCulture), TuiRole.Text));
        }

        runs.Add(_done
            ? new StyledRun("   ↑↓ scroll · enter/esc return to shell", TuiRole.Faint)
            : new StyledRun("   running… (quit blocked until the decision completes)", TuiRole.Warning));
        _footer.SetLines([new StyledLine(runs)]);
    }

    private static readonly StyledLine _launching = StyledLine.Plain("launching…", TuiRole.Dim);

    /// <summary>A bordered pane matching the shell's chrome — rounded, quiet grey, with the
    /// label over the top border line. The run window has one pane and it always owns
    /// focus, so it stays coral throughout.</summary>
    private static View MakePane(string title)
    {
        var pane = new View { BorderStyle = LineStyle.Rounded, CanFocus = true };
        pane.SetScheme(TuiTheme.BaseScheme);
        pane.Padding!.Thickness = new Thickness(1, 0, 1, 0);

        var borderView = pane.Border!.GetOrCreateView();
        borderView.SetScheme(TuiTheme.BorderFocused);
        var titleLabel = new Label { X = 2, Y = 0, Text = $" {title} " };
        titleLabel.SetScheme(TuiTheme.BorderFocused);
        borderView.Add(titleLabel);
        return pane;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cts.Dispose();
        base.Dispose(disposing);
    }
}
