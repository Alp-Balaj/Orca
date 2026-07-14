using System.ComponentModel;
using Orca.Cli.Tui;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Explicit TUI launch (<c>orca ui</c>) — a real Spectre command so <c>--repo</c>
/// and <c>--help</c> parse normally, for muscle memory and shells that misreport
/// interactivity. On a non-interactive terminal it refuses cleanly (one line, exit 1) via
/// <see cref="TuiEntry.Launch"/>, never an <c>Application.Init</c> crash. Bare <c>orca</c>
/// on a TTY opens the same TUI without this command, before Spectre dispatch (Program.cs).</summary>
public sealed class UiCommand : Command<UiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => TuiEntry.Launch(settings.Repo, ConsoleInteractivity.Detect());
}
