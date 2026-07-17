using System.Globalization;
using Orca.Core.Config;
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

/// <summary>The full-screen run window (build-order steps 5–6, owner-chosen layout): a
/// dedicated <c>Window</c> that takes over the terminal for the duration of one checkpoint
/// session and hands it back to the read-only shell when the session ends. It owns the
/// threading model (tui-plan §4): the session runs on a background <see cref="Task"/>; its
/// <see cref="RunEventChannel"/> observer writes observations onto the channel from the
/// executor's and session's threads; a drain (<c>Application.AddTimeout</c>) pulls each batch
/// onto the UI thread, folds it into the <see cref="RunActivityBuffer"/>, and repaints the
/// activity pane once; the three <see cref="ICheckpointGate"/> moments render as modal
/// dialogs via <see cref="TuiCheckpointGate"/>. Executor threads never touch a widget; the UI
/// thread never blocks on the stream.
///
/// Step 6 makes the activity pane the live per-token view for all four roles (the stream now
/// flows through <see cref="RunActivityLine"/> → <see cref="StreamEventDisplay"/>), with
/// scrollback that pauses tail-follow (<see cref="StyledView.IsFollowing"/>), a pane header
/// that reports how many display events were elided under load, and an Interrupt affordance
/// (Ctrl-C, confirmed) that cancels the in-flight invocation into the
/// <see cref="CheckpointSessionOutcome.Interrupted"/> outcome. Interrupt is the ONLY control
/// the live pane adds and the only way to reach quit while a session is live — the recorded,
/// resumable Ctrl-C the CLI always had. When <c>ExecuteAsync</c> returns, the outcome is
/// rendered and the human dismisses back to the shell, which then rebuilds its report.</summary>
internal sealed class RunScreen : Window
{
    private readonly CheckpointRunPreview _preview;
    private readonly RunEventChannel _channel = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly RunActivityBuffer _buffer = new();
    private readonly StyledView _activity;
    private readonly StyledView _activityHeader;
    private readonly StyledView _footer;

    private decimal _spentThisRun;
    private RoleName? _currentRole;
    private bool _done;
    private bool _interrupting;
    private bool _abandonOffered;
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

        // A one-row header inside the pane: tail-follow state and, under load, the elided
        // display-event count with the honest "the log has everything" note.
        _activityHeader = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        _activity = new StyledView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _activity.FollowChanged += UpdateActivityHeader;   // repaint the indicator on scroll
        activityPane.Add(_activityHeader, _activity);

        _footer = new StyledView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        Add(header, activityPane, _footer);
        KeyDown += OnKey;

        RenderActivity();
        UpdateActivityHeader();
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

    /// <summary>UI thread. Drain the channel in one coalesced batch, fold the projected lines
    /// into the bounded buffer (lifecycle never evicted, stream oldest-first under load), and
    /// repaint once — following the tail unless the reader has scrolled up. Also used as the
    /// gate's flush hook so events preceding a gate land before its modal appears.</summary>
    private void FlushChannel()
    {
        var batch = new List<RunObservation>();
        if (_channel.DrainInto(batch) == 0)
            return;

        foreach (var observation in batch)
        {
            switch (observation)
            {
                case RunObservation.RoleStarted started:
                    _currentRole = started.Role;
                    break;
                case RunObservation.RoleFinished finished:
                    if (finished.Execution.Result is { } result)
                        _spentThisRun += result.TotalCostUsd;
                    _currentRole = null;
                    break;
            }

            var lines = RunActivityLine.Describe(observation);
            if (lines.Count == 0)
                continue;
            if (observation is RunObservation.RoleStreamEvent)
                _buffer.AppendStream(lines);
            else
                _buffer.AppendLifecycle(lines);
        }

        RenderActivity();
        UpdateActivityHeader();
        UpdateFooter();
    }

    private void OnComplete(Task<CheckpointSessionResult> task)
    {
        _done = true;
        if (_drainToken is { } token)
            Application.RemoveTimeout(token);   // no stray tick after the screen disposes
        _channel.Complete();
        FlushChannel();               // final drain: no observation lost before the outcome

        _buffer.AppendLifecycle([StyledLine.Empty]);
        if (task.IsFaulted)
        {
            // ExecuteAsync swallows cancellation into an Interrupted result; a fault here is
            // an unexpected error, surfaced rather than crashing the loop.
            SessionResult = null;
            var error = task.Exception?.GetBaseException();
            _buffer.AppendLifecycle(
            [
                StyledLine.Of(new("● ", TuiRole.Error), new("run failed unexpectedly", TuiRole.Error)),
                StyledLine.Plain(error?.Message ?? "unknown error", TuiRole.Dim),
            ]);
        }
        else
        {
            SessionResult = task.Result;
            _buffer.AppendLifecycle(RunOutcomeText.Describe(task.Result));
        }

        RenderActivity();
        UpdateActivityHeader();
        UpdateFooter();
        _activity.SetFocus();
    }

