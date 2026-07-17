using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Orca.Core.Run;
using Orca.Core.Vault;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>The goal-intake interview (SUG-08): one cheap vault-scoped Interviewer
/// invocation that sharpens a raw goal into <c>&lt;plan-dir&gt;/&lt;plan-id&gt;.goal.md</c> —
/// outcome, constraints, non-goals, checkpoint guidance, and open questions as Markdown
/// task items the human answers in Obsidian. plan-generate then consumes the note as its
/// goal brief and refuses while questions remain unanswered. Deliberately stateless
/// (unlike planning): this is a single small invocation — if it is cut off mid-run, the
/// §5.4 retry machinery has nothing durable to lose and re-running re-pays one cheap
/// call, so resume plumbing would cost more than it protects.</summary>
public sealed class GoalCommand : AsyncCommand<GoalCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<plan-id>")]
        [Description("Plan id the refined brief is for (lowercase slug); also the note's file name stem.")]
        public string PlanId { get; init; } = null!;

        [CommandOption("--goal <TEXT>")]
        [Description("The raw goal, inline. Exactly one of --goal / --goal-file is required.")]
        public string? Goal { get; init; }

        [CommandOption("--goal-file <PATH>")]
        [Description("File whose content is the raw goal, passed to the Interviewer verbatim.")]
        public string? GoalFile { get; init; }

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
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var layout = new VaultLayout(config.VaultRoot, config.PlansDir);

        var goalPath = layout.GoalFile(settings.PlanId);
        if (File.Exists(goalPath))
        {
            AnsiConsole.MarkupLine(
                $"[red]goal note already exists:[/] {goalPath.EscapeMarkup()} — edit it in Obsidian " +
                "(answer its open questions there), or delete it to re-interview from scratch.");
            return 1;
        }

        var role = config.GetRole(RoleName.Interviewer);
        var maxParseRetries = config.MaxParseRetries
            ?? throw new ConfigException(
                $"maxParseRetries is required to run the Interviewer (the §5.4 parse-retry ceiling) — set it in {config.RepoConfigPath} or {config.GlobalConfigPath}");

        var rawGoal = settings.Goal ?? File.ReadAllText(settings.GoalFile!);
        if (string.IsNullOrWhiteSpace(rawGoal))
        {
            AnsiConsole.MarkupLine("[red]the raw goal must be non-empty[/]");
            return 1;
        }

        var relativeGoalPath = $"{config.PlansDir}/{settings.PlanId}/{settings.PlanId}.goal.md";
        var timestamp = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss}Z";
        var prompt = InterviewerPrompt.AssembleInitial(settings.PlanId, rawGoal, relativeGoalPath, timestamp);
        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;
        var logLayout = new RunLogLayout(workspace.RepoRoot);

        var options = new ClaudeInvocationOptions
        {
            Prompt = prompt,
            WorkingDirectory = config.VaultRoot,
            AllowedTools = role.AllowedTools,
            MaxTurns = role.MaxTurns,
            Timeout = role.Timeout,
            Model = role.Model,
            MaxBudgetUsd = role.MaxBudgetUsd,
            SettingsFilePath = settingsJson is null ? null : logLayout.GoalSettingsFile(settings.PlanId, 1),
        };

        RenderPreview(settings.PlanId, goalPath, options, maxParseRetries);

        if (settings.DryRun)
        {
            AnsiConsole.Write(new Panel(string.Join('\n', ClaudeArguments.Build(options)).EscapeMarkup())
                .Header("argv (one element per line, passed verbatim — dry run, nothing executed)"));
            AnsiConsole.Write(new Panel(prompt.EscapeMarkup())
                .Header($"assembled prompt (sha256 {CheckpointPrompt.Sha256(prompt)[..12]})"));
            return 0;
        }

        if (OrcaIgnore.EnsureIgnored(workspace.RepoRoot))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]added '{OrcaIgnore.Entry}' to .gitignore[/] — commit that change, then re-run (Orca never commits to your branch on its own).");
            return 1;
        }

        var executor = new ClaudeCodeExecutor();
        var parseFailures = 0;
        var invocation = 1;
        while (true)
        {
            if (options.SettingsFilePath is { } settingsPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, settingsJson!);
            }

            var execution = await executor.ExecuteAsync(
                options, logLayout.GoalAttemptPaths(settings.PlanId, invocation));

            if (execution.StopReason != InvocationStopReason.Succeeded)
            {
                AnsiConsole.MarkupLine(
                    $"[red]the interview did not succeed[/] (stop reason: {InvocationStopReasons.ToWire(execution.StopReason).EscapeMarkup()})" +
                    (execution.Result?.ResultText is { } text ? $" — claude says: {text.EscapeMarkup()}" : "") +
                    " — nothing durable was lost; re-run orca goal.");
                return 1;
            }

            string? parseFailure = null;
            GoalNote? note = null;
            if (!File.Exists(goalPath))
            {
                parseFailure = $"the Interviewer wrote no file at {relativeGoalPath} (expected {goalPath})";
            }
            else
            {
                try
                {
                    note = GoalNoteParser.Parse(File.ReadAllText(goalPath), goalPath);
                }
                catch (VaultException e)
                {
                    parseFailure = e.Message;
                }
            }

            if (parseFailure is null)
            {
                AnsiConsole.MarkupLine($"[green]goal note written:[/] {goalPath.EscapeMarkup()}");
                var questions = note!.UnansweredQuestions;
                if (questions.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]{questions.Count} open question(s) need your answers[/] (in Obsidian, in the file):");
                    foreach (var question in questions)
                        AnsiConsole.MarkupLine($"  · {question.EscapeMarkup()}");
                    AnsiConsole.MarkupLine("then run [bold]orca plan-generate[/] with no --goal/--goal-file — it reads this note.");
                }
                else
                {
                    AnsiConsole.MarkupLine("no open questions — review the brief, then run [bold]orca plan-generate[/] with no --goal/--goal-file.");
                }

                return 0;
            }

            parseFailures++;
            if (parseFailures > maxParseRetries)
            {
                AnsiConsole.MarkupLine(
                    $"[red]the Interviewer's output failed parse/validation {parseFailures} time(s)[/], exhausting maxParseRetries={maxParseRetries}; last error: {parseFailure.EscapeMarkup()}");
                return 1;
            }

            invocation++;
            AnsiConsole.MarkupLine($"[yellow]output failed validation, retrying[/] ({parseFailure.EscapeMarkup()})");
            var retryPrompt = InterviewerPrompt.AssembleRetry(
                settings.PlanId, rawGoal, relativeGoalPath, timestamp, parseFailure);
            options = options with
            {
                Prompt = retryPrompt,
                SettingsFilePath = options.SettingsFilePath is null
                    ? null
                    : logLayout.GoalSettingsFile(settings.PlanId, invocation),
            };
        }
    }

    private static void RenderPreview(
        string planId, string goalPath, ClaudeInvocationOptions options, int maxParseRetries)
    {
        var table = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");
        table.AddRow("plan", planId.EscapeMarkup());
        table.AddRow("writes", goalPath.EscapeMarkup());
        table.AddRow("model", (options.Model ?? "CLI default").EscapeMarkup());
        table.AddRow("allowed tools", string.Join(", ", options.AllowedTools).EscapeMarkup());
        table.AddRow("max turns / timeout", $"{options.MaxTurns} / {options.Timeout}");
        table.AddRow("max budget", options.MaxBudgetUsd is { } b ? $"${b}" : "(none)");
        table.AddRow("parse retries", maxParseRetries.ToString());
        AnsiConsole.Write(new Panel(table).Header("goal interview"));
    }
}
