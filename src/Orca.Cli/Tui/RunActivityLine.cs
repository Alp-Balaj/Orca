using System.Globalization;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of one <see cref="RunObservation"/> into styled activity-pane
/// lines — the live view's content while a checkpoint session runs (build-order steps 5–6).
/// Mirrors <see cref="Orca.Core.Execution.StreamEventDisplay"/>'s shape (a lenient, throw-free
/// <c>Describe</c>) and <see cref="TuiText"/>'s voice (role tag faint, glyphs coloured,
/// facts dim), with no Terminal.Gui types so it is unit-tested without the shell.
///
/// Two kinds of line interleave here in arrival order. LIFECYCLE events — roles
/// starting/finishing/retrying, gates, the branch, vault records (step 5). And the
/// per-token live STREAM: <see cref="RunObservation.RoleStreamEvent"/> is projected through
/// <see cref="Orca.Core.Execution.StreamEventDisplay.Describe"/> (step 6), so all four roles'
/// invocations are visible as they happen — Planner via the strategy, Implementer via the
/// runner, Reconciler and Verifier via the verifier loops. Both kinds wear the same faint
/// <c>[role]</c> gutter; the stream borrows StreamEventDisplay's text verbatim, which keeps
/// a role's finishing facts ("$x · n turns · Nm Ns") reading identically whether they arrive
/// as the stream's own Result line or the lifecycle <see cref="RoleFinished"/> line.</summary>
internal static class RunActivityLine
{
    public static IReadOnlyList<StyledLine> Describe(RunObservation observation) => observation switch
    {
        RunObservation.RoleStarted e => [RoleStarted(e)],
        RunObservation.RoleFinished e => [RoleFinished(e)],
        RunObservation.RoleParseRetry e => [RoleParseRetry(e)],
        RunObservation.GateStarted e => [GateStarted(e)],
        RunObservation.GateFinished e => [GateFinished(e.Result)],
        RunObservation.BranchReady e => [BranchReady(e)],
        RunObservation.VaultRecordWritten e => [VaultRecordWritten(e)],
        RunObservation.RoleStreamEvent e => RoleStreamEvent(e),

        _ => [],
    };

    /// <summary>The step-6 live stream: each <see cref="DisplayLine"/> the lenient projection
    /// yields becomes one role-tagged styled line — the same <c>[role]</c> gutter the
    /// lifecycle lines use, plus a glyph and colour keyed on <see cref="DisplayKind"/>. One
    /// stream event can yield several lines (assistant text plus tool-use blocks); the empty
    /// case never happens (StreamEventDisplay always falls back to the bare event type).</summary>
    private static IReadOnlyList<StyledLine> RoleStreamEvent(RunObservation.RoleStreamEvent e)
    {
        var display = StreamEventDisplay.Describe(e.Event);
        var lines = new List<StyledLine>(display.Count);
        foreach (var line in display)
            lines.Add(StreamLine(e.Role, line));
        return lines;
    }

    private static StyledLine StreamLine(RoleName role, DisplayLine line)
    {
        var (glyph, glyphRole, textRole) = StreamStyle(line.Kind);
        return new StyledLine([Tag(role), new(glyph, glyphRole), new(line.Text, textRole)]);
    }

    /// <summary>Glyph + colours per stream-event kind, in <see cref="TuiText"/>'s voice:
    /// actions (tool use) and writes (assistant text) get the coral accent glyph the §4
    /// mockup shows; tool results hang off the faint <c>⎿</c> connector like the detail
    /// pane's evidence; the result line stays neutral (its pass/fail is spoken by the
    /// lifecycle <see cref="RoleFinished"/> line, so a green tick here would double up).</summary>
    private static (string Glyph, TuiRole GlyphRole, TuiRole TextRole) StreamStyle(DisplayKind kind) => kind switch
    {
        DisplayKind.Init => ("● ", TuiRole.Faint, TuiRole.Dim),
        DisplayKind.AssistantText => ("✎ ", TuiRole.Accent, TuiRole.Text),
        DisplayKind.ToolUse => ("⚒ ", TuiRole.Accent, TuiRole.Dim),
        DisplayKind.ToolResult => ("⎿ ", TuiRole.Faint, TuiRole.Dim),
        DisplayKind.Result => ("● ", TuiRole.Dim, TuiRole.Dim),
        DisplayKind.RateLimit => ("⏳ ", TuiRole.Warning, TuiRole.Warning),
        _ => ("· ", TuiRole.Faint, TuiRole.Faint),
    };