    private void OnKey(object? sender, Key key)
    {
        if (!_done)
        {
            // Interrupt is the one live control and the only path to quit while a session
            // runs (tui-plan §3.3, decision 7): Ctrl-C — with 'i' as a fallback should the
            // driver not deliver Ctrl-C — confirms, then cancels the in-flight invocation.
            if (key == Key.C.WithCtrl || key == Key.I)
            {
                key.Handled = true;
                TryInterrupt();
                return;
            }

            // BF-06: a hung drain (grandchild holding the pipes) used to leave the TUI
            // permanently stuck behind the swallowed quit keys. Once the interrupt has
            // visibly stalled, Ctrl-Q abandons the session — the child may keep running,
            // and the footer says so.
            if (_abandonOffered && key == Key.Q.WithCtrl)
            {
                key.Handled = true;
                Application.RequestStop();
                return;
            }

            // Quit is otherwise reachable only by interrupting first; swallow quit-ish
            // keys. The activity pane still scrolls (its own keys are handled first).
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

    /// <summary>Confirm, then cancel the in-flight invocation. The executor kills its process
    /// tree, <c>ExecuteAsync</c> resolves to <see cref="CheckpointSessionOutcome.Interrupted"/>,
    /// and the existing resume machinery recovers the attempt on the next run with nothing
    /// re-paid. Idempotent — a second Ctrl-C while already cancelling is ignored.</summary>
    private void TryInterrupt()
    {
        if (_interrupting)
            return;

        var role = _currentRole is { } r ? r.ToString().ToLowerInvariant() : "invocation";
        var confirmed = TuiModal.Ask("interrupt",
        [
            StyledLine.Of(new("● ", TuiRole.Warning), new($"Interrupt the running {role}?", TuiRole.Text)),
            StyledLine.Empty,
            StyledLine.Plain("The in-flight invocation is cancelled and its process tree killed.", TuiRole.Dim),
            StyledLine.Plain("It re-runs on the next checkpoint-run; completed sub-steps are not re-paid.", TuiRole.Dim),
        ],
            new TuiModal.Choice<bool>(Key.Enter, "enter interrupt", true),
            new TuiModal.Choice<bool>(Key.C.WithCtrl, "ctrl-c interrupt", true),
            new TuiModal.Choice<bool>(Key.Esc, "esc keep running", false));

        if (!confirmed)
            return;

        // BF-05 polish: the run can finish while the confirm modal is open — appending
        // "interrupting…" after the outcome summary would claim an interrupt that never
        // happened.
        if (_done)
            return;

        _interrupting = true;
        _cts.Cancel();

        // BF-06: if the session hasn't resolved ~10s after the cancel (a gate or pump
        // drain wedged on a surviving child), stop pretending — offer the abandon key.
        Application.AddTimeout(TimeSpan.FromSeconds(10), () =>
        {
            if (!_done && _interrupting)
            {
                _abandonOffered = true;
                UpdateFooter();
            }

            return false;
        });
        _buffer.AppendLifecycle(
        [
            StyledLine.Empty,
            StyledLine.Of(
                new("● ", TuiRole.Warning),
                new("interrupting… cancelling the in-flight invocation", TuiRole.Warning)),
        ]);
        RenderActivity();
        UpdateActivityHeader();
        UpdateFooter();
    }

    /// <summary>Show the launching placeholder until the first observation lands, then the
    /// bounded buffer's lines. Tail-follow (unless the reader paused it) is
    /// <see cref="StyledView"/>'s job.</summary>
    private void RenderActivity() =>
        _activity.SetLines(_buffer.Count == 0 && !_done ? _launchingLines : _buffer.Lines, scrollToEnd: true);

    private void UpdateActivityHeader()
    {
        var runs = new List<StyledRun>();
        if (_done)
        {
            runs.Add(new StyledRun("run complete", TuiRole.Faint));
        }
        else
        {
            runs.Add(new StyledRun("live  ", TuiRole.Accent));
            runs.Add(_activity.IsFollowing
                ? new StyledRun("▼ following", TuiRole.Faint)
                : new StyledRun("⏸ paused — End for latest", TuiRole.Warning));
        }

        // The honest drop note (tui-plan §4): the pane elided display events under load, but
        // the run log on disk is the complete record.
        if (_buffer.Elided > 0)
        {
            runs.Add(new StyledRun("   ", TuiRole.Text));
            runs.Add(new StyledRun($"{_buffer.Elided} events elided", TuiRole.Dim));
            runs.Add(new StyledRun(" — the run log has everything", TuiRole.Faint));
        }

        _activityHeader.SetLines([new StyledLine(runs)]);
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
            : _abandonOffered
                ? new StyledRun("   session not responding — ctrl-q abandon (child may keep running)", TuiRole.Error)
                : _interrupting
                    ? new StyledRun("   interrupting…", TuiRole.Warning)
                    : new StyledRun("   ↑↓ scroll · ctrl-c interrupt", TuiRole.Faint));
        _footer.SetLines([new StyledLine(runs)]);
    }

    private static readonly IReadOnlyList<StyledLine> _launchingLines =
        [StyledLine.Plain("launching…", TuiRole.Dim)];

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
