using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Git;
using Orca.Core.Run;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>SUG-10: zero-cost preflight — sectioned pass/warn/fail with one action line
/// each, exit 0/1. Catches the expensive mistakes (version drift, sync artifacts,
/// unparseable plans, cross-machine divergence) before they cost an invocation.</summary>
public sealed class DoctorCommand : Command<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);

        var findings = new Doctor(config, workspace).Run();

        foreach (var section in findings.GroupBy(f => f.Section))
        {
            AnsiConsole.MarkupLine($"[bold]{section.Key.EscapeMarkup()}[/]");
            foreach (var finding in section)
            {
                var (glyph, colour) = finding.Level switch
                {
                    DoctorLevel.Pass => ("✓", "green"),
                    DoctorLevel.Info => ("·", "grey"),
                    DoctorLevel.Warn => ("!", "yellow"),
                    _ => ("✗", "red"),
                };
                AnsiConsole.MarkupLine($"  [{colour}]{glyph}[/] {finding.Message.EscapeMarkup()}");
                if (finding.Action is { } action)
                    AnsiConsole.MarkupLine($"    [grey]→ {action.EscapeMarkup()}[/]");
            }

            AnsiConsole.WriteLine();
        }

        if (Doctor.HasFailures(findings))
        {
            AnsiConsole.MarkupLine("[red]doctor found failures[/] — fix them before spending an invocation.");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]no failures[/] — warnings, if any, are listed above.");
        return 0;
    }
}
