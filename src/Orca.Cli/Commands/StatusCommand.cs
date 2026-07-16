using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Shows the plan's checkpoint statuses and the run state — everything needed to
/// resume a run cold (spec §5.3). Since the §3.4 extraction this is a thin renderer over
/// the StatusReport query; resolution semantics (explicit --plan wins, otherwise exactly
/// one plan must exist) are unchanged.</summary>
public sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--plan <PLAN-ID>")]
        [Description("Plan to show. Optional when the vault holds exactly one plan.")]
        public string? Plan { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var layout = new VaultLayout(config.VaultRoot, config.PlansDir);

        // BF-09 §3: a silently skipped plan directory is exactly the cross-machine
        // divergence this command exists to expose — say it before anything else.
        foreach (var warning in PlanLocator.ListSkippedPlanDirectories(layout))
            AnsiConsole.MarkupLine($"[yellow]{warning.EscapeMarkup()}[/]");

        var planId = PlanLocator.ResolvePlanId(layout, settings.Plan);
        var report = StatusReportBuilder.Build(config, workspace, planId);
        var entry = report.Plans[0];
        var plan = entry.Plan;
        var state = entry.RunState;

        AnsiConsole.MarkupLine(
            $"[bold]{plan.Title.EscapeMarkup()}[/] — {plan.PlanId.EscapeMarkup()} v{plan.PlanVersion}, status [bold]{plan.Status}[/], base branch {plan.BaseBranch.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"repo {report.RepoRoot.EscapeMarkup()} on branch [bold]{report.CurrentBranch.EscapeMarkup()}[/]{(report.WorkingTreeClean ? "" : " [yellow](dirty)[/]")}");
        AnsiConsole.WriteLine();

        var table = new Table().AddColumns("", "checkpoint", "description", "status");
        foreach (var checkpoint in plan.Checkpoints)
        {
            var marker = state?.CheckpointId == checkpoint.Id ? "→" : "";
            var status = checkpoint.Status switch
            {
                CheckpointStatus.Complete => "[green]complete[/]",
                CheckpointStatus.Rejected => "[red]rejected[/]",
                CheckpointStatus.Blocked => "[red]blocked[/]",
                CheckpointStatus.InProgress => "[yellow]in_progress[/]",
                CheckpointStatus.AwaitingReview => "[yellow]awaiting_review[/]",
                _ => "pending",
            };
            table.AddRow(marker, checkpoint.Id.EscapeMarkup(), checkpoint.Description.EscapeMarkup(), status);
        }

        AnsiConsole.Write(table);

        if (state is null)
        {
            AnsiConsole.MarkupLine("no runs yet — [bold]orca checkpoint-run[/] starts the first pending checkpoint.");
            return 0;
        }

        var runTable = new Table().HideHeaders().Border(TableBorder.None).AddColumns("k", "v");
        runTable.AddRow("last checkpoint", $"{state.CheckpointId.EscapeMarkup()} attempt {state.Attempt} on {state.Branch.EscapeMarkup()}");

        if (entry.Evidence.TryGetValue(state.CheckpointId, out var evidence)
            && evidence.LatestVerification is { } latest)
        {
            var verdict = latest.Verdict == VerificationVerdict.Pass ? "[green]pass[/]" : "[red]fail[/]";
            runTable.AddRow("verification", $"{verdict} (attempt {latest.Attempt}, gates: build {SchemaYaml(latest.Gates.Build)}, tests {SchemaYaml(latest.Gates.Tests)})");
        }
        else
        {
            runTable.AddRow("verification", "(no record yet)");
        }

        runTable.AddRow("session", (state.SessionId ?? "(none recorded)").EscapeMarkup());
        runTable.AddRow("accumulated cost", $"${state.AccumulatedCostUsd}");
        if (state.LastUsage is { } usage)
            runTable.AddRow("last usage", $"in {usage.InputTokens}, out {usage.OutputTokens}, cache read {usage.CacheReadInputTokens}");
        runTable.AddRow("started / updated", $"{state.StartedAt:u} / {state.UpdatedAt:u}");
        AnsiConsole.Write(new Panel(runTable).Header("run state"));
        return 0;
    }

    private static string SchemaYaml(GateOutcome outcome) =>
        outcome == GateOutcome.Pass ? "pass" : "fail";
}
