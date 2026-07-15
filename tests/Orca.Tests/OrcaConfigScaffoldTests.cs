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
