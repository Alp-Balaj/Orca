using Orca.Cli.Commands;
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

    config.AddCommand<PlanInitCommand>("plan-init")
        .WithDescription("Create a plan directory with a schema-valid skeleton plan file in the vault.");
    config.AddCommand<PlanGenerateCommand>("plan-generate")
        .WithDescription("Have the Planner write a draft plan into the vault from your goal brief (one scoped claude invocation, capped parse retries).");
    config.AddCommand<CheckpointRunCommand>("checkpoint-run")
        .WithDescription("Run one checkpoint (one scoped claude invocation), then stop for your approve/reject decision.");
    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show the plan's checkpoints and the current run state.");
});

try
{
    return await app.RunAsync(args);
}
catch (Exception e) when (e is ConfigException or VaultException or GitException
    or ExecutorException or CheckpointRunException or PlanningException or ArgumentException)
{
    // Fail loud, but with the reason and no stack noise — these are refusals and
    // misconfigurations, not bugs. Anything else escapes and crashes visibly.
    AnsiConsole.MarkupLine($"[red]error:[/] {e.Message.EscapeMarkup()}");
    return 1;
}
