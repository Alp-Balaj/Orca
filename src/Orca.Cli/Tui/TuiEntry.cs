using Orca.Core.Config;
using Orca.Core.Git;
using Orca.Core.Run;
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

        return Run(repoPath);
    }

    /// <summary>Open the read-only TUI over the repo's current status. The caller has
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
            Application.Run(new MainScreen(report, Build));
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
