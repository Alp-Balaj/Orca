using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Runs exactly one checkpoint — one Implementer invocation — then stops for the
/// human's approve/reject decision. Never loops over checkpoints (spec §6).</summary>
public sealed class CheckpointRunCommand : AsyncCommand<CheckpointRunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--plan <PLAN-ID>")]
        [Description("Plan to run. Optional when the vault holds exactly one plan.")]
        public string? Plan { get; init; }

        [CommandOption("--checkpoint <CHECKPOINT-ID>")]
        [Description("Checkpoint to run. Defaults to the plan's first pending checkpoint.")]
        public string? Checkpoint { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print the branch, prompt, and exact claude argv without mutating anything.")]
        public bool DryRun { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var executor = new ClaudeCodeExecutor();
        var runner = new CheckpointRunner(config, workspace, executor);
        var verifier = new CheckpointVerifier(config, workspace, executor);

        var preview = runner.Prepare(settings.Plan, settings.Checkpoint);
        RenderPreview(preview);

        if (settings.DryRun)
        {
            RenderDryRun(preview);
            return 0;
        }

        if (preview.PendingDecision)
            return await ResumePendingDecision(runner, verifier, preview);

        // .orca/ must be ignored before run logs exist: approval's `git add -A` must not
        // sweep them into history, and rejection's `git clean -fd` must not delete them.
        if (OrcaIgnore.EnsureIgnored(workspace.RepoRoot))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]added '{OrcaIgnore.Entry}' to .gitignore[/] — commit that change, then re-run (Orca never commits to your branch on its own).");
            return 1;
        }

        var outcome = await runner.RunAsync(preview);
        RenderExecution(outcome);

        if (outcome.Execution.Outcome == ExecutionOutcome.TimedOut || outcome.Execution.Result is not { IsError: false })
        {
            AnsiConsole.MarkupLine("[red]the invocation did not succeed[/] — see the run log above for the full transcript.");
            if (!workspace.IsClean())
            {
                RenderChanges(workspace);
                if (AnsiConsole.Prompt(new ConfirmationPrompt(
                        "Discard the partial changes it left behind (hard reset + clean)?")
                    { DefaultValue = false }))
                {
                    runner.Reject(discardChanges: true);
                    AnsiConsole.MarkupLine("[yellow]discarded[/]");
                }
            }
            return 1;
        }

        if (workspace.IsClean())
        {
            AnsiConsole.MarkupLine(
                "[red]the invocation succeeded but produced no changes[/] — nothing to review; treat as a failed attempt.");
            return 1;
        }

        // Step 7: the pipeline runs automatically after the Implementer — gates first
        // (machine facts), then Reconciler and semantic Verifier only if the gates pass.
        var verification = await verifier.RunAsync(preview.Plan, preview.Checkpoint, preview.Attempt);
        RenderVerification(verification);
        if (verification.Record is null)
        {
            RenderVerificationFailure(verification);
            return 1;
        }

        var attemptCost = (outcome.Execution.Result?.TotalCostUsd ?? 0m) + verification.CostUsd;
        return Decide(runner, preview, attemptCost);
    }

    /// <summary>The deferred-decision resume (step 5), now verification-aware (step 7): a
    /// pending attempt with no verification record is offered the pipeline first — gates
    /// re-run (cheap, deterministic) and only the sub-steps without records pay quota.</summary>
    private static async Task<int> ResumePendingDecision(
        CheckpointRunner runner, CheckpointVerifier verifier, CheckpointRunPreview preview)
    {
        var pendingAttempt = preview.Attempt - 1;
        AnsiConsole.MarkupLine(
            $"[yellow]attempt {pendingAttempt} is still awaiting your decision[/] — reviewing it now instead of launching a new invocation.");

        if (verifier.HasRecord(preview.Plan.PlanId, preview.Checkpoint.Id, pendingAttempt))
        {
            var existing = await verifier.RunAsync(preview.Plan, preview.Checkpoint, pendingAttempt);
            RenderVerification(existing);
        }
        else if (AnsiConsole.Prompt(new ConfirmationPrompt(
                     $"Attempt {pendingAttempt} has not been verified — run gates + reconcile + verify now?")))
        {
            var verification = await verifier.RunAsync(preview.Plan, preview.Checkpoint, pendingAttempt);
            RenderVerification(verification);
            if (verification.Record is null)
            {
                RenderVerificationFailure(verification);
                return 1;
            }
        }

        return Decide(runner, preview, attemptCostUsd: null);
    }

    /// <summary>The per-checkpoint human gate (spec §7.1). Approve commits; Reject offers
    /// — never performs — a hard reset; Defer leaves everything for a later run, which
    /// resumes this decision instead of re-invoking.</summary>
    private static int Decide(CheckpointRunner runner, CheckpointRunPreview preview, decimal? attemptCostUsd)
    {
        RenderChanges(GitWorkspace.Discover(preview.Options.WorkingDirectory));

        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Checkpoint [bold]{preview.Checkpoint.Id.EscapeMarkup()}[/] — your call:")
            .AddChoices("Approve — commit these changes", "Reject", "Defer — decide later"));

        if (choice.StartsWith("Approve", StringComparison.Ordinal))
        {
            var sha = runner.Approve(preview, attemptCostUsd);
            AnsiConsole.MarkupLine($"[green]committed[/] {sha[..12].EscapeMarkup()} on {preview.Branch.EscapeMarkup()}");
            AnsiConsole.MarkupLine("Review and merge the branch when ready; then mark the checkpoint complete in the plan.");
            return 0;
        }

        if (choice.StartsWith("Reject", StringComparison.Ordinal))
        {
            var discard = AnsiConsole.Prompt(new ConfirmationPrompt(
                    $"Hard-reset [bold]{preview.Branch.EscapeMarkup()}[/], discarding ALL uncommitted changes (including new files)?")
                { DefaultValue = false });
            runner.Reject(discard);
            AnsiConsole.MarkupLine(discard
                ? "[yellow]changes discarded[/] — the branch is back at its base state."
                : "[yellow]changes left in place[/] — re-run checkpoint-run to decide later.");
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]deferred[/] — re-run checkpoint-run to pick this decision back up.");
        return 0;
    }

    private static void RenderPreview(CheckpointRunPreview preview)
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

    private static void RenderDryRun(CheckpointRunPreview preview)
    {
        AnsiConsole.Write(new Panel(string.Join('\n', preview.Argv).EscapeMarkup())
            .Header("argv (one element per line, passed verbatim — dry run, nothing executed)"));
        if (preview.SettingsJson is not null)
            AnsiConsole.Write(new Panel(preview.SettingsJson.EscapeMarkup()).Header("--settings payload"));
        AnsiConsole.Write(new Panel(preview.Prompt.EscapeMarkup())
            .Header($"assembled prompt (sha256 {preview.PromptSha256[..12]})"));
    }

    private static void RenderExecution(CheckpointRunOutcome outcome)
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
            // logged-out machine, a budget kill all explain themselves here.
            if (r.IsError && r.ResultText is { } text)
                table.AddRow("claude says", text.EscapeMarkup());
            table.AddRow("turns / duration", $"{r.NumTurns} / {TimeSpan.FromMilliseconds(r.DurationMs)}");
            table.AddRow("cost", $"${r.TotalCostUsd}");
            table.AddRow("tokens in/out", $"{r.Usage.InputTokens}/{r.Usage.OutputTokens}");
        }
        table.AddRow("session", (e.SessionId ?? "(none)").EscapeMarkup());
        table.AddRow("accumulated cost", $"${outcome.RunState.AccumulatedCostUsd}");
        AnsiConsole.Write(new Panel(table).Header("invocation result"));
    }

    private static void RenderChanges(GitWorkspace workspace)
    {
        var status = workspace.StatusLines();
        AnsiConsole.Write(new Panel(string.Join('\n', status).EscapeMarkup())
            .Header($"working tree changes ({status.Count} paths)"));
        var stat = workspace.DiffStat();
        if (stat.Length > 0)
            AnsiConsole.Write(new Panel(stat.EscapeMarkup()).Header("diffstat (tracked files)"));
    }

    private static void RenderVerification(CheckpointVerificationOutcome verification)
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
    private static void RenderVerificationFailure(CheckpointVerificationOutcome verification)
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
