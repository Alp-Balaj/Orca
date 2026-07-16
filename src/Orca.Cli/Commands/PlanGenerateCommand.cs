using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Runs the Planner: one scoped claude invocation (plus capped parse retries)
/// that writes a draft plan file into the vault for the human to review and approve.
/// How much deliberation a plan deserves — single vs multi candidate — is the human's
/// call, made here, never decided by code or model (spec §8).</summary>
public sealed class PlanGenerateCommand : AsyncCommand<PlanGenerateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<plan-id>")]
        [Description("Plan id (lowercase slug, e.g. recipe-tagging); also the folder and file name.")]
        public string PlanId { get; init; } = null!;

        [CommandOption("--goal <TEXT>")]
        [Description("The goal brief, inline. Exactly one of --goal / --goal-file is required.")]
        public string? Goal { get; init; }

        [CommandOption("--goal-file <PATH>")]
        [Description("File whose content is the goal brief, passed to the Planner verbatim.")]
        public string? GoalFile { get; init; }

        [CommandOption("--title <TITLE>")]
        [Description("Optional exact title; when omitted the Planner derives one from the goal.")]
        public string? Title { get; init; }

        [CommandOption("--base-branch <BRANCH>")]
        [Description("Branch the plan will execute against. Defaults to main.")]
        public string BaseBranch { get; init; } = "main";

        [CommandOption("--strategy <single|multi>")]
        [Description("Planning strategy. Omit to choose interactively; never auto-decided.")]
        public string? Strategy { get; init; }

        [CommandOption("--no-scout")]
        [Description("Skip the Scout stage even when roles.scout is configured — plan from the goal brief alone (SUG-01).")]
        public bool NoScout { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print the assembled prompt and exact claude argv without mutating anything.")]
        public bool DryRun { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }

        public override ValidationResult Validate()
        {
            if (Goal is null == (GoalFile is null))
                return ValidationResult.Error("exactly one of --goal or --goal-file is required");
            if (Strategy is not (null or "single" or "multi"))
                return ValidationResult.Error($"--strategy must be 'single' or 'multi', got '{Strategy}'");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!RouteStrategyToHuman(settings.Strategy, ConsoleInteractivity.Detect()))
            return 1;

        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);

        var goal = settings.Goal ?? ReadGoalFile(settings.GoalFile!);
        var request = new PlanningRequest
        {
            PlanId = settings.PlanId,
            Goal = goal,
            Title = settings.Title,
            TargetRepoName = Path.GetFileName(workspace.RepoRoot),
            BaseBranch = settings.BaseBranch,
            NoScout = settings.NoScout,
        };

        var strategy = new SingleCandidatePlanningStrategy(config, new ClaudeCodeExecutor(), workspace.RepoRoot);
        var preview = strategy.Prepare(request);
        RenderPreview(preview, config);

        if (settings.DryRun)
        {
            RenderDryRun(preview);
            return 0;
        }

        // Planning writes run logs and state under .orca/ — it must be ignored before
        // they exist, for the same reasons checkpoint-run enforces this.
        if (OrcaIgnore.EnsureIgnored(workspace.RepoRoot))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]added '{OrcaIgnore.Entry}' to .gitignore[/] — commit that change, then re-run (Orca never commits to your branch on its own).");
            return 1;
        }

        var outcome = await strategy.GenerateAsync(preview);
        RenderOutcome(outcome);
        return outcome.State.Status == PlanningRunStatus.Succeeded ? 0 : 1;
    }

    /// <summary>Single vs multi is a human decision (spec §8). An explicit flag is that
    /// decision already made; otherwise ask. On a non-interactive terminal (§3.6) there is
    /// no one to ask and deliberation depth is never defaulted, so refuse up front — one
    /// line, nonzero exit, before any invocation and before any quota is spent — rather
    /// than crashing at the selection prompt. Multi-candidate is deferred until the
    /// implementer loop is proven end to end — choosing it refuses clearly rather than
    /// silently downgrading.</summary>
    internal static bool RouteStrategyToHuman(string? strategyFlag, bool interactive)
    {
        string choice;
        if (strategyFlag is not null)
        {
            choice = strategyFlag;
        }
        else if (!interactive)
        {
            AnsiConsole.MarkupLine(
                "[red]--strategy is required on a non-interactive terminal.[/] How much deliberation a plan deserves (single vs multi) is a human decision and is never defaulted (spec §8). Re-run with [bold]--strategy single[/].");
            return false;
        }
        else
        {
            choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("How much deliberation does this plan deserve? Your call — Orca never decides this.")
                .AddChoices(
                    "single — one generation call (routine work following existing patterns)",
                    "multi — N candidates + critic (architecturally significant; not built yet)"))
                .StartsWith("multi", StringComparison.Ordinal) ? "multi" : "single";
        }

        if (choice == "multi")
        {
            AnsiConsole.MarkupLine(
                "[red]multi-candidate planning is deferred[/] until the single-candidate path and the implementer loop are proven end to end (plan orca-build, checkpoint 06 acceptance criteria). Re-run with [bold]--strategy single[/], or wait for checkpoint 08 to land.");
            return false;
        }

        return true;
    }

    private static string ReadGoalFile(string path)
    {
        if (!File.Exists(path))
            throw new PlanningException($"goal file does not exist: {path}");
        return File.ReadAllText(path);
    }

    private static void RenderPreview(PlanningPreview preview, OrcaConfig config)
    {
        var table = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");
        table.AddRow("plan", preview.Request.PlanId.EscapeMarkup());
        table.AddRow("strategy", "single_candidate");
        // SUG-01: the Scout stage is part of what the human authorizes — show it.
        if (preview.Scout is { } scout)
            table.AddRow("scout", $"runs first (attempt {scout.Attempt}, model {scout.Options.Model ?? "CLI default"}) — audit to {scout.AuditFilePath}".EscapeMarkup());
        else
            table.AddRow("scout", (preview.ScoutSkipReason ?? "(skipped)").EscapeMarkup());
        table.AddRow("attempt", preview.Attempt.ToString());
        table.AddRow("writes into", Path.Combine(config.VaultRoot, config.PlansDir).EscapeMarkup());
        table.AddRow("plan file", preview.RelativePlanPath.EscapeMarkup());
        table.AddRow("model", (preview.Options.Model ?? "CLI default").EscapeMarkup());
        table.AddRow("allowed tools", string.Join(", ", preview.Options.AllowedTools).EscapeMarkup());
        table.AddRow("denied tools", preview.SettingsJson is null ? "(none configured)" : "via --settings permissions.deny");
        table.AddRow("max turns / timeout", $"{preview.Options.MaxTurns} / {preview.Options.Timeout}");
        table.AddRow("max budget", preview.Options.MaxBudgetUsd is { } b ? $"${b}" : "(none)");
        table.AddRow("parse retries", preview.MaxParseRetries.ToString());
        table.AddRow("run log", preview.LogPaths.EventLog.EscapeMarkup());
        AnsiConsole.Write(new Panel(table).Header("plan generation"));
    }

    private static void RenderDryRun(PlanningPreview preview)
    {
        if (preview.Scout is { } scout)
        {
            AnsiConsole.Write(new Panel(string.Join('\n', scout.Argv).EscapeMarkup())
                .Header("scout argv (runs first; the Planner prompt below gains its audit)"));
            AnsiConsole.Write(new Panel(scout.Prompt.EscapeMarkup())
                .Header($"scout prompt (sha256 {scout.PromptSha256[..12]})"));
        }

        AnsiConsole.Write(new Panel(string.Join('\n', preview.Argv).EscapeMarkup())
            .Header("argv (one element per line, passed verbatim — dry run, nothing executed)"));
        if (preview.SettingsJson is not null)
            AnsiConsole.Write(new Panel(preview.SettingsJson.EscapeMarkup()).Header("--settings payload"));
        AnsiConsole.Write(new Panel(preview.Prompt.EscapeMarkup())
            .Header($"assembled prompt (sha256 {preview.PromptSha256[..12]})"));
    }

    private static void RenderOutcome(PlanningOutcome outcome)
    {
        var last = outcome.State.Attempts[^1];
        var table = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");
        table.AddRow("attempts", outcome.State.Attempts.Count.ToString());
        table.AddRow("stop reason", InvocationStopReasons.ToWire(last.StopReason).EscapeMarkup());
        table.AddRow("session", (last.SessionId ?? "(none)").EscapeMarkup());
        table.AddRow("cost (run total)", $"${outcome.State.AccumulatedCostUsd}");
        AnsiConsole.Write(new Panel(table).Header("planning result"));

        if (outcome.State.Status == PlanningRunStatus.Succeeded)
        {
            AnsiConsole.MarkupLine($"[green]draft plan written:[/] {outcome.PlanFilePath.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "Review it, set status to [bold]approved[/] when it earns it, then run [bold]orca checkpoint-run[/].");
            return;
        }

        // Distinct, actionable failure surfacing per stop reason (the approved
        // rate-limit/auth-expiry contract) — and always the CLI's own words when it
        // offered any.
        switch (last.StopReason)
        {
            case InvocationStopReason.RateLimited:
                var resets = outcome.LastExecution.LastRateLimit?.ResetsAt is { } ts
                    ? $" (window resets {DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime():yyyy-MM-dd HH:mm zzz})"
                    : "";
                AnsiConsole.MarkupLine(
                    $"[red]rate-limited:[/] the subscription window is exhausted{resets.EscapeMarkup()} — nothing is retried automatically; re-run plan-generate later and attempt numbering resumes.");
                break;
            case InvocationStopReason.AuthFailed:
                AnsiConsole.MarkupLine(
                    "[red]authentication failed:[/] this machine's claude login is missing or expired — run [bold]claude[/] interactively to log in, then re-run.");
                break;
            case InvocationStopReason.BudgetExceeded:
                AnsiConsole.MarkupLine(
                    "[red]budget ceiling hit:[/] the CLI killed the run crossing roles.planner.maxBudgetUsd — raise it (or simplify the goal), then re-run.");
                break;
            case InvocationStopReason.TimedOut:
                AnsiConsole.MarkupLine(
                    "[red]timed out:[/] the wall-clock ceiling killed the run — raise roles.planner.timeoutSeconds or simplify the goal, then re-run.");
                break;
            default:
                AnsiConsole.MarkupLine("[red]the planning invocation did not produce a valid plan.[/]");
                break;
        }

        if (outcome.FailureDetail is { } detail)
            AnsiConsole.MarkupLine($"claude/parser says: {detail.EscapeMarkup()}");
        if (last.ParseFailure is not null && File.Exists(outcome.PlanFilePath))
            AnsiConsole.MarkupLine(
                $"the rejected file is left at {outcome.PlanFilePath.EscapeMarkup()} for inspection.");
        AnsiConsole.MarkupLine($"full transcript: {outcome.State.Attempts.Count} attempt log(s) under .orca/runs/{outcome.State.PlanId.EscapeMarkup()}/");
    }
}
