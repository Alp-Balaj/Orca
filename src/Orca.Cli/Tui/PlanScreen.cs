using System.Globalization;
using Orca.Core.Planning;
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

/// <summary>The full-screen plan-generate window (build-order step 7): the gate-less sibling
/// of <see cref="RunScreen"/>. It drives one Planner run — <c>SingleCandidatePlanningStrategy
/// .GenerateAsync(preview, observer, ct)</c> on a background <see cref="Task"/> — through the
/// SAME live pane step 6 built for checkpoints, reusing its pure components verbatim: the
/// <see cref="RunEventChannel"/> observer, the <see cref="RunActivityBuffer"/> drop policy,
/// the <see cref="RunActivityLine"/> projection (so the planner's RoleStarted/RoleStreamEvent/
/// RoleFinished/RoleParseRetry/VaultRecordWritten light up identically to the four-role
/// checkpoint stream), and <see cref="StyledView"/>'s scrollback/tail-follow. The threading
/// model is identical to <see cref="RunScreen"/>: a 60 ms drain marshals batches onto the UI
/// thread and repaints once; executor threads never touch a widget.
///
/// What is DELIBERATELY absent: planning has no <see cref="ICheckpointGate"/>, so there are
/// no gate modals — the single-vs-multi decision already happened in the form (multi refuses),
/// and parse retries are automatic (owner decision). The Planner is the only role that streams
/// here. This is a sibling rather than a generalization of <see cref="RunScreen"/> so the
/// tested checkpoint flow is not reworked; the ~screen-level threading glue is Terminal.Gui-
/// specific and re-implemented thinly.
///
/// One contract difference drives the completion handling: <c>GenerateAsync</c> does NOT
/// swallow cancellation (the checkpoint session does) — the executor rethrows
/// <c>OperationCanceledException</c> — so a Ctrl-C Interrupt lands as a cancelled Task, which
/// <see cref="OnComplete"/> renders via <see cref="PlanOutcomeText.Interrupted"/> rather than
/// from a <see cref="PlanningOutcome"/>.</summary>
internal sealed class PlanScreen : Window
{
    private readonly PlanningPreview _preview;
    private readonly RunEventChannel _channel = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly RunActivityBuffer _buffer = new();
    private readonly StyledView _activity;
    private readonly StyledView _activityHeader;
    private readonly StyledView _footer;

    private decimal _spentThisRun;
    private bool _done;
    private bool _interrupting;
    private object? _drainToken;

