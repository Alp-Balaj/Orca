using Orca.Core.Config;

namespace Orca.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private const string RepoConfig = """
        {
          "schemaVersion": 1,
          "vault": {
            "name": "brian-the-brain",
            "plansDir": "Dev/RecipeApp/Plans"
          },
          "commands": {
            "build": ["dotnet", "build"],
            "test": ["dotnet", "test"]
          },
          "maxParseRetries": 2,
          "maxCheckpointAttempts": 3,
          "deniedTools": ["Bash(git push*)", "Bash(rm *)"],
          "roles": {
            "implementer": {
              "allowedTools": ["Read", "Edit", "Bash(dotnet build*)"],
              "maxTurns": 60,
              "timeoutSeconds": 1800,
              "maxBudgetUsd": 3.0
            }
          }
        }
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "orca-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigLoaderTests() => Directory.CreateDirectory(_root);

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

    private string WriteRepoConfig(string json)
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "orca.json"), json);
        return repo;
    }

    private string WriteGlobalConfig(string json)
    {
        var path = Path.Combine(_root, "global-config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string GlobalWithVault() => WriteGlobalConfig($$"""
        {
          "schemaVersion": 1,
          "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} }
        }
        """);

    [Fact]
    public void Loads_repo_config_over_global_and_resolves_the_vault_name()
    {
        var repo = WriteRepoConfig(RepoConfig);
        var config = ConfigLoader.Load(repo, GlobalWithVault());

        Assert.Equal("brian-the-brain", config.VaultName);
        Assert.Equal(Path.Combine(_root, "vault"), config.VaultRoot);
        Assert.Equal("Dev/RecipeApp/Plans", config.PlansDir);
        Assert.Equal(["dotnet", "build"], config.BuildCommand);
        Assert.Equal(2, config.MaxParseRetries);
        Assert.Equal(3, config.MaxCheckpointAttempts);
    }

    [Fact]
    public void Role_block_maps_onto_the_executor_options_shape()
    {
        var config = ConfigLoader.Load(WriteRepoConfig(RepoConfig), GlobalWithVault());

        var implementer = config.GetRole(RoleName.Implementer);
        Assert.Equal(["Read", "Edit", "Bash(dotnet build*)"], implementer.AllowedTools);
        Assert.Equal(60, implementer.MaxTurns);
        Assert.Equal(TimeSpan.FromMinutes(30), implementer.Timeout);
        Assert.Null(implementer.Model);
        Assert.Equal(3.0m, implementer.MaxBudgetUsd);
    }

    [Fact]
    public void Repo_fields_win_over_global_field_by_field()
    {
        var global = WriteGlobalConfig($$"""
            {
              "schemaVersion": 1,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} },
              "maxParseRetries": 9,
              "roles": {
                "implementer": { "model": "claude-sonnet-5", "maxTurns": 10 }
              }
            }
            """);

        var config = ConfigLoader.Load(WriteRepoConfig(RepoConfig), global);

        // Repo declared 2; global's 9 loses. Global's model survives because the repo
        // block doesn't set one; repo's maxTurns 60 beats global's 10.
        Assert.Equal(2, config.MaxParseRetries);
        var implementer = config.GetRole(RoleName.Implementer);
        Assert.Equal("claude-sonnet-5", implementer.Model);
        Assert.Equal(60, implementer.MaxTurns);
    }

    [Fact]
    public void Denied_tools_merge_as_a_union_never_replaced()
    {
        // Owner decision 2026-07-06: a repo config can add deny rules but never remove
        // machine-level ones — fail-closed.
        var global = WriteGlobalConfig($$"""
            {
              "schemaVersion": 1,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} },
              "deniedTools": ["Bash(sudo *)", "Bash(git push*)"]
            }
            """);

        var config = ConfigLoader.Load(WriteRepoConfig(RepoConfig), global);

        Assert.Equal(["Bash(sudo *)", "Bash(git push*)", "Bash(rm *)"], config.DeniedTools);
    }

    [Fact]
    public void Missing_global_file_is_fine_but_the_vault_name_must_then_fail_loudly()
    {
        var repo = WriteRepoConfig(RepoConfig);
        var missingGlobal = Path.Combine(_root, "nope.json");

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repo, missingGlobal));
        Assert.Contains("brian-the-brain", e.Message);
        Assert.Contains("vaults", e.Message);
    }

    [Fact]
    public void Missing_orca_json_names_the_expected_path()
    {
        var repo = Path.Combine(_root, "empty-repo");
        Directory.CreateDirectory(repo);

        // MissingRepoConfigException, not a plain ConfigException: the CLI catches this
        // subtype to append "run 'orca init' to scaffold one." without Core knowing the
        // command name. It stays a ConfigException so the general catch still covers it.
        var e = Assert.Throws<MissingRepoConfigException>(() => ConfigLoader.Load(repo, GlobalWithVault()));
        Assert.IsAssignableFrom<ConfigException>(e);
        Assert.Contains("orca.json", e.Message);
        Assert.Contains(repo, e.Message);
    }

    [Fact]
    public void Vaults_map_in_the_repo_config_is_rejected()
    {
        // Machine-local paths in a committed file are exactly what the indirection avoids.
        var repo = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vaults": { "x": "/somewhere" }
            }
            """);

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repo, GlobalWithVault()));
        Assert.Contains("machine-local", e.Message);
    }

    [Fact]
    public void Unknown_keys_fail_loudly()
    {
        var repo = WriteRepoConfig("""{ "schemaVersion": 1, "vualt": {} }""");

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(repo, GlobalWithVault()));
    }

    [Fact]
    public void Unknown_role_names_fail_loudly()
    {
        var repo = WriteRepoConfig("""
            { "schemaVersion": 1, "roles": { "architect": { "maxTurns": 5 } } }
            """);

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(repo, GlobalWithVault()));
    }

    [Fact]
    public void Missing_schema_version_fails_loudly()
    {
        var repo = WriteRepoConfig("""{ "vault": { "name": "x", "plansDir": "Plans" } }""");

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repo, GlobalWithVault()));
        Assert.Contains("schemaVersion", e.Message);
    }

    [Fact]
    public void Malformed_json_fails_loudly_with_the_path()
    {
        var repo = WriteRepoConfig("{ not json");

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repo, GlobalWithVault()));
        Assert.Contains("orca.json", e.Message);
    }

    [Fact]
    public void Relative_vault_path_in_the_global_map_is_rejected()
    {
        var global = WriteGlobalConfig("""
            { "schemaVersion": 1, "vaults": { "brian-the-brain": "relative/vault" } }
            """);

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(WriteRepoConfig(RepoConfig), global));
        Assert.Contains("absolute", e.Message);
    }

    [Fact]
    public void An_unconfigured_role_fails_only_when_requested()
    {
        var config = ConfigLoader.Load(WriteRepoConfig(RepoConfig), GlobalWithVault());

        var e = Assert.Throws<ConfigException>(() => config.GetRole(RoleName.Planner));
        Assert.Contains("planner", e.Message);
    }

    [Fact]
    public void A_partially_configured_role_names_every_missing_field()
    {
        var repo = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Plans" },
              "commands": { "build": ["dotnet", "build"], "test": ["dotnet", "test"] },
              "roles": { "implementer": { "allowedTools": ["Read"] } }
            }
            """);
        var config = ConfigLoader.Load(repo, GlobalWithVault());

        var e = Assert.Throws<ConfigException>(() => config.GetRole(RoleName.Implementer));
        Assert.Contains("maxTurns", e.Message);
        Assert.Contains("timeoutSeconds", e.Message);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "vault": { "plansDir": "Plans" }, "commands": { "build": ["b"], "test": ["t"] } }""", "vault.name")]
    [InlineData("""{ "schemaVersion": 1, "vault": { "name": "brian-the-brain" }, "commands": { "build": ["b"], "test": ["t"] } }""", "vault.plansDir")]
    [InlineData("""{ "schemaVersion": 1, "vault": { "name": "brian-the-brain", "plansDir": "Plans" }, "commands": { "test": ["t"] } }""", "commands.build")]
    [InlineData("""{ "schemaVersion": 1, "vault": { "name": "brian-the-brain", "plansDir": "Plans" }, "commands": { "build": [], "test": ["t"] } }""", "commands.build")]
    public void Required_merged_fields_fail_loudly_when_absent(string json, string expectedInMessage)
    {
        var e = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Load(WriteRepoConfig(json), GlobalWithVault()));
        Assert.Contains(expectedInMessage, e.Message);
    }

    [Theory]
    [InlineData(""" "maxParseRetries": -1 """)]
    [InlineData(""" "maxCheckpointAttempts": 0 """)]
    [InlineData(""" "gateTimeoutSeconds": 0 """)]
    [InlineData(""" "planning": { "candidates": 1 } """)]
    [InlineData(""" "deniedTools": ["Bash(rm *)", " "] """)]
    [InlineData(""" "roles": { "verifier": { "maxTurns": 0 } } """)]
    [InlineData(""" "roles": { "verifier": { "timeoutSeconds": 0 } } """)]
    [InlineData(""" "roles": { "verifier": { "maxBudgetUsd": 0 } } """)]
    public void Out_of_range_values_fail_loudly(string fragment)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Plans" },
              "commands": { "build": ["b"], "test": ["t"] },
              {{fragment}}
            }
            """;

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(WriteRepoConfig(json), GlobalWithVault()));
    }

    [Fact]
    public void A_value_invalid_only_in_the_global_config_names_both_files_not_just_the_repo()
    {
        // BF-16: the repo file omits maxCheckpointAttempts entirely — an offending value
        // that's global-only must not produce an error that reads as if it lives in the
        // repo's orca.json, the way "{repoPath}: maxCheckpointAttempts must be >= 1..."
        // used to.
        var global = WriteGlobalConfig($$"""
            {
              "schemaVersion": 1,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} },
              "maxCheckpointAttempts": 0
            }
            """);
        var repoPath = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Plans" },
              "commands": { "build": ["b"], "test": ["t"] }
            }
            """);

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repoPath, global));

        Assert.Contains("maxCheckpointAttempts must be >= 1, got 0", e.Message);
        Assert.Contains(Path.Combine(repoPath, "orca.json"), e.Message);
        Assert.Contains(global, e.Message);
    }

    [Fact]
    public void A_role_field_invalid_only_in_the_global_config_names_both_files()
    {
        // Same class of bug (BF-16), through ValidateRoleSection: the repo declares no
        // "verifier" block at all, so the whole section — including the bad maxTurns —
        // comes from global.
        var global = WriteGlobalConfig($$"""
            {
              "schemaVersion": 1,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} },
              "roles": { "verifier": { "maxTurns": 0 } }
            }
            """);
        var repoPath = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Plans" },
              "commands": { "build": ["b"], "test": ["t"] }
            }
            """);

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repoPath, global));

        Assert.Contains("roles.verifier.maxTurns must be >= 1, got 0", e.Message);
        Assert.Contains(Path.Combine(repoPath, "orca.json"), e.Message);
        Assert.Contains(global, e.Message);
    }

    [Fact]
    public void Gate_timeout_merges_repo_over_global_and_maps_to_a_timespan()
    {
        var global = WriteGlobalConfig($$"""
            {
              "schemaVersion": 1,
              "gateTimeoutSeconds": 600,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} }
            }
            """);
        var repo = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Plans" },
              "commands": { "build": ["b"], "test": ["t"] },
              "gateTimeoutSeconds": 900
            }
            """);

        Assert.Equal(TimeSpan.FromSeconds(900), ConfigLoader.Load(repo, global).GateTimeout);
    }

    [Fact]
    public void An_absent_gate_timeout_means_unbounded()
    {
        var repo = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Plans" },
              "commands": { "build": ["b"], "test": ["t"] }
            }
            """);

        Assert.Null(ConfigLoader.Load(repo, GlobalWithVault()).GateTimeout);
    }

    [Fact]
    public void Backslashes_in_plans_dir_are_rejected_identically_on_both_machines()
    {
        // BF-09 §4: a backslashed plansDir works on Windows and silently becomes one
        // literal directory name on Linux.
        var global = WriteGlobalConfig($$"""
            {
              "schemaVersion": 1,
              "vaults": { "brian-the-brain": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "vault"))}} }
            }
            """);
        var repoPath = WriteRepoConfig("""
            {
              "schemaVersion": 1,
              "vault": { "name": "brian-the-brain", "plansDir": "Orca\\Repo\\Plans" },
              "commands": { "build": ["b"], "test": ["t"] }
            }
            """);

        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Load(repoPath, global));
        Assert.Contains("contains backslashes", e.Message);
        Assert.Contains("forward slashes", e.Message);
    }
}