    private static StyledLine RoleStarted(RunObservation.RoleStarted e)
    {
        var runs = new List<StyledRun>
        {
            Tag(e.Role),
            new("⚒ ", TuiRole.Accent),
            new("started", TuiRole.Text),
            new($"  attempt {e.Attempt}", TuiRole.Dim),
        };
        // Invocation numbers §5.4 parse retries within the attempt; only worth showing
        // once it is past the first, where it means "this is a re-ask".
        if (e.Invocation > 1)
            runs.Add(new StyledRun($" · invocation {e.Invocation}", TuiRole.Dim));
        return new StyledLine(runs);
    }

    private static StyledLine RoleFinished(RunObservation.RoleFinished e)
    {
        var execution = e.Execution;
        var failed = execution.Outcome == ExecutionOutcome.TimedOut
            || execution.Result is not { IsError: false };

        var runs = new List<StyledRun>
        {
            Tag(e.Role),
            failed ? new("✗ ", TuiRole.Error) : new("✓ ", TuiRole.Success),
            failed ? new("failed", TuiRole.Error) : new("done", TuiRole.Text),
        };

        if (execution.Outcome == ExecutionOutcome.TimedOut)
            runs.Add(new StyledRun("  timed out (killed)", TuiRole.Dim));
        else if (execution.Result is { } result)
            runs.Add(new StyledRun("  " + ResultFacts(result), TuiRole.Dim));

        return new StyledLine(runs);
    }

    private static StyledLine RoleParseRetry(RunObservation.RoleParseRetry e) => new(
    [
        Tag(e.Role),
        new("↻ ", TuiRole.Warning),
        new("parse retry", TuiRole.Warning),
        new($" → invocation {e.NextInvocation}", TuiRole.Dim),
        new($"  {FirstLine(e.Error)}", TuiRole.Faint),
    ]);

    private static StyledLine GateStarted(RunObservation.GateStarted e) => new(
    [
        new("[gates] ", TuiRole.Faint),
        new("⚒ ", TuiRole.Accent),
        new(e.Gate, TuiRole.Text),
        new($"  {string.Join(' ', e.Command)}", TuiRole.Dim),
    ]);

    /// <summary>Mirrors the CONTENT of CheckpointRenderer.RenderGate: pass/fail, then the
    /// timeout-or-exit detail with elapsed seconds.</summary>
    private static StyledLine GateFinished(GateCommandResult gate)
    {
        var pass = gate.Outcome == GateOutcome.Pass;
        var detail = gate.TimedOut
            ? $"killed after {gate.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s — timeout"
            : $"exit {gate.ExitCode}, {gate.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
        return new StyledLine(
        [
            new("[gates] ", TuiRole.Faint),
            new(gate.Gate + " ", TuiRole.Text),
            pass ? new("✓", TuiRole.Success) : new("✗", TuiRole.Error),
            new($" ({detail})", TuiRole.Dim),
        ]);
    }

    private static StyledLine BranchReady(RunObservation.BranchReady e) => new(
    [
        new("[branch] ", TuiRole.Faint),
        new("⎇ ", TuiRole.Accent),
        new(e.Branch, TuiRole.Text),
        new(e.Created ? "  (new)" : "  (existing)", TuiRole.Dim),
    ]);

    private static StyledLine VaultRecordWritten(RunObservation.VaultRecordWritten e) => new(
    [
        new($"[{e.Kind}] ", TuiRole.Faint),
        new("✎ ", TuiRole.Accent),
        new("record", TuiRole.Text),
        new($"  {ShortPath(e.Path)}", TuiRole.Dim),
    ]);

    /// <summary>The role tag, e.g. "[implementer] " — faint, lowercase, matching the §4
    /// mockup's <c>[implementer] ⚒ …</c> gutter.</summary>
    private static StyledRun Tag(RoleName role) => new($"[{role.ToString().ToLowerInvariant()}] ", TuiRole.Faint);

    /// <summary>Same facts and format StreamEventDisplay's result line uses
    /// ("$0.50 · 12 turns · 1m03s"), so a finished role reads identically to the stream's
    /// own result event will in step 6.</summary>
    private static string ResultFacts(ClaudeResultSummary result) => string.Join(" · ",
        "$" + result.TotalCostUsd.ToString("0.00", CultureInfo.InvariantCulture),
        result.NumTurns == 1 ? "1 turn" : $"{result.NumTurns} turns",
        DurationText(result.DurationMs));

    private static string DurationText(long durationMs) =>
        durationMs >= 60_000
            ? $"{durationMs / 60_000}m{durationMs % 60_000 / 1_000}s"
            : (durationMs / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s";

    private static string ShortPath(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var name = slash < 0 ? trimmed : trimmed[(slash + 1)..];
        if (slash < 0)
            return name;
        var parentStart = trimmed.LastIndexOf('/', slash - 1);
        var parent = parentStart < 0 ? trimmed[..slash] : trimmed[(parentStart + 1)..slash];
        return $"{parent}/{name}";
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end]).Trim();
        return line.Length <= 120 ? line : line[..119] + "…";
    }
}