    private PlanScreen(IPlanningStrategy strategy, PlanningPreview preview)
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
                new("  generating plan ", TuiRole.Faint),
                new(preview.Request.PlanId, TuiRole.Text),
                new($"  ·  attempt {preview.Attempt}", TuiRole.Faint)),
        ]);

        var activityPane = MakePane("planner");
        activityPane.X = 0;
        activityPane.Y = 2;
        activityPane.Width = Dim.Fill();
        activityPane.Height = Dim.Fill(2);

        _activityHeader = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        _activity = new StyledView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _activity.FollowChanged += UpdateActivityHeader;
        activityPane.Add(_activityHeader, _activity);

        _footer = new StyledView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        Add(header, activityPane, _footer);
        KeyDown += OnKey;

        RenderActivity();
        UpdateActivityHeader();
        UpdateFooter();
        _activity.SetFocus();

        // The drain: pull whatever accumulated in one batch and repaint once (UI cadence, not
        // event cadence). Self-removing once the run is done; also removed in OnComplete so no
        // stray tick survives into the shell's loop after this screen disposes.
        _drainToken = Application.AddTimeout(TimeSpan.FromMilliseconds(60), () =>
        {
            FlushChannel();
            return !_done;
        });

        // The run is a single GenerateAsync on a background Task; the channel observer marshals
        // its lifecycle + per-token stream back to this UI thread. There is no gate to consult.
        var task = Task.Run(() => strategy.GenerateAsync(preview, _channel, _cts.Token));
        _ = task.ContinueWith(
            t => Application.Invoke(() => OnComplete(t)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Drive one plan-generate run to its end in a full-screen window, then return
    /// its outcome (null when interrupted or faulted — the caller just refreshes the shell
    /// either way). Runs a nested Terminal.Gui loop; returns once the human dismisses the
    /// outcome.</summary>
    public static PlanningOutcome? Run(IPlanningStrategy strategy, PlanningPreview preview)
    {
        var screen = new PlanScreen(strategy, preview);
        Application.Run(screen);
        var outcome = screen.Outcome;
        screen.Dispose();
        return outcome;
    }

    public PlanningOutcome? Outcome { get; private set; }

    /// <summary>UI thread. Drain the channel in one coalesced batch, fold the projected lines
    /// into the bounded buffer (lifecycle never evicted, stream oldest-first under load), and
    /// repaint once — following the tail unless the reader has scrolled up.</summary>
    private void FlushChannel()
    {
        var batch = new List<RunObservation>();
        if (_channel.DrainInto(batch) == 0)
            return;

        foreach (var observation in batch)
        {
            if (observation is RunObservation.RoleFinished finished && finished.Execution.Result is { } result)
                _spentThisRun += result.TotalCostUsd;

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

    private void OnComplete(Task<PlanningOutcome> task)
    {
        _done = true;
        if (_drainToken is { } token)
            Application.RemoveTimeout(token);
        _channel.Complete();
        FlushChannel();               // final drain: no observation lost before the outcome

        _buffer.AppendLifecycle([StyledLine.Empty]);

        // GenerateAsync rethrows cancellation rather than returning an outcome, so an Interrupt
        // lands as a cancelled (or OCE-faulted) Task — rendered as an interrupt, not a failure.
        if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException)
        {
            Outcome = null;
            _buffer.AppendLifecycle(PlanOutcomeText.Interrupted());
        }
        else if (task.IsFaulted)
        {
            Outcome = null;
            var error = task.Exception?.GetBaseException();
            _buffer.AppendLifecycle(
            [
                StyledLine.Of(new("● ", TuiRole.Error), new("plan generation failed unexpectedly", TuiRole.Error)),
                StyledLine.Plain(error?.Message ?? "unknown error", TuiRole.Dim),
            ]);
        }
        else
        {
            Outcome = task.Result;
            _buffer.AppendLifecycle(PlanOutcomeText.Describe(task.Result));
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
            // Interrupt is the one live control and the only path to quit while the run is in
            // flight (tui-plan §3.3, decision 7): Ctrl-C — with 'i' as a fallback should the
            // driver not deliver Ctrl-C — confirms, then cancels the in-flight invocation.
            if (key == Key.C.WithCtrl || key == Key.I)
            {
                key.Handled = true;
                TryInterrupt();
                return;
            }

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

    /// <summary>Confirm, then cancel the in-flight Planner invocation. The executor kills its
    /// process tree and rethrows cancellation, so <c>GenerateAsync</c>'s Task ends cancelled;
    /// planning state stays in-progress and the next plan-generate resumes by attempt
    /// numbering with nothing completed re-paid. Idempotent.</summary>
    private void TryInterrupt()
    {
        if (_interrupting)
            return;

        var confirmed = TuiModal.Ask("interrupt",
        [
            StyledLine.Of(new("● ", TuiRole.Warning), new("Interrupt the running planner?", TuiRole.Text)),
            StyledLine.Empty,
            StyledLine.Plain("The in-flight invocation is cancelled and its process tree killed.", TuiRole.Dim),
            StyledLine.Plain("It re-runs on the next plan-generate; attempt numbering continues, nothing completed is re-paid.", TuiRole.Dim),
        ],
            new TuiModal.Choice<bool>(Key.Enter, "enter interrupt", true),
            new TuiModal.Choice<bool>(Key.C.WithCtrl, "ctrl-c interrupt", true),
            new TuiModal.Choice<bool>(Key.Esc, "esc keep running", false));

        if (!confirmed)
            return;

        _interrupting = true;
        _cts.Cancel();
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
            new(_preview.RelativePlanPath, TuiRole.Dim),
        };
        if (_spentThisRun > 0m)
        {
            runs.Add(new StyledRun("  spent ", TuiRole.Faint));
            runs.Add(new StyledRun("$" + _spentThisRun.ToString("0.00", CultureInfo.InvariantCulture), TuiRole.Text));
        }

        runs.Add(_done
            ? new StyledRun("   ↑↓ scroll · enter/esc return to shell", TuiRole.Faint)
            : _interrupting
                ? new StyledRun("   interrupting…", TuiRole.Warning)
                : new StyledRun("   ↑↓ scroll · ctrl-c interrupt", TuiRole.Faint));
        _footer.SetLines([new StyledLine(runs)]);
    }

    private static readonly IReadOnlyList<StyledLine> _launchingLines =
        [StyledLine.Plain("launching…", TuiRole.Dim)];

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
