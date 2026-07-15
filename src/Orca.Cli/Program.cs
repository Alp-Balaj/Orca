using Orca.Cli;
using Orca.Cli.Commands;
using Orca.Cli.Tui;
using Orca.Core.Config;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Orca.Core.Run;
using Orca.Core.Vault;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("orca");
    config.PropagateExceptions();

    config.AddCommand<InitCommand>("init")
        .WithDescription("Scaffold a committed orca.json in this repo, then stop for you to review and commit it.");
    config.AddCommand<PlanInitCommand>("plan-init")
        .WithDescription("Create a plan directory with a schema-valid skeleton plan file in the vault.");
    config.AddCommand<PlanGenerateCommand>("plan-generate")
        .WithDescription("Have the Planner write a draft plan into the vault from your goal brief (one scoped claude invocation, capped parse retries).");
    config.AddCommand<CheckpointRunCommand>("checkpoint-run")
        .WithDescription("Run one checkpoint (one scoped claude invocation), then stop for your approve/reject decision.");
    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show the plan's checkpoints and the current run state.");
    config.AddCommand<UiCommand>("ui")
        .WithDescription("Open the full-screen, read-only status TUI (also opened by bare `orca` on an interactive terminal).");
});

try
{
    // Entry semantics (§3.6 / decision 3): bare `orca` on an interactive terminal opens the
    // TUI, checked BEFORE Spectre dispatch. On a non-interactive terminal bare `orca` falls
    // through to Spectre and prints today's help byte-for-byte (scripts see no difference).
    // The interactivity probe is step 3's ConsoleInteractivity — not a second TTY check.
    if (args.Length == 0 && ConsoleInteractivity.Detect())
        return TuiEntry.Run(repoPath: null);

    return await app.RunAsync(args);
}
catch (MissingRepoConfigException e)
{
    // The one refusal with a concrete next step: no orca.json here. Point the way
    // forward. Core stays free of CLI vocabulary — the command name lives only here.
    AnsiConsole.MarkupLine(
        $"[red]error:[/] {e.Message.EscapeMarkup()} — run 'orca init' to scaffold one.");
    return 1;
}
catch (Exception e) when (e is ConfigException or VaultException or GitException
    or ExecutorException or CheckpointRunException or PlanningException or ArgumentException)
{
    // Fail loud, but with the reason and no stack noise — these are refusals and
    // misconfigurations, not bugs. Anything else escapes and crashes visibly.
    AnsiConsole.MarkupLine($"[red]error:[/] {e.Message.EscapeMarkup()}");
    return 1;
}
