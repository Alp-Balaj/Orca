using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Vault;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Flips a plan's <c>status: draft → approved</c> (or back with
/// <c>--revert</c>) through the surgical frontmatter editor — the CLI half of SUG-17,
/// replacing the source-mode Obsidian edit that risked the Properties-panel timestamp
/// trap (BF-09 §6). The human stays the decider: this is a deliberate command per flip,
/// never automatic, and the plan is validated through the real parser before anything is
/// written — a plan that doesn't parse is refused, not approved into a wedged state.</summary>
public sealed class PlanApproveCommand : Command<PlanApproveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[plan-id]")]
        [Description("Plan to approve. Optional when the vault holds exactly one plan.")]
        public string? Plan { get; init; }

        [CommandOption("--revert")]
        [Description("Un-approve: flip approved back to draft instead.")]
        public bool Revert { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var layout = new VaultLayout(config.VaultRoot, config.PlansDir);

        var planId = PlanLocator.ResolvePlanId(layout, settings.Plan);
        var plan = new VaultReader(layout).ReadPlan(planId);
        var (expected, target) = settings.Revert
            ? (PlanStatus.Approved, PlanStatus.Draft)
            : (PlanStatus.Draft, PlanStatus.Approved);

        if (plan.Status != expected)
        {
            AnsiConsole.MarkupLine(
                $"[red]plan '{planId.EscapeMarkup()}' has status '{SchemaEnums.ToYaml(plan.Status)}'[/] — " +
                (settings.Revert
                    ? "only an approved plan can be reverted to draft."
                    : "only a draft plan can be approved."));
            return 1;
        }

        var path = CheckpointStatusEditor.SetPlanStatus(
            layout.PlanFile(planId), expected, target, DateTimeOffset.UtcNow);
        AnsiConsole.MarkupLine(
            $"[green]{planId.EscapeMarkup()}[/]: {SchemaEnums.ToYaml(expected)} → " +
            $"{SchemaEnums.ToYaml(target)} in {path.EscapeMarkup()}");
        return 0;
    }
}
