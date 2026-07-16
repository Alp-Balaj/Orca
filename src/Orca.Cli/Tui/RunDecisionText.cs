using System.Globalization;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of a <see cref="CheckpointDecisionContext"/> into the styled
/// body the gate modals render before they ask — the invocation result, the verification
/// verdicts, the working-tree changes, and the attempt cost (build-order step 5, item 4).
/// It mirrors the CONTENT of <c>CheckpointRenderer.RenderExecution</c> +
/// <c>RenderVerification</c> + <c>RenderChanges</c> so the human sees the same facts the
/// CLI shows before the same decision. No Terminal.Gui types.</summary>
internal static class RunDecisionText
{
    public static IReadOnlyList<StyledLine> Describe(CheckpointDecisionContext context)
    {
        var lines = new List<StyledLine>();

        if (context.Run is { } run)
            AppendExecution(lines, run);

        if (context.Verification is { } verification)
        {
            if (lines.Count > 0)
                lines.Add(StyledLine.Empty);
            AppendVerification(lines, verification);
        }

        if (lines.Count > 0)
            lines.Add(StyledLine.Empty);
        AppendChanges(lines, context.StatusLines, context.DiffStat);

        if (context.AttemptCostUsd is { } cost)
        {
            lines.Add(StyledLine.Empty);
            lines.Add(StyledLine.Of(
                new("attempt cost ", TuiRole.Faint),
                new(Money(cost), TuiRole.Text)));
        }

        return lines;
    }

    private static void AppendExecution(List<StyledLine> lines, CheckpointRunOutcome outcome)
    {
        var e = outcome.Execution;
        lines.Add(StyledLine.Plain("invocation result", TuiRole.Faint));

        var failed = e.Outcome == ExecutionOutcome.TimedOut || e.Result is not { IsError: false };
        lines.Add(StyledLine.Of(
            new("  outcome      ", TuiRole.Dim),
            e.Outcome == ExecutionOutcome.TimedOut
                ? new("timed out (killed)", TuiRole.Error)
                : failed ? new("error", TuiRole.Error) : new("succeeded", TuiRole.Success)));
        lines.Add(StyledLine.Of(
            new("  stop reason  ", TuiRole.Dim),
            new(InvocationStopReasons.ToWire(e.StopReason),
                e.StopReason == InvocationStopReason.Succeeded ? TuiRole.Success : TuiRole.Error)));

        if (e.Result is { } r)
        {
            // On failure the CLI's own words beat any paraphrase (a wrong model id, a
            // logged-out machine, a budget kill all explain themselves here). On success
            // the same text is the Implementer's own summary — or exactly why it stopped
            // early on a blocked criterion — and the human deciding needs it (SUG-11 §3).
            if (r.ResultText is { } text)
                lines.Add(StyledLine.Of(
                    new(r.IsError ? "  claude says  " : "  summary      ", TuiRole.Dim),
                    new(text, TuiRole.Text)));
            lines.Add(StyledLine.Of(
                new("  cost         ", TuiRole.Dim),
                new(Money(r.TotalCostUsd), TuiRole.Text),
                new($" · {(r.NumTurns == 1 ? "1 turn" : $"{r.NumTurns} turns")}", TuiRole.Dim)));
        }
    }

    /// <summary>Mirrors CheckpointRenderer.RenderVerification: gates (from the live results
    /// or the record), the reconciler verdict with missing/extra, per-criterion results
    /// with notes, and the final derived verdict.</summary>
    private static void AppendVerification(List<StyledLine> lines, CheckpointVerificationOutcome v)
    {
        lines.Add(StyledLine.Plain("verification", TuiRole.Faint));

        var build = v.BuildGate?.Outcome ?? v.Record?.Gates.Build;
        var tests = v.TestsGate?.Outcome ?? v.Record?.Gates.Tests;
        if (build is { } b || tests is { } t)
            lines.Add(StyledLine.Of(
                new("  gates  ", TuiRole.Dim),
                new($"build {GateWord(build)}", GateRole(build)),
                new(" · ", TuiRole.Dim),
                new($"tests {GateWord(tests)}", GateRole(tests))));

        if (v.Reconciliation is { } reconciliation)
        {
            lines.Add(StyledLine.Of(
                new("  reconciler ", TuiRole.Dim),
                new($"implements_plan: {reconciliation.ImplementsPlan.ToString().ToLowerInvariant()}",
                    reconciliation.ImplementsPlan == ReconciliationVerdict.Full ? TuiRole.Success : TuiRole.Warning)));
            foreach (var missing in reconciliation.Missing)
                lines.Add(StyledLine.Of(new("    missing  ", TuiRole.Error), new(missing, TuiRole.Text)));
            foreach (var extra in reconciliation.Extra)
                lines.Add(StyledLine.Of(new("    extra    ", TuiRole.Warning), new(extra, TuiRole.Text)));
        }

        foreach (var item in v.Record?.Semantic ?? [])
        {
            var pass = item.Verdict == SemanticVerdict.Pass;
            lines.Add(StyledLine.Of(
                new("  ", TuiRole.Dim),
                new(item.Verdict switch
                {
                    SemanticVerdict.Pass => "✓ ",
                    SemanticVerdict.Fail => "✗ ",
                    _ => "? ",
                }, pass ? TuiRole.Success : item.Verdict == SemanticVerdict.Unclear ? TuiRole.Warning : TuiRole.Error),
                new(item.Criterion, TuiRole.Text)));
            if (item.Note is { } note)
                lines.Add(StyledLine.Plain($"      {note}", TuiRole.Dim));
        }

        if (v.Record is { } record)
            lines.Add(StyledLine.Of(
                new("  verdict  ", TuiRole.Dim),
                new(record.Verdict == VerificationVerdict.Pass ? "pass" : "fail",
                    record.Verdict == VerificationVerdict.Pass ? TuiRole.Success : TuiRole.Error)));

        if (v.CostUsd > 0m)
            lines.Add(StyledLine.Of(
                new("  verification cost ", TuiRole.Dim), new(Money(v.CostUsd), TuiRole.Text)));
    }

    private static void AppendChanges(List<StyledLine> lines, IReadOnlyList<string> statusLines, string diffStat)
    {
        lines.Add(StyledLine.Plain($"working tree changes ({statusLines.Count} paths)", TuiRole.Faint));
        foreach (var status in statusLines)
            lines.Add(StyledLine.Plain("  " + status, TuiRole.Dim));
        if (diffStat.Length > 0)
        {
            lines.Add(StyledLine.Plain("diffstat (tracked files)", TuiRole.Faint));
            foreach (var stat in diffStat.Split('\n'))
                lines.Add(StyledLine.Plain("  " + stat.TrimEnd('\r'), TuiRole.Dim));
        }
    }

    private static string GateWord(GateOutcome? outcome) => outcome switch
    {
        GateOutcome.Pass => "✓",
        GateOutcome.Fail => "✗",
        _ => "—",
    };

    private static TuiRole GateRole(GateOutcome? outcome) => outcome switch
    {
        GateOutcome.Pass => TuiRole.Success,
        GateOutcome.Fail => TuiRole.Error,
        _ => TuiRole.Dim,
    };

    private static string Money(decimal usd) => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
}
