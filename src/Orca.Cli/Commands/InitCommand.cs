using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Git;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>First-run onboarding (plan orca-init, checkpoint 2). Scaffolds a committed
/// target-repo <c>orca.json</c> in a config-less repo, then STOPS for the human to review
/// and commit it — mirroring the <c>.orca/</c>-gitignore scaffold-then-stop precedent. It
/// never <c>git add</c>/<c>commit</c>s, and it never edits the machine-local global config:
/// the vault name is <em>read</em> from the global <c>vaults</c> map (point-and-stop). The
/// bytes it writes come straight from <see cref="OrcaConfigScaffold"/> (Core, parse-checked),
/// so the file it leaves behind is always one <see cref="ConfigLoader.Load"/> can read.</summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }

        [CommandOption("--vault <NAME>")]
        [Description("Vault name to record in orca.json; must be a key in the global config's vaults map. Defaults to the sole vault when exactly one is defined.")]
        public string? Vault { get; init; }

        [CommandOption("--plans-dir <REL>")]
        [Description("vault.plansDir value, relative to the vault root. Defaults to Orca/<RepoName>/Plans (whose folders orca init creates in the vault).")]
        public string? PlansDir { get; init; }

        [CommandOption("--force")]
        [Description("Overwrite an existing orca.json. On an interactive terminal this still confirms first.")]
        public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var interactive = ConsoleInteractivity.Detect();
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var repoFile = Path.Combine(workspace.RepoRoot, ConfigLoader.RepoFileName);

        switch (DecideExisting(File.Exists(repoFile), settings.Force, interactive))
        {
            case ExistingFileAction.Refuse:
                AnsiConsole.MarkupLine(
                    $"[red]error:[/] {ConfigLoader.RepoFileName} already exists at {repoFile.EscapeMarkup()} — re-run with [bold]--force[/] to overwrite it.");
                return 1;

            case ExistingFileAction.ConfirmThenWrite:
                if (!AnsiConsole.Prompt(new ConfirmationPrompt(
                        $"Overwrite the existing {ConfigLoader.RepoFileName} at {repoFile.EscapeMarkup()}?")
                    { DefaultValue = false }))
                {
                    AnsiConsole.MarkupLine("[yellow]left unchanged.[/]");
                    return 0;
                }

                break;

            case ExistingFileAction.Write:
                break;
        }

        return Scaffold(workspace, settings.Vault, settings.PlansDir, interactive);
    }

    /// <summary>The write-and-stop tail shared by <see cref="Execute"/> and the bare-<c>orca</c>
    /// / <c>orca ui</c> onboarding offer (plan orca-init, checkpoint 4 — see
    /// <see cref="Orca.Cli.Tui.TuiEntry"/>): resolve the vault against the global config
    /// (read-only), detect commands, resolve <c>vault.plansDir</c> to the canonical
    /// <c>Orca/&lt;repoName&gt;/Plans</c> — creating its folders in the vault unless <c>--plans-dir</c>
    /// named a location itself — write the parse-checked scaffold at the repo root, then STOP with
    /// the review-and-commit guidance. No <c>git add</c>/<c>commit</c>, no global-config edit; the
    /// vault-folder creation is the one write outside the repo, and it never throws (see
    /// <see cref="OrcaConfigScaffold.DetectPlansDir"/>). Don't-clobber is the caller's decision —
    /// reaching here means the write is meant to happen (the offer only fires when <c>orca.json</c>
    /// is absent).</summary>
    internal static int Scaffold(GitWorkspace workspace, string? requestedVault, string? plansDir, bool interactive)
    {
        var globalPath = ConfigLoader.GlobalConfigPath();
        var vaults = LoadGlobalVaults(globalPath);
        var vaultName = ResolveVaultName(
            vaults,
            requestedVault,
            interactive,
            globalPath,
            PickVaultInteractively);

        // Safe: ResolveVaultName only returns a name that is a key of a non-null, non-empty map.
        var vaultRoot = vaults![vaultName];
        var repoName = Path.GetFileName(workspace.RepoRoot);

        var (resolvedPlansDir, plansSummary) = ResolvePlansDir(plansDir, vaultRoot, repoName);

        var detected = OrcaConfigScaffold.DetectCommands(workspace.RepoRoot);
        var json = OrcaConfigScaffold.Build(new OrcaConfigScaffoldInput
        {
            VaultName = vaultName,
            RepoName = repoName,
            PlansDir = resolvedPlansDir,
            Commands = detected,
        });

        var repoFile = Path.Combine(workspace.RepoRoot, ConfigLoader.RepoFileName);
        File.WriteAllText(repoFile, json);

        AnsiConsole.MarkupLine($"[green]created[/] {repoFile.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"vault [bold]{vaultName.EscapeMarkup()}[/]; {detected.Summary.EscapeMarkup()}");
        AnsiConsole.MarkupLine(plansSummary.EscapeMarkup());
        AnsiConsole.MarkupLine(
            "Review [bold]commands[/] and [bold]vault.plansDir[/], then commit it (Orca never commits on its own) and run [bold]orca plan-generate[/].");
        return 0;
    }

    /// <summary>What to do about an <c>orca.json</c> that may already be there — the pure
    /// don't-clobber rule (plan constraint 6). Never overwrite without <c>--force</c>; with
    /// <c>--force</c>, confirm on an interactive terminal but write straight through on a
    /// non-interactive one (scriptable — <c>--force</c> is the intent already stated).</summary>
    internal static ExistingFileAction DecideExisting(bool exists, bool force, bool interactive)
    {
        if (!exists)
            return ExistingFileAction.Write;
        if (!force)
            return ExistingFileAction.Refuse;
        return interactive ? ExistingFileAction.ConfirmThenWrite : ExistingFileAction.Write;
    }

    /// <summary>Resolves which vault name to record in <c>orca.json</c> from the global
    /// config's <c>vaults</c> map — read-only; <c>init</c> never edits machine-local config
    /// (point-and-stop, owner decision 2026-07-15). Pure and testable: the interactive pick
    /// is injected. Throws <see cref="ConfigException"/> — caught at the CLI boundary and
    /// printed as one <c>error:</c> line with exit 1 — for every unresolvable case, so a
    /// non-interactive run never hides a prompt (plan constraint 5).</summary>
    internal static string ResolveVaultName(
        IReadOnlyDictionary<string, string>? vaults,
        string? requested,
        bool interactive,
        string globalPath,
        Func<IReadOnlyList<string>, string> pick)
    {
        var names = vaults is null
            ? []
            : vaults.Keys.Order(StringComparer.Ordinal).ToList();

        if (names.Count == 0)
            throw new ConfigException(
                $"no vaults are defined in the global config ({globalPath}) — add one first, e.g. \"vaults\": {{ \"<name>\": \"<absolute path on this machine>\" }}, then re-run orca init");

        if (requested is not null)
        {
            if (!vaults!.ContainsKey(requested))
                throw new ConfigException(
                    $"vault '{requested}' is not defined in the global config ({globalPath}) — known vaults: {string.Join(", ", names)}");
            return requested;
        }

        if (names.Count == 1)
            return names[0];

        if (!interactive)
            throw new ConfigException(
                $"the global config ({globalPath}) defines multiple vaults ({string.Join(", ", names)}) — pass --vault <name> to choose one on a non-interactive terminal");

        return pick(names);
    }

    private static string PickVaultInteractively(IReadOnlyList<string> names) =>
        AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Which vault should this repo use?")
            .AddChoices(names));

    /// <summary>Chooses the <c>vault.plansDir</c> to scaffold, and the line to echo. An explicit
    /// <c>--plans-dir</c> wins outright and is written straight through — no vault I/O, nothing
    /// created, because the human named their own location. Otherwise the canonical
    /// <c>Orca/&lt;repoName&gt;/Plans</c> is used and its parent folders are created in the vault when the
    /// root exists (owner decision 2026-07-15); that creation lives in
    /// <see cref="OrcaConfigScaffold.DetectPlansDir"/>, which never throws for an absent or unwritable
    /// vault, so even a non-interactive run still leaves a reviewable file (plan constraint 5).</summary>
    internal static (string PlansDir, string Summary) ResolvePlansDir(
        string? requested,
        string vaultRoot,
        string repoName)
    {
        if (requested is not null)
            return (requested, $"plansDir \"{requested}\" (from --plans-dir)");

        var prepared = OrcaConfigScaffold.DetectPlansDir(vaultRoot, repoName);
        return (prepared.PlansDir, prepared.Summary);
    }

    private static IReadOnlyDictionary<string, string>? LoadGlobalVaults(string globalPath) =>
        File.Exists(globalPath)
            ? ConfigDocumentParser.Parse(File.ReadAllText(globalPath), globalPath).Vaults
            : null;

    /// <summary>The three outcomes of the don't-clobber check — see <see cref="DecideExisting"/>.</summary>
    internal enum ExistingFileAction
    {
        Write,
        Refuse,
        ConfirmThenWrite,
    }
}
