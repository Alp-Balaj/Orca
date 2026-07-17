using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Planning;
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

        [CommandOption("--tokens")]
        [Description("Also show accumulated token usage by role — where the spend actually goes (SUG-06).")]
        public bool Tokens { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var layout = new VaultLayout(config.VaultRoot, config.PlansDir);

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

        // SUG-03 stage A: the ready set — what a human can safely start right now.
        var ready = CheckpointGraph.ReadySet(plan);
        if (ready.Count > 0)
            AnsiConsole.MarkupLine(
                $"runnable now: [bold]{string.Join(", ", ready.Select(c => c.Id)).EscapeMarkup()}[/]" +
                (ready.Count > 1 ? " — independent per depends_on; parallelize at will (two terminals, worktrees you manage)" : ""));
        else if (plan.Checkpoints.Any(c => c.Status == CheckpointStatus.Pending))
            AnsiConsole.MarkupLine(
                "[yellow]no checkpoint is ready[/] — every pending one waits on an incomplete dependency.");

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

        if (settings.Tokens)
            RenderTokens(state, new PlanningStateStore(workspace.RepoRoot).Read(planId));

        return 0;
    }

    /// <summary>SUG-06 §2: spend attribution — accumulated usage per role from run
    /// state, plus the planning-side roles from planning state. Cache-hit is
    /// cache-read / (input + cache-read): how much of what the model read was already
    /// cached rather than re-paid at full input rate.</summary>
    private static void RenderTokens(RunState state, PlanningState? planning)
    {
        var table = new Table().AddColumns("role", "input", "output", "cache write", "cache read", "cache hit");

        foreach (var (role, usage) in (state.UsageByRole ?? new Dictionary<string, TokenUsage>())
                 .OrderBy(p => p.Key, StringComparer.Ordinal))
            AddUsageRow(table, role, usage);

        // Planning-side roles accumulate from their attempt history.
        foreach (var group in (planning?.Attempts ?? [])
                 .Where(a => a.Usage is not null)
                 .GroupBy(a => a.Role, StringComparer.Ordinal)
                 .OrderBy(g => g.Key, StringComparer.Ordinal))
            AddUsageRow(table, group.Key, group.Aggregate(TokenUsage.Zero, (sum, a) => sum.Plus(a.Usage!)));

        if (table.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine("no per-role usage recorded yet — it accumulates from the next invocation on.");
            return;
        }

        AnsiConsole.Write(new Panel(table).Header("tokens by role (accumulated)"));
    }

    private static void AddUsageRow(Table table, string role, TokenUsage usage)
    {
        var readable = usage.InputTokens + usage.CacheReadInputTokens;
        var hit = readable == 0 ? "—" : $"{100.0 * usage.CacheReadInputTokens / readable:0}%";
        table.AddRow(
            role.EscapeMarkup(),
            usage.InputTokens.ToString("N0"),
            usage.OutputTokens.ToString("N0"),
            usage.CacheCreationInputTokens.ToString("N0"),
            usage.CacheReadInputTokens.ToString("N0"),
            hit);
    }

    private static string SchemaYaml(GateOutcome outcome) =>
        outcome == GateOutcome.Pass ? "pass" : "fail";
}
