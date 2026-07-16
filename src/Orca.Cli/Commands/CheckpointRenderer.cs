using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Run;
using Spectre.Console;

namespace Orca.Cli.Commands;

/// <summary>The checkpoint-run rendering, byte-for-byte as it was when it lived inside
/// CheckpointRunCommand — shared between the command's post-session output and the
/// Spectre gate's pre-prompt output, so the extraction of CheckpointRunSession changes
/// where rendering is triggered from, never what it prints.</summary>
internal static class CheckpointRenderer
{
    internal static void RenderPreview(CheckpointRunPreview preview)
    {
        var table = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");
        table.AddRow("plan", $"{preview.Plan.PlanId.EscapeMarkup()} v{preview.Plan.PlanVersion}");
        table.AddRow("checkpoint", $"{preview.Checkpoint.Id.EscapeMarkup()} — {preview.Checkpoint.Description.EscapeMarkup()}");
        table.AddRow("attempt", preview.Attempt.ToString());
        table.AddRow("branch", preview.Branch.EscapeMarkup() + (preview.BranchExists ? " (exists)" : $" (new, from {preview.Plan.BaseBranch.EscapeMarkup()})"));
        table.AddRow("model", (preview.Options.Model ?? "CLI default").EscapeMarkup());
        table.AddRow("allowed tools", string.Join(", ", preview.Options.AllowedTools).EscapeMarkup());
        table.AddRow("denied tools", preview.SettingsJson is null ? "(none configured)" : "via --settings permissions.deny");
        table.AddRow("max turns / timeout", $"{preview.Options.MaxTurns} / {preview.Options.Timeout}");
        table.AddRow("max budget", preview.Options.MaxBudgetUsd is { } b ? $"${b}" : "(none)");
        table.AddRow("run log", preview.LogPaths.EventLog.EscapeMarkup());
        AnsiConsole.Write(new Panel(table).Header("checkpoint run"));
    }

    internal static void RenderDryRun(CheckpointRunPreview preview)
    {
        AnsiConsole.Write(new Panel(string.Join('\n', preview.Argv).EscapeMarkup())
            .Header("argv (one element per line, passed verbatim — dry run, nothing executed)"));
        if (preview.SettingsJson is not null)
            AnsiConsole.Write(new Panel(preview.SettingsJson.EscapeMarkup()).Header("--settings payload"));
        AnsiConsole.Write(new Panel(preview.Prompt.EscapeMarkup())
            .Header($"assembled prompt (sha256 {preview.PromptSha256[..12]})"));
    }

    internal static void RenderExecution(CheckpointRunOutcome outcome)
    {
        var e = outcome.Execution;
        var table = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");
        table.AddRow("outcome", e.Outcome == ExecutionOutcome.TimedOut ? "[red]timed out (killed)[/]" : "completed");
        table.AddRow("stop reason", e.StopReason == InvocationStopReason.Succeeded
            ? "succeeded"
            : $"[red]{InvocationStopReasons.ToWire(e.StopReason).EscapeMarkup()}[/]");
        table.AddRow("exit code", e.ExitCode.ToString());
        if (e.Result is { } r)
        {
            table.AddRow("verdict", r.IsError ? $"[red]error ({r.Subtype.EscapeMarkup()})[/]" : "success");
            // On failure the CLI's own words beat any paraphrase — a wrong model id, a
            // logged-out machine, a budget kill all explain themselves here. On success
            // the same text is the Implementer's own summary of what it did (or exactly
            // why it stopped early on a blocked criterion) — the human deciding
            // approve/reject should see what the agent said, not have it discarded
            // (SUG-11 §3).
            if (r.ResultText is { } text)
                table.AddRow(r.IsError ? "claude says" : "summary", text.EscapeMarkup());
            table.AddRow("turns / duration", $"{r.NumTurns} / {TimeSpan.FromMilliseconds(r.DurationMs)}");
            table.AddRow("cost", $"${r.TotalCostUsd}");
            table.AddRow("tokens in/out", $"{r.Usage.InputTokens}/{r.Usage.OutputTokens}");
        }
        table.AddRow("session", (e.SessionId ?? "(none)").EscapeMarkup());
        table.AddRow("accumulated cost", $"${outcome.RunState.AccumulatedCostUsd}");
        AnsiConsole.Write(new Panel(table).Header("invocation result"));
    }

    internal static void RenderInvocationFailedLine() =>
        AnsiConsole.MarkupLine("[red]the invocation did not succeed[/] — see the run log above for the full transcript.");

    internal static void RenderChanges(IReadOnlyList<string> statusLines, string diffStat)
    {
        AnsiConsole.Write(new Panel(string.Join('\n', statusLines).EscapeMarkup())
            .Header($"working tree changes ({statusLines.Count} paths)"));
        if (diffStat.Length > 0)
            AnsiConsole.Write(new Panel(diffStat.EscapeMarkup()).Header("diffstat (tracked files)"));
    }

