using System.Globalization;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of a finished <see cref="CheckpointSessionResult"/> into the
/// styled summary the run view shows before returning to the shell (build-order step 5,
/// item 5). Exhaustive over every <see cref="CheckpointSessionOutcome"/> — including
/// <see cref="CheckpointSessionOutcome.Interrupted"/>, which the TUI can't reach until the
/// step-6 Interrupt affordance exists but is rendered anyway so the switch is total —
/// mirroring the CONTENT of <c>CheckpointRunCommand.Render</c> / <c>CheckpointRenderer</c>
/// in the CLI lane. No Terminal.Gui types, so it is unit-tested without the shell.</summary>
internal static class RunOutcomeText
{
    public static IReadOnlyList<StyledLine> Describe(CheckpointSessionResult result)
    {
        var lines = new List<StyledLine>();
        switch (result.Outcome)
        {
            case CheckpointSessionOutcome.GitignoreAdded:
                Head(lines, "gitignore updated", TuiRole.Warning);
                Body(lines, $"added '{OrcaIgnore.Entry}' to .gitignore — commit that change, then re-run.");
                Body(lines, "Orca never commits to your branch on its own.", TuiRole.Dim);
                break;

            case CheckpointSessionOutcome.InvocationFailed:
                Head(lines, "invocation failed", TuiRole.Error);
                Body(lines, "the invocation did not succeed — see the run log above for the full transcript.");
                if (result.DiscardedChanges)
                    Body(lines, "partial changes discarded — the branch is back at its base state.", TuiRole.Warning);
                else
                    Body(lines, "partial changes left in place.", TuiRole.Dim);
                break;

            case CheckpointSessionOutcome.NoChanges:
                Head(lines, "no changes", TuiRole.Error);
                Body(lines, "the invocation succeeded but produced no changes — nothing to review; treat as a failed attempt.");
                break;

            case CheckpointSessionOutcome.VerificationIncomplete:
                Head(lines, "verification incomplete", TuiRole.Error);
                AppendIncomplete(lines, result.Verification);
                Body(lines, "the Implementer's changes are untouched — the pending decision resumes on the next run.", TuiRole.Dim);
                break;

            case CheckpointSessionOutcome.Approved:
                Head(lines, "approved", TuiRole.Success);
                var sha = result.CommitSha is { Length: >= 12 } s ? s[..12] : result.CommitSha ?? "(unknown)";
                Body(lines, $"committed {sha} on {result.Preview.Branch}");
                Body(lines, result.NextStep!, TuiRole.Dim);
                break;

            case CheckpointSessionOutcome.Rejected:
                Head(lines, "rejected", TuiRole.Warning);
                Body(lines, result.DiscardedChanges
                    ? "changes discarded — the branch is back at its base state."
                    : "changes left in place — re-run to decide later.");
                Body(lines, result.NextStep!, TuiRole.Dim);
                break;

            case CheckpointSessionOutcome.Deferred:
                Head(lines, "deferred", TuiRole.Warning);
                // AutoDeferred is a CLI-lane-only flag (a human is always here in the TUI);
                // rendered for completeness in case a future path ever surfaces it.
                Body(lines, result.AutoDeferred
                    ? "auto-deferred — the decision is waiting; re-run from an interactive terminal (nothing is re-paid)."
                    : "re-run to pick this decision back up.");
                Body(lines, result.NextStep!, TuiRole.Dim);
                break;

            case CheckpointSessionOutcome.Interrupted:
                Head(lines, "interrupted", TuiRole.Warning);
                Body(lines, "re-run to resume; completed sub-steps are not re-paid.");
                break;

            default:
                Head(lines, result.Outcome.ToString().ToLowerInvariant(), TuiRole.Dim);
                break;
        }

        if (result.AttemptCostUsd is { } cost)
        {
            lines.Add(StyledLine.Empty);
            lines.Add(StyledLine.Of(
                new("attempt cost ", TuiRole.Faint),
                new("$" + cost.ToString("0.00", CultureInfo.InvariantCulture), TuiRole.Text)));
        }

        return lines;
    }

    /// <summary>The per-stop-reason incomplete message, mirroring the CONTENT of
    /// CheckpointRenderer.RenderVerificationFailure (the CLI's actionable, cause-specific
    /// lines) — always with the CLI's own words when present.</summary>
    private static void AppendIncomplete(List<StyledLine> lines, CheckpointVerificationOutcome? verification)
    {
        var role = verification?.FailureRole ?? "verification";
        var message = verification?.FailureStopReason switch
        {
            InvocationStopReason.RateLimited =>
                "rate-limited: the subscription window is exhausted — re-run later and verification resumes where it stopped.",
            InvocationStopReason.AuthFailed =>
                "authentication failed: this machine's claude login is missing or expired — run claude interactively to log in, then re-run.",
            InvocationStopReason.BudgetExceeded =>
                $"budget ceiling hit: the CLI killed the run crossing roles.{role}.maxBudgetUsd — raise it, then re-run.",
            InvocationStopReason.TimedOut =>
                $"timed out: the wall-clock ceiling killed the run — raise roles.{role}.timeoutSeconds, then re-run.",
            _ => "re-run to resume verification; completed sub-steps are not re-paid.",
        };
        Body(lines, message);

        if (verification?.FailureDetail is { } detail)
            Body(lines, $"claude/parser says: {detail}", TuiRole.Dim);
        if (verification?.FailureResultText is { } text)
            Body(lines, $"claude says: {text}", TuiRole.Dim);
    }

    private static void Head(List<StyledLine> lines, string word, TuiRole role) =>
        lines.Add(StyledLine.Of(new("● ", role), new(word, role)));

    private static void Body(List<StyledLine> lines, string text, TuiRole role = TuiRole.Text) =>
        lines.Add(StyledLine.Plain(text, role));
}
