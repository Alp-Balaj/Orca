using System.Globalization;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of a finished <see cref="PlanningOutcome"/> into the styled
/// summary the plan-generate view shows before returning to the shell (build-order step 7).
/// It mirrors, line for line, the CONTENT of <c>PlanGenerateCommand.RenderOutcome</c>: the
/// result facts, then either the draft-plan-written path with the review-and-approve pointer
/// (success — and NEVER an auto-approve; the human flips the plan to approved in Obsidian),
/// or the per-stop-reason failure guidance (rate-limited with its reset window, auth,
/// budget, timeout, and the generic parse/invalid-plan case) with the rejected-file hint.
///
/// Two things planning can end on that are NOT a <see cref="PlanningOutcome"/> get their own
/// renderers here for cohesion: <see cref="GitignoreAdded"/> (the thin front end's
/// <c>OrcaIgnore.EnsureIgnored</c> stop, before any spend) and <see cref="Interrupted"/> (a
/// Ctrl-C cancellation — <c>GenerateAsync</c> throws it rather than returning an outcome, so
/// the screen renders this from the cancelled Task). No Terminal.Gui types, so every branch
/// is unit-tested without the shell — the same fixture pattern as
/// <see cref="RunOutcomeText"/>.</summary>
internal static class PlanOutcomeText
{
    public static IReadOnlyList<StyledLine> Describe(PlanningOutcome outcome)
    {
        var lines = new List<StyledLine>();
        var state = outcome.State;
        var last = state.Attempts.Count > 0 ? state.Attempts[^1] : null;

        // Result facts, mirroring RenderOutcome's summary panel.
        lines.Add(StyledLine.Of(new("attempts     ", TuiRole.Faint), new(state.Attempts.Count.ToString(), TuiRole.Text)));
        if (last is not null)
        {
            lines.Add(StyledLine.Of(
                new("stop reason  ", TuiRole.Faint), new(InvocationStopReasons.ToWire(last.StopReason), TuiRole.Text)));
            lines.Add(StyledLine.Of(
                new("session      ", TuiRole.Faint), new(last.SessionId ?? "(none)", TuiRole.Text)));
        }
        lines.Add(StyledLine.Of(
            new("cost (run)   ", TuiRole.Faint), new(Money(state.AccumulatedCostUsd), TuiRole.Text)));
        lines.Add(StyledLine.Empty);

        if (state.Status == PlanningRunStatus.Succeeded)
        {
            Head(lines, "draft plan written", TuiRole.Success);
            Body(lines, outcome.PlanFilePath);
            // The exact words RenderOutcome uses — and never an auto-approve; the human sets
            // status to approved in the vault (a §7 non-goal for this screen to touch).
            Body(lines, "Review it, set status to approved when it earns it, then run orca checkpoint-run.", TuiRole.Dim);
            return lines;
        }

        // Distinct, actionable failure surfacing per stop reason (the approved
        // rate-limit/auth-expiry contract), mirroring RenderOutcome's switch.
        switch (last?.StopReason)
        {
            case InvocationStopReason.RateLimited:
                // BF-08: same honesty as the checkpoint lane — an unrecognized status is
                // a possible rate limit, not a certain exhaustion.
                var rateLimit = outcome.LastExecution.LastRateLimit;
                Head(lines, "possible rate limit", TuiRole.Warning);
                Body(lines,
                    $"status '{rateLimit?.Status ?? "unknown"}'{rateLimit?.ResetsAtText} — nothing is retried " +
                    "automatically; re-run plan-generate later and attempt numbering resumes. If quota wasn't the cause, the run log has the CLI's full story.");
                break;
            case InvocationStopReason.AuthFailed:
                Head(lines, "authentication failed", TuiRole.Error);
                Body(lines, "this machine's claude login is missing or expired — run claude interactively to log in, then re-run.");
                break;
            case InvocationStopReason.BudgetExceeded:
                Head(lines, "budget ceiling hit", TuiRole.Warning);
                Body(lines, "the CLI killed the run crossing roles.planner.maxBudgetUsd — raise it (or simplify the goal), then re-run.");
                break;
            case InvocationStopReason.TimedOut:
                Head(lines, "timed out", TuiRole.Warning);
                Body(lines, "the wall-clock ceiling killed the run — raise roles.planner.timeoutSeconds or simplify the goal, then re-run.");
                break;
            default:
                Head(lines, "no valid plan", TuiRole.Error);
                Body(lines, "the planning invocation did not produce a valid plan.");
                break;
        }

        if (outcome.FailureDetail is { } detail)
            Body(lines, $"claude/parser says: {detail}", TuiRole.Dim);
        if (last?.ParseFailure is not null)
            Body(lines, $"the rejected file is left at {outcome.PlanFilePath} for inspection.", TuiRole.Dim);
        Body(lines, $"full transcript: {state.Attempts.Count} attempt log(s) under .orca/runs/{state.PlanId}/", TuiRole.Dim);
        return lines;
    }

    /// <summary>The thin front end's gitignore stop (mirroring the CLI's
    /// <c>OrcaIgnore.EnsureIgnored</c> branch): the run has not started and nothing is spent —
    /// commit the change, then re-run. Orca never commits to your branch on its own.</summary>
    public static IReadOnlyList<StyledLine> GitignoreAdded()
    {
        var lines = new List<StyledLine>();
        Head(lines, "gitignore updated", TuiRole.Warning);
        Body(lines, $"added '{OrcaIgnore.Entry}' to .gitignore — commit that change, then re-run.");
        Body(lines, "Orca never commits to your branch on its own. Nothing was spent.", TuiRole.Dim);
        return lines;
    }

    /// <summary>A Ctrl-C interrupt during generation. <c>GenerateAsync</c> does not swallow
    /// cancellation (unlike the checkpoint session) — the executor kills its process tree and
    /// the exception propagates — so the screen renders this from the cancelled Task. Planning
    /// state stays in-progress and the next run resumes by attempt numbering.</summary>
    public static IReadOnlyList<StyledLine> Interrupted()
    {
        var lines = new List<StyledLine>();
        Head(lines, "interrupted", TuiRole.Warning);
        Body(lines, "re-run plan-generate to resume; attempt numbering continues, nothing completed is re-paid.");
        return lines;
    }

    private static void Head(List<StyledLine> lines, string word, TuiRole role) =>
        lines.Add(StyledLine.Of(new("● ", role), new(word, role)));

    private static void Body(List<StyledLine> lines, string text, TuiRole role = TuiRole.Text) =>
        lines.Add(StyledLine.Plain(text, role));

    private static string Money(decimal usd) => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
}
