using Orca.Cli.Commands;
using Orca.Core.Config;

namespace Orca.Tests;

/// <summary>Build-order for plan orca-init, checkpoint 2: the `orca init` command's pure,
/// terminal-free decision logic. The two branch points — the don't-clobber rule and vault
/// resolution — are extracted as static methods that take the interactivity result as a
/// bool and inject the interactive pick, so every branch (refuse-existing, --force on both
/// terminals, each vault-resolution path, the non-interactive refusals) is exercised
/// without a real console (same style as <see cref="NonInteractiveCliTests"/>). The actual
/// file write is covered by <see cref="OrcaConfigScaffoldTests"/>, which round-trips the
/// scaffold bytes back through <see cref="ConfigLoader.Load"/>.</summary>
public sealed class InitCommandTests
{
    private const string GlobalPath = "/global/config.json";

    // --- Don't-clobber: never overwrite without --force; --force confirms on a TTY only ---

    [Fact]
    public void A_missing_orca_json_is_written_regardless_of_force()
    {
        Assert.Equal(InitCommand.ExistingFileAction.Write,
            InitCommand.DecideExisting(exists: false, force: false, interactive: true));
        Assert.Equal(InitCommand.ExistingFileAction.Write,
            InitCommand.DecideExisting(exists: false, force: false, interactive: false));
    }

    [Fact]
    public void An_existing_orca_json_without_force_is_refused()
    {
        Assert.Equal(InitCommand.ExistingFileAction.Refuse,
            InitCommand.DecideExisting(exists: true, force: false, interactive: true));
        Assert.Equal(InitCommand.ExistingFileAction.Refuse,
            InitCommand.DecideExisting(exists: true, force: false, interactive: false));
    }

    [Fact]
    public void Force_over_an_existing_file_confirms_on_a_tty_but_writes_straight_through_when_non_interactive()
    {
        Assert.Equal(InitCommand.ExistingFileAction.ConfirmThenWrite,
            InitCommand.DecideExisting(exists: true, force: true, interactive: true));
        Assert.Equal(InitCommand.ExistingFileAction.Write,
            InitCommand.DecideExisting(exists: true, force: true, interactive: false));
    }

    // --- Vault resolution against the global config's vaults map (read-only) ---

    private static string ThrowIfPicked(IReadOnlyList<string> _) =>
        throw new InvalidOperationException("the interactive pick must not be reached");

    [Fact]
    public void No_vaults_defined_fails_loud_pointing_at_the_global_config()
    {
        var missing = Assert.Throws<ConfigException>(() =>
            InitCommand.ResolveVaultName(vaults: null, requested: null, interactive: true, GlobalPath, ThrowIfPicked));
        Assert.Contains("no vaults are defined", missing.Message);
        Assert.Contains(GlobalPath, missing.Message);

        // An empty map is the same as none — nothing to resolve to.
        Assert.Throws<ConfigException>(() => InitCommand.ResolveVaultName(
            new Dictionary<string, string>(), requested: null, interactive: true, GlobalPath, ThrowIfPicked));
    }

    [Fact]
    public void A_single_vault_is_the_default_without_a_flag_or_a_prompt()
    {
        var vaults = new Dictionary<string, string> { ["brian-the-brain"] = "/vaults/brian" };

        // Non-interactive too: exactly one leaves nothing to choose.
        Assert.Equal("brian-the-brain",
            InitCommand.ResolveVaultName(vaults, requested: null, interactive: false, GlobalPath, ThrowIfPicked));
    }

    [Fact]
    public void An_explicit_vault_flag_is_validated_against_the_map()
    {
        var vaults = new Dictionary<string, string> { ["a"] = "/a", ["b"] = "/b" };

        Assert.Equal("b",
            InitCommand.ResolveVaultName(vaults, requested: "b", interactive: false, GlobalPath, ThrowIfPicked));

        var unknown = Assert.Throws<ConfigException>(() =>
            InitCommand.ResolveVaultName(vaults, requested: "c", interactive: false, GlobalPath, ThrowIfPicked));
        Assert.Contains("vault 'c' is not defined", unknown.Message);
        Assert.Contains("a, b", unknown.Message); // known vaults listed, ordinal-ordered
    }

    [Fact]
    public void Multiple_vaults_without_a_flag_refuse_on_a_non_interactive_terminal()
    {
        var vaults = new Dictionary<string, string> { ["a"] = "/a", ["b"] = "/b" };

        var ex = Assert.Throws<ConfigException>(() =>
            InitCommand.ResolveVaultName(vaults, requested: null, interactive: false, GlobalPath, ThrowIfPicked));
        Assert.Contains("multiple vaults", ex.Message);
        Assert.Contains("--vault", ex.Message);
    }

    [Fact]
    public void Multiple_vaults_without_a_flag_prompt_to_pick_on_an_interactive_terminal()
    {
        var vaults = new Dictionary<string, string> { ["a"] = "/a", ["b"] = "/b" };
        var offered = new List<string>();

        var chosen = InitCommand.ResolveVaultName(vaults, requested: null, interactive: true, GlobalPath,
            names =>
            {
                offered.AddRange(names);
                return "b";
            });

        Assert.Equal("b", chosen);
        Assert.Equal(["a", "b"], offered); // the pick is offered the ordinal-ordered keys
    }
}
