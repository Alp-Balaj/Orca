using Orca.Cli.Commands;
using Orca.Core.Config;
using Orca.Core.Git;
using Orca.Core.Run;
using Spectre.Console;
using Terminal.Gui.App;

// Terminal.Gui 2.4.17 marks the legacy static Application (Init/Run/Shutdown) [Obsolete]
// ("the legacy static Application object is going away"), but it ships and works, and it is
// the lifecycle counterpart to the Application.Invoke marshaling primitive decision 1
// approved (§1). Implemented as approved; the migration off the static Application is a
// separate owner decision flagged in the step-4 summary.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>The TUI's entry seam (§3.6): the interactivity gate, the Terminal.Gui
/// Application lifecycle (Init → Run → Shutdown), and the refuse-cleanly path. This is the
/// one place the Terminal.Gui lane is entered; once <see cref="Run"/> hands the terminal to
/// <c>Application</c>, the Spectre lane's <c>AnsiConsole</c> is never touched again until
/// shutdown (the lanes rule, §2).</summary>
internal static class TuiEntry
{
    /// <summary>Explicit launch (<c>orca ui</c>). On a non-interactive terminal it refuses
    /// cleanly — one line naming the subcommands, exit 1 — BEFORE any
    /// <c>Application.Init</c>, so a redirected/dumb terminal never hits a driver crash.</summary>
    public static int Launch(string? repoPath, bool interactive)
    {
        if (!interactive)
        {
            Console.Error.WriteLine(
                "orca ui needs an interactive terminal. Use the subcommands: plan-init, plan-generate, checkpoint-run, status.");
            return 1;
        }

        return RunWithOnboarding(repoPath);
    }

    /// <summary>The interactive entry both bare <c>orca</c> and <c>orca ui</c> route through on
    /// a TTY (each caller has already confirmed interactivity). Opens the full-screen TUI (runs checkpoints and generates plans — quota-spending, a human at every gate) over the
    /// repo's status; but if the repo has no committed <c>orca.json</c> — the one refusal with a
    /// concrete next step — it OFFERS to scaffold one right here (plan orca-init, checkpoint 4;
    /// owner decision 2026-07-15: both entry points offer) instead of failing loud through the
    /// Spectre lane. Every other refusal (non-git dir, unresolvable vault, bad config) still
    /// throws and surfaces through the CLI boundary's error handling.</summary>
    public static int RunWithOnboarding(string? repoPath)
    {
        try
        {
            return Run(repoPath);
        }
        catch (MissingRepoConfigException)
        {
            return OfferInit(repoPath, Confirm);
        }
    }

    /// <summary>No <c>orca.json</c> here and we're on a TTY: offer to scaffold one (default no).
    /// Decline is a clean choice — a one-line hint, exit 0 (owner decision 2026-07-15), not the
    /// red missing-config error. Accept runs the same <see cref="InitCommand.Scaffold"/> path,
    /// which writes the parse-checked file and STOPS with review-and-commit guidance; we do NOT
    /// open the TUI over the freshly written, unreviewed config (scaffold-then-stop, the whole
    /// feature's discipline). The confirm is injected so the branch is unit-testable without a
    /// console — mirroring the injected vault pick in <see cref="InitCommand"/>. This runs
    /// before <c>Application.Init</c>, so the Spectre lane is still live (the lanes rule holds).</summary>
    internal static int OfferInit(string? repoPath, Func<bool> confirm)
    {
        if (!confirm())
        {
            AnsiConsole.MarkupLine("No changes — run [bold]orca init[/] when you're ready.");
            return 0;
        }

        var workspace = GitWorkspace.Discover(repoPath ?? Directory.GetCurrentDirectory());
        return InitCommand.Scaffold(workspace, requestedVault: null, plansDir: null, interactive: true);
    }

    private static bool Confirm() =>
        AnsiConsole.Prompt(new ConfirmationPrompt("No orca.json here — scaffold one now?") { DefaultValue = false });

    /// <summary>Open the full-screen TUI (runs checkpoints and generates plans — quota-spending, a human at every gate) over the repo's current status. The caller has
    /// already confirmed the terminal is interactive (bare <c>orca</c> checks it before
    /// dispatch; <see cref="Launch"/> checks it for <c>orca ui</c>). The
    /// <see cref="StatusReport"/> is built before <c>Application.Init</c> — plain vault/git
    /// I/O, and any refusal (missing config, unresolvable vault) surfaces through the
    /// Spectre lane's normal error handling rather than from inside a running UI.</summary>
    public static int Run(string? repoPath)
    {
        var workspace = GitWorkspace.Discover(repoPath ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);

        StatusReport Build() => StatusReportBuilder.Build(config, workspace, planId: null);

        var report = Build();

        Application.Init();
        try
        {
            // SUG-18: the theme is the global config's call, applied once at startup.
            TuiTheme.Select(config.TuiTheme);
            // BF-17: every other screen disposes; the shell shouldn't be the exception.
            using var mainScreen = new MainScreen(report, Build, config, workspace);
            Application.Run(mainScreen);
        }
        finally
        {
            // Always restore the terminal, even if the run loop threw, so the Spectre lane
            // (and the shell) get a clean console back.
            Application.Shutdown();
        }

        return 0;
    }
}
