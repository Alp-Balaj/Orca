using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Git;
using Orca.Core.Vault;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Creates a plan directory with a schema-valid skeleton plan file in the
/// configured vault.</summary>
public sealed class PlanInitCommand : Command<PlanInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<plan-id>")]
        [Description("Plan id (lowercase slug, e.g. recipe-tagging); also the folder and file name.")]
        public string PlanId { get; init; } = null!;

        [CommandOption("--title <TITLE>")]
        [Description("Human-readable plan title.")]
        public string? Title { get; init; }

        [CommandOption("--base-branch <BRANCH>")]
        [Description("Branch the plan executes against. Defaults to main.")]
        public string BaseBranch { get; init; } = "main";

        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Title)
                ? ValidationResult.Error("--title is required")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var layout = new VaultLayout(config.VaultRoot, config.PlansDir);

        var path = PlanScaffold.CreatePlan(
            layout,
            settings.PlanId,
            settings.Title!,
            Path.GetFileName(workspace.RepoRoot),
            settings.BaseBranch,
            DateTimeOffset.UtcNow);

        AnsiConsole.MarkupLine($"[green]created[/] {path.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "Fill in the checkpoints, set status to [bold]approved[/], then run [bold]orca checkpoint-run[/].");
        return 0;
    }
}
