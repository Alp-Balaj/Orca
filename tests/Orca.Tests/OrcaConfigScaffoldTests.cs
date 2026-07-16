using Orca.Core.Config;

namespace Orca.Tests;

public sealed class OrcaConfigScaffoldTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-scaffold-tests-" + Guid.NewGuid().ToString("N"));

    public OrcaConfigScaffoldTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // The acceptance gate for the whole checkpoint: the scaffold is only correct if the thing it
    // writes is a thing Orca can load. Write the scaffold as a real orca.json, point a fake global
    // config at a vault, and run the full ConfigLoader.Load — which parses strictly, merges, and
    // resolves the vault name — then require every role to resolve too.
    private OrcaConfig LoadScaffold(DetectedCommands commands, string? plansDir = null)
    {
        var repoDir = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDir);

        var json = OrcaConfigScaffold.Build(new OrcaConfigScaffoldInput
        {
            VaultName = "brian-the-brain",
            RepoName = "MyApp",
            PlansDir = plansDir,
            Commands = commands,
        });

        // Independent of Load: the scaffold's output must satisfy the strict parser directly.
        ConfigDocumentParser.Parse(json, "scaffold");

        File.WriteAllText(Path.Combine(repoDir, ConfigLoader.RepoFileName), json);

        var globalPath = Path.Combine(repoDir, "global.json");
        File.WriteAllText(globalPath, $$"""
            {
              "schemaVersion": 1,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} }
            }
            """);

        return ConfigLoader.Load(repoDir, globalPath);
    }

    private static void AssertAllFourRolesResolve(OrcaConfig config)
    {
        foreach (var role in Enum.GetValues<RoleName>())
        {
            var resolved = config.GetRole(role);
            Assert.NotEmpty(resolved.AllowedTools);
            Assert.True(resolved.MaxTurns >= 1);
            Assert.True(resolved.Timeout > TimeSpan.Zero);
        }
    }

    private string MakeRepoWith(params string[] fileNames)
    {
        var dir = Path.Combine(_root, "detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(dir, name), "");
        return dir;
    }

    [Fact]
    public void Scaffold_output_loads_back_as_a_valid_config_with_all_roles()
    {
        var config = LoadScaffold(new DetectedCommands
        {
            Build = ["dotnet", "build", "MyApp.slnx"],
            Test = ["dotnet", "test", "MyApp.slnx"],
            Summary = "test",
            IsPlaceholder = false,
        });

        Assert.Equal("brian-the-brain", config.VaultName);
        Assert.Equal("MyApp/Plans", config.PlansDir);
        Assert.Equal(["dotnet", "build", "MyApp.slnx"], config.BuildCommand);
        Assert.Equal(["dotnet", "test", "MyApp.slnx"], config.TestCommand);
        Assert.Equal(2, config.MaxParseRetries);
        Assert.Equal(3, config.MaxCheckpointAttempts);
        Assert.Equal(TimeSpan.FromSeconds(600), config.GateTimeout);
        AssertAllFourRolesResolve(config);
    }

    [Fact]
    public void Scaffold_ships_the_opinionated_model_tier_by_default()
    {
        // SUG-05: the manual pattern this scaffold replicates is "higher model plans,
        // Sonnet/Haiku implements" — a fresh orca.json must not need a human to remember
        // to configure that tiering.
        var config = LoadScaffold(new DetectedCommands
        {
            Build = ["dotnet", "build", "MyApp.slnx"],
            Test = ["dotnet", "test", "MyApp.slnx"],
            Summary = "test",
            IsPlaceholder = false,
        });

        Assert.Equal("opus", config.GetRole(RoleName.Planner).Model);
        Assert.Equal("sonnet", config.GetRole(RoleName.Implementer).Model);
        Assert.Equal("haiku", config.GetRole(RoleName.Reconciler).Model);
        Assert.Equal("sonnet", config.GetRole(RoleName.Verifier).Model);
    }

    [Fact]
    public void Placeholder_commands_still_produce_a_loadable_config()
    {
        // The REPLACE_ME fallback must load (so the scaffold-then-review file appears); it is the
        // gate run, not config load, that fails loud on it later.
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("README.md"));
        Assert.True(detected.IsPlaceholder);
        Assert.Equal([OrcaConfigScaffold.PlaceholderToken], detected.Build);

        var config = LoadScaffold(detected);
        Assert.Equal([OrcaConfigScaffold.PlaceholderToken], config.BuildCommand);
        Assert.Equal([OrcaConfigScaffold.PlaceholderToken], config.TestCommand);
        AssertAllFourRolesResolve(config);
    }

    [Fact]
    public void Detects_slnx_and_the_scaffold_round_trips()
    {
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("MyApp.slnx"));
        Assert.False(detected.IsPlaceholder);
        Assert.Equal(["dotnet", "build", "MyApp.slnx"], detected.Build);
        Assert.Equal(["dotnet", "test", "MyApp.slnx"], detected.Test);

        var config = LoadScaffold(detected);
        Assert.Equal(["dotnet", "build", "MyApp.slnx"], config.BuildCommand);
        AssertAllFourRolesResolve(config);
    }

    [Fact]
    public void Detects_sln_and_the_scaffold_round_trips()
    {
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("Legacy.sln"));
        Assert.False(detected.IsPlaceholder);
        Assert.Equal(["dotnet", "build", "Legacy.sln"], detected.Build);

        AssertAllFourRolesResolve(LoadScaffold(detected));
    }

    [Fact]
    public void Detects_a_single_csproj_and_the_scaffold_round_trips()
    {
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("Tool.csproj"));
        Assert.False(detected.IsPlaceholder);
        Assert.Equal(["dotnet", "build", "Tool.csproj"], detected.Build);
        Assert.Equal(["dotnet", "test", "Tool.csproj"], detected.Test);

        AssertAllFourRolesResolve(LoadScaffold(detected));
    }

    [Fact]
    public void Detects_package_json_and_the_scaffold_round_trips()
    {
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("package.json"));
        Assert.False(detected.IsPlaceholder);
        Assert.Equal(["npm", "run", "build"], detected.Build);
        Assert.Equal(["npm", "test"], detected.Test);

        AssertAllFourRolesResolve(LoadScaffold(detected));
    }

    [Fact]
    public void A_sln_pattern_does_not_swallow_a_slnx_file()
    {
        // Windows quirk guard: a 3-char search pattern extension (*.sln) matches *.slnx via the
        // Win32 short-extension rule. Detection must classify a lone .slnx as slnx, not sln.
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("Solo.slnx"));
        Assert.Equal("dotnet", detected.Build[0]);
        Assert.Equal("Solo.slnx", detected.Build[2]);
    }

    [Fact]
    public void Solution_file_wins_over_a_csproj_in_the_same_directory()
    {
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("MyApp.slnx", "MyApp.csproj"));
        Assert.Equal(["dotnet", "build", "MyApp.slnx"], detected.Build);
    }

    [Fact]
    public void Multiple_csproj_are_ambiguous_and_fall_through_to_the_placeholder()
    {
        var detected = OrcaConfigScaffold.DetectCommands(MakeRepoWith("A.csproj", "B.csproj"));
        Assert.True(detected.IsPlaceholder);
    }

    [Fact]
    public void PlansDir_defaults_to_RepoName_Plans_but_an_override_is_honored()
    {
        var placeholder = OrcaConfigScaffold.DetectCommands(MakeRepoWith("README.md"));
        Assert.Equal("MyApp/Plans", LoadScaffold(placeholder).PlansDir);
        Assert.Equal("custom/here", LoadScaffold(placeholder, plansDir: "custom/here").PlansDir);
    }

    // --- Canonical plansDir preparation (owner decision 2026-07-15: Orca/<repoName>/Plans, no scan) ---

    /// <summary>Creates <paramref name="relativeDirs"/> under a fresh vault root and returns it.</summary>
    private string MakeVaultWith(params string[] relativeDirs)
    {
        var vault = Path.Combine(_root, "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vault);
        foreach (var rel in relativeDirs)
            Directory.CreateDirectory(Path.Combine(vault, rel.Replace('/', Path.DirectorySeparatorChar)));
        return vault;
    }

    [Fact]
    public void PlansDir_is_always_Orca_repoName_Plans()
    {
        // A folder elsewhere in the vault that happens to share the repo's name is irrelevant now —
        // there is exactly one canonical location, never a scan.
        var vault = MakeVaultWith("7 - Dev Folder/Projects/MyApp", "Notes");

        var prepared = OrcaConfigScaffold.DetectPlansDir(vault, "MyApp");

        Assert.Equal("Orca/MyApp/Plans", prepared.PlansDir); // forward slashes, fixed shape
    }

    [Fact]
    public void PlansDir_preparation_creates_the_full_folder_structure()
    {
        var vault = MakeVaultWith();

        OrcaConfigScaffold.DetectPlansDir(vault, "MyApp");

        Assert.True(Directory.Exists(Path.Combine(vault, "Orca")));
        Assert.True(Directory.Exists(Path.Combine(vault, "Orca", "MyApp")));
        Assert.True(Directory.Exists(Path.Combine(vault, "Orca", "MyApp", "Plans")));
    }

    [Fact]
    public void PlansDir_preparation_is_idempotent_when_the_folders_already_exist()
    {
        var vault = MakeVaultWith("Orca/MyApp");

        var first = OrcaConfigScaffold.DetectPlansDir(vault, "MyApp");
        var second = OrcaConfigScaffold.DetectPlansDir(vault, "MyApp");

        Assert.Equal("Orca/MyApp/Plans", first.PlansDir);
        Assert.Equal("Orca/MyApp/Plans", second.PlansDir);
        Assert.True(Directory.Exists(Path.Combine(vault, "Orca", "MyApp")));
    }

    [Fact]
    public void PlansDir_preparation_creates_nothing_when_the_vault_root_is_absent()
    {
        var absent = Path.Combine(_root, "does-not-exist");

        var prepared = OrcaConfigScaffold.DetectPlansDir(absent, "MyApp");

        Assert.Equal("Orca/MyApp/Plans", prepared.PlansDir);
        Assert.False(Directory.Exists(absent)); // never conjures the vault root into being
    }

    [Fact]
    public void A_prepared_plansDir_round_trips_through_the_scaffold_and_config_load()
    {
        var vault = MakeVaultWith();
        var prepared = OrcaConfigScaffold.DetectPlansDir(vault, "MyApp");

        var placeholder = OrcaConfigScaffold.DetectCommands(MakeRepoWith("README.md"));
        Assert.Equal(
            "Orca/MyApp/Plans",
            LoadScaffold(placeholder, plansDir: prepared.PlansDir).PlansDir);
    }

    [Fact]
    public void Output_is_two_space_indented_and_carries_no_vaults_block()
    {
        var json = OrcaConfigScaffold.Build(new OrcaConfigScaffoldInput
        {
            VaultName = "brian-the-brain",
            RepoName = "MyApp",
            Commands = OrcaConfigScaffold.DetectCommands(MakeRepoWith("MyApp.slnx")),
        });

        // A committed orca.json must never carry machine paths — the scaffold names the vault only.
        Assert.DoesNotContain("\"vaults\"", json);
        Assert.Contains("\n  \"vault\": {", json);
        Assert.Contains("\n    \"name\": \"brian-the-brain\"", json);
    }
}
