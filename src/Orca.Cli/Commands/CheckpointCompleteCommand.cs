using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Git;
using Orca.Core.Run;
using Orca.Core.Vault;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Marks a checkpoint <c>complete</c> (or re-opens it with <c>--reopen</c>)
/// through the surgical frontmatter editor — the CLI half of SUG-17's checkpoint edit,
/// replacing the source-mode Obsidian edit. Guarded by evidence: without an Orca approval
/// commit at the checkpoint branch's head or a passing verification record, marking
/// complete requires an explicit <c>--anyway</c> — mirroring the TUI's "mark complete
/// anyway?" confirm.</summary>
public sealed class CheckpointCompleteCommand : Command<CheckpointCompleteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<checkpoint-id>")]
        [Description("Checkpoint to mark complete.")]
        public string Checkpoint { get; init; } = string.Empty;

        [CommandOption("--plan <PLAN-ID>")]
        [Description("Plan the checkpoint belongs to. Optional when the vault holds exactly one plan.")]
        public string? Plan { get; init; }

        [CommandOption("--reopen")]
        [Description("Flip a complete checkpoint back to pending instead. Attempt numbering continues from the vault's records — nothing is renumbered.")]
        public bool Reopen { get; init; }

        [CommandOption("--anyway")]
        [Description("Mark complete even though no approval commit or passing verification record was found.")]
        public bool Anyway { get; init; }

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
        var reader = new VaultReader(layout);
        var plan = reader.ReadPlan(planId);
        var checkpoint = plan.Checkpoints.FirstOrDefault(
            c => string.Equals(c.Id, settings.Checkpoint, StringComparison.Ordinal));
        if (checkpoint is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]plan '{planId.EscapeMarkup()}' has no checkpoint '{settings.Checkpoint.EscapeMarkup()}'[/]");
            return 1;
        }

        if (settings.Reopen)
        {
            if (checkpoint.Status != CheckpointStatus.Complete)
            {
                AnsiConsole.MarkupLine(
                    $"[red]checkpoint '{checkpoint.Id.EscapeMarkup()}' has status '{SchemaEnums.ToYaml(checkpoint.Status)}'[/] — only a complete checkpoint can be re-opened.");
                return 1;
            }

            var reopenedPath = CheckpointStatusEditor.SetStatus(
                layout.PlanFile(planId), checkpoint.Id,
                CheckpointStatus.Complete, CheckpointStatus.Pending, DateTimeOffset.UtcNow);
            AnsiConsole.MarkupLine(
                $"[green]{checkpoint.Id.EscapeMarkup()}[/]: complete → pending in {reopenedPath.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "[yellow]note[/] the vault's verification/reconciliation records for this checkpoint still exist — the next attempt number continues from that evidence (never reused).");
            return 0;
        }

        if (checkpoint.Status == CheckpointStatus.Complete)
        {
            AnsiConsole.MarkupLine(
                $"checkpoint '{checkpoint.Id.EscapeMarkup()}' is already complete — nothing to do (use --reopen to flip it back).");
            return 1;
        }

        var evidence = CompletionEvidence.Probe(workspace, reader, planId, checkpoint.Id);
        if (!evidence.Any && !settings.Anyway)
        {
            AnsiConsole.MarkupLine(
                $"[red]no completion evidence found for '{checkpoint.Id.EscapeMarkup()}'[/] — no Orca approval commit at " +
                $"'{evidence.Branch.EscapeMarkup()}' and no passing verification record. Pass --anyway to mark it complete regardless.");
            return 1;
        }

        var path = CheckpointStatusEditor.SetStatus(
            layout.PlanFile(planId), checkpoint.Id,
            checkpoint.Status, CheckpointStatus.Complete, DateTimeOffset.UtcNow);
        AnsiConsole.MarkupLine(
            $"[green]{checkpoint.Id.EscapeMarkup()}[/]: {SchemaEnums.ToYaml(checkpoint.Status)} → complete in {path.EscapeMarkup()}");
        if (evidence.Description is { } reason)
            AnsiConsole.MarkupLine($"[grey]evidence: {reason.EscapeMarkup()}[/]");
        else
            AnsiConsole.MarkupLine("[yellow]marked complete without evidence (--anyway)[/]");
        return 0;
    }
}