    internal static void RenderVerification(CheckpointVerificationOutcome verification)
    {
        var table = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");

        if (verification.RecordPreexisted)
            table.AddRow("source", "existing record (nothing re-run)");

        if (verification.BuildGate is { } build)
            table.AddRow("build gate", RenderGate(build));
        else if (verification.Record is { } r1)
            table.AddRow("build gate", r1.Gates.Build == GateOutcome.Pass ? "[green]pass[/]" : "[red]fail[/]");
        if (verification.TestsGate is { } tests)
            table.AddRow("tests gate", RenderGate(tests));
        else if (verification.Record is { } r2)
            table.AddRow("tests gate", r2.Gates.Tests == GateOutcome.Pass ? "[green]pass[/]" : "[red]fail[/]");

        if (verification.Reconciliation is { } reconciliation)
        {
            var verdictColor = reconciliation.ImplementsPlan == ReconciliationVerdict.Full ? "green" : "yellow";
            table.AddRow("reconciler", $"[{verdictColor}]implements_plan: {reconciliation.ImplementsPlan.ToString().ToLowerInvariant()}[/]");
            foreach (var item in reconciliation.Missing)
                table.AddRow("  missing", item.EscapeMarkup());
            foreach (var item in reconciliation.Extra)
                table.AddRow("  extra", item.EscapeMarkup());
        }

        if (verification.Record?.Semantic is { } semantic)
        {
            foreach (var item in semantic)
            {
                var mark = item.Verdict switch
                {
                    SemanticVerdict.Pass => "[green]pass[/]",
                    SemanticVerdict.Fail => "[red]fail[/]",
                    _ => "[yellow]unclear[/]",
                };
                var note = item.Note is null ? "" : $" — {item.Note.EscapeMarkup()}";
                table.AddRow("criterion", $"{mark} {item.Criterion.EscapeMarkup()}{note}");
            }
        }

        if (verification.Record is { } record)
        {
            table.AddRow("verdict", record.Verdict == VerificationVerdict.Pass
                ? "[green bold]pass[/]"
                : "[red bold]fail[/]");
            table.AddRow("record", verification.RecordPath!.EscapeMarkup());
        }

        if (verification.CostUsd > 0m)
            table.AddRow("verification cost", $"${verification.CostUsd}");

        AnsiConsole.Write(new Panel(table).Header("verification"));

        if (verification.Record is { Verdict: VerificationVerdict.Fail })
            AnsiConsole.MarkupLine(
                "[yellow]verification failed[/] — the findings are in the vault and will ride the next attempt's prompt (Reject below, then re-run checkpoint-run).");
    }

    private static string RenderGate(GateCommandResult gate)
    {
        var outcome = gate.Outcome == GateOutcome.Pass ? "[green]pass[/]" : "[red]fail[/]";
        var detail = gate.TimedOut
            ? $"killed after {gate.Elapsed.TotalSeconds:0.0}s — timeout"
            : $"exit {gate.ExitCode}, {gate.Elapsed.TotalSeconds:0.0}s";
        return $"{outcome} — `{string.Join(' ', gate.Command).EscapeMarkup()}` ({detail.EscapeMarkup()})";
    }

    /// <summary>The pipeline stopped before a record was written — distinct, actionable
    /// message per stop reason (the step-6 contract), always with the CLI's own words.</summary>
    internal static void RenderVerificationFailure(CheckpointVerificationOutcome verification)
    {
        var role = verification.FailureRole ?? "verification";
        switch (verification.FailureStopReason)
        {
            case InvocationStopReason.RateLimited:
                AnsiConsole.MarkupLine(
                    $"[red]rate-limited:[/] the subscription window is exhausted — nothing is retried automatically; re-run checkpoint-run later and verification resumes where it stopped.");
                break;
            case InvocationStopReason.AuthFailed:
                AnsiConsole.MarkupLine(
                    "[red]authentication failed:[/] this machine's claude login is missing or expired — run [bold]claude[/] interactively to log in, then re-run checkpoint-run to resume verification.");
                break;
            case InvocationStopReason.BudgetExceeded:
                AnsiConsole.MarkupLine(
                    $"[red]budget ceiling hit:[/] the CLI killed the run crossing roles.{role.EscapeMarkup()}.maxBudgetUsd — raise it, then re-run checkpoint-run to resume verification.");
                break;
            case InvocationStopReason.TimedOut:
                AnsiConsole.MarkupLine(
                    $"[red]timed out:[/] the wall-clock ceiling killed the run — raise roles.{role.EscapeMarkup()}.timeoutSeconds, then re-run checkpoint-run to resume verification.");
                break;
            default:
                AnsiConsole.MarkupLine(
                    "[red]verification incomplete[/] — re-run checkpoint-run to resume it; completed sub-steps are not re-paid.");
                break;
        }

        if (verification.FailureDetail is { } detail)
            AnsiConsole.MarkupLine($"claude/parser says: {detail.EscapeMarkup()}");
        if (verification.FailureResultText is { } text)
            AnsiConsole.MarkupLine($"claude says: {text.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "the Implementer's changes are untouched — the pending decision resumes on the next checkpoint-run.");
    }
}
