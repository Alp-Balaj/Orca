using System.ComponentModel;
using Orca.Core.Config;
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
        var runner = new CheckpointRunner(config, workspace, new ClaudeCodeExecutor());

        var preview = runner.Prepare(settings.Plan, settings.Checkpoint);
        RenderPreview(preview);

        if (settings.DryRun)
        {
            RenderDryRun(preview);
            return 0;
        }

        if (preview.PendingDecision)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]attempt {preview.Attempt - 1} is still awaiting your decision[/] — reviewing it now instead of launching a new invocation.");
            return Decide(runner, preview, attemptCostUsd: null);
        }

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

        return Decide(runner, preview, outcome.Execution.Result?.TotalCostUsd);
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
        table.AddRow("stop reason", e.StopReason == Orca.Core.Domain.InvocationStopReason.Succeeded
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
}
