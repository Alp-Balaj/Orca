using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Orca.Core.Vault;
using Orca.Core.Run;
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
        [Description("The goal brief, inline. Omit both --goal and --goal-file to use the vault's <plan-id>.goal.md (written by `orca goal`).")]
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

        [CommandOption("--force")]
        [Description("Plan from the vault goal note even though it still has unanswered open questions (SUG-08).")]
        public bool Force { get; init; }

        public override ValidationResult Validate()
        {
            if (Goal is not null && GoalFile is not null)
                return ValidationResult.Error("--goal and --goal-file are mutually exclusive");
            if (Strategy is not (null or "single" or "multi"))
                return ValidationResult.Error($"--strategy must be 'single' or 'multi', got '{Strategy}'");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var chosenStrategy = RouteStrategyToHuman(settings.Strategy, ConsoleInteractivity.Detect());
        if (chosenStrategy is null)
            return 1;

        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);

        // SUG-08: with neither --goal nor --goal-file, the vault's refined goal note is
        // the brief — and its open questions must be answered (or --force says the human
        // accepts planning over them).
        string goal;
        if (settings.Goal is null && settings.GoalFile is null)
        {
            var layout = new VaultLayout(config.VaultRoot, config.PlansDir);
            var goalPath = layout.GoalFile(settings.PlanId);
            if (!File.Exists(goalPath))
            {
                AnsiConsole.MarkupLine(
                    $"[red]no goal given and no goal note found[/] at {goalPath.EscapeMarkup()} — run " +
                    "[bold]orca goal[/] first, or pass --goal/--goal-file.");
                return 1;
            }

            var note = GoalNoteParser.Parse(File.ReadAllText(goalPath), goalPath);
            var questions = note.UnansweredQuestions;
            if (questions.Count > 0 && !settings.Force)
            {
                AnsiConsole.MarkupLine(
                    $"[red]the goal note still has {questions.Count} unanswered question(s)[/] — answer them in " +
                    $"{goalPath.EscapeMarkup()} (replace each '- [ ]' item with your answer), or pass --force to plan over them:");
                foreach (var question in questions)
                    AnsiConsole.MarkupLine($"  · {question.EscapeMarkup()}");
                return 1;
            }

            goal = note.Body;
        }
        else
        {
            goal = settings.Goal ?? ReadGoalFile(settings.GoalFile!);
        }
        var request = new PlanningRequest
        {
            PlanId = settings.PlanId,
            Goal = goal,
            Title = settings.Title,
            TargetRepoName = Path.GetFileName(workspace.RepoRoot),
            BaseBranch = settings.BaseBranch,
            NoScout = settings.NoScout,
        };

        // SUG-13: the spec's own precondition (a proven single-candidate + implementer
        // loop) is met, so multi is a real path now — still chosen by the human, never
        // defaulted.
        IPlanningStrategy strategy = chosenStrategy == "multi"
            ? new MultiCandidatePlanningStrategy(config, new ClaudeCodeExecutor(), workspace.RepoRoot)
            : new SingleCandidatePlanningStrategy(config, new ClaudeCodeExecutor(), workspace.RepoRoot);
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

        // SUG-03 stage B groundwork: same one-process-per-repo rule as checkpoint-run.
        using var _ = OrcaLock.Acquire(workspace.RepoRoot);
        var outcome = await strategy.GenerateAsync(preview);
        RenderOutcome(outcome);
        return outcome.State.Status == PlanningRunStatus.Succeeded ? 0 : 1;
    }

    /// <summary>Single vs multi is a human decision (spec §8). An explicit flag is that
    /// decision already made; otherwise ask. On a non-interactive terminal (§3.6) there
    /// is no one to ask and deliberation depth is never defaulted, so refuse up front —
    /// one line, nonzero exit, before any invocation and before any quota is spent —
    /// rather than crashing at the selection prompt. Returns "single"/"multi", or null
    /// when refused. Multi is real since SUG-13 — the spec's own precondition (a proven
    /// single-candidate + implementer loop) was met 2026-07-08.</summary>
    internal static string? RouteStrategyToHuman(string? strategyFlag, bool interactive)
    {
        if (strategyFlag is not null)
            return strategyFlag;

        if (!interactive)
        {
            AnsiConsole.MarkupLine(
                "[red]--strategy is required on a non-interactive terminal.[/] How much deliberation a plan deserves (single vs multi) is a human decision and is never defaulted (spec §8). Re-run with [bold]--strategy single[/] (or multi).");
            return null;
        }

        return AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("How much deliberation does this plan deserve? Your call — Orca never decides this.")
            .AddChoices(
                "single — one generation call (routine work following existing patterns)",
                "multi — N candidates + critic + synthesis (architecturally significant decisions; ~N+2× the planner cost)"))
            .StartsWith("multi", StringComparison.Ordinal) ? "multi" : "single";
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
                // BF-08: same honesty as the checkpoint lane — an unrecognized status is
                // a possible rate limit, not a certain exhaustion.
                var rateLimit = outcome.LastExecution.LastRateLimit;
                AnsiConsole.MarkupLine(
                    $"[red]possible rate limit[/] (status '{(rateLimit?.Status ?? "unknown").EscapeMarkup()}')" +
                    $"{(rateLimit?.ResetsAtText ?? "").EscapeMarkup()} — nothing is retried automatically; re-run " +
                    "plan-generate later and attempt numbering resumes. If quota wasn't the cause, the run log has the CLI's full story.");
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
