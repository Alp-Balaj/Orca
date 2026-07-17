using Orca.Cli;
using Orca.Cli.Commands;
using Orca.Cli.Tui;
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
    config.AddCommand<GoalCommand>("goal")
        .WithDescription("Refine a raw goal into a reviewable brief (one cheap Interviewer invocation); answer its open questions, then plan-generate reads it.");
    config.AddCommand<PlanGenerateCommand>("plan-generate")
        .WithDescription("Have the Planner write a draft plan into the vault from your goal brief (one scoped claude invocation, capped parse retries).");
    config.AddCommand<CheckpointRunCommand>("checkpoint-run")
        .WithDescription("Run one checkpoint (one scoped claude invocation), then stop for your approve/reject decision.");
    config.AddCommand<PlanApproveCommand>("plan-approve")
        .WithDescription("Approve a draft plan (status: draft → approved) via the surgical frontmatter editor — no source-mode YAML edit.");
    config.AddCommand<CheckpointCompleteCommand>("checkpoint-complete")
        .WithDescription("Mark a checkpoint complete (evidence-guarded) or re-open it with --reopen — no source-mode YAML edit.");
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
        return TuiEntry.RunWithOnboarding(repoPath: null);

    return await app.RunAsync(args);
}
catch (Exception e) when (ExitDecision.Describe(e) is { } message)
{
    // Fail loud, but with the reason and no stack noise — these are refusals and
    // misconfigurations, not bugs. ExitDecision is the pure decision of what counts as
    // one of those; anything else escapes this filter and crashes visibly. Plain
    // Console.Error (not AnsiConsole, which targets stdout and would color-code the line
    // based on the parent terminal's environment even when this stream is redirected) so
    // a script piping stdout never sees this, and stderr always reads byte-for-byte
    // "error: <message>".
    Console.Error.WriteLine($"error: {message}");
    return 1;
}
