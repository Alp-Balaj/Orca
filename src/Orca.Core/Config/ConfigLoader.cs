using Orca.Core.Execution;

namespace Orca.Core.Config;

/// <summary>Loads the committed target-repo <c>orca.json</c> over the machine's global
/// defaults (spec §3). Merge is field-by-field with the repo winning and arrays replacing
/// wholesale — except <see cref="ConfigDocument.DeniedTools"/>, which is the union of both
/// lists (owner decision 2026-07-06: fail-closed; a repo config can add deny rules but
/// never silently remove machine-level ones).</summary>
public static class ConfigLoader
{
    public const string RepoFileName = "orca.json";

    /// <summary>Machine-local defaults: <c>~/.config/orca/config.json</c> on Linux/macOS
    /// (XDG), <c>%APPDATA%\orca\config.json</c> on Windows — both via
    /// <see cref="Environment.SpecialFolder.ApplicationData"/>.</summary>
    public static string GlobalConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "orca",
            "config.json");

    /// <summary>Loads, merges, and resolves. A missing global file is fine (it is a
    /// defaults layer); a missing <c>orca.json</c> is not — it marks the target repo. An
    /// unresolvable vault name fails here, loudly, because every vault operation would
    /// otherwise fail somewhere less explicable.</summary>
    public static OrcaConfig Load(string targetRepoRoot, string? globalConfigPath = null)
    {
        if (string.IsNullOrWhiteSpace(targetRepoRoot))
            throw new ArgumentException("target repo root must be a non-empty path", nameof(targetRepoRoot));

        var repoPath = Path.Combine(Path.GetFullPath(targetRepoRoot), RepoFileName);
        if (!File.Exists(repoPath))
            throw new MissingRepoConfigException(
                $"no {RepoFileName} found at {repoPath} — is this the target repo root? (spec §3: {RepoFileName} is committed at the target repo's root)");

        globalConfigPath ??= GlobalConfigPath();
        var global = File.Exists(globalConfigPath)
            ? ConfigDocumentParser.Parse(File.ReadAllText(globalConfigPath), globalConfigPath)
            : new ConfigDocument { SchemaVersion = 1 };

        var repo = ConfigDocumentParser.Parse(File.ReadAllText(repoPath), repoPath);
        if (repo.Vaults is not null)
            throw new ConfigException(
                $"{repoPath}: 'vaults' is machine-local and belongs only in the global config ({globalConfigPath}) — a committed {RepoFileName} must never carry machine paths; name the vault via vault.name instead");

        var merged = Merge(global, repo);
        return Resolve(merged, repoPath, globalConfigPath);
    }

    internal static ConfigDocument Merge(ConfigDocument global, ConfigDocument repo) => new()
    {
        SchemaVersion = 1,
        Vault = MergeVault(global.Vault, repo.Vault),
        Commands = MergeCommands(global.Commands, repo.Commands),
        GateTimeoutSeconds = repo.GateTimeoutSeconds ?? global.GateTimeoutSeconds,
        MaxParseRetries = repo.MaxParseRetries ?? global.MaxParseRetries,
        MaxCheckpointAttempts = repo.MaxCheckpointAttempts ?? global.MaxCheckpointAttempts,
        MaxPromptBytes = repo.MaxPromptBytes ?? global.MaxPromptBytes,
        Planning = new PlanningSection
        {
            Candidates = repo.Planning?.Candidates ?? global.Planning?.Candidates,
        },
        DeniedTools = UnionDeniedTools(global.DeniedTools, repo.DeniedTools),
        Roles = MergeRoles(global.Roles, repo.Roles),
        Vaults = global.Vaults,
        ClaudeVersionPolicy = repo.ClaudeVersionPolicy ?? global.ClaudeVersionPolicy,
    };

    private static VaultSection MergeVault(VaultSection? global, VaultSection? repo) => new()
    {
        Name = repo?.Name ?? global?.Name,
        PlansDir = repo?.PlansDir ?? global?.PlansDir,
    };

    private static CommandsSection MergeCommands(CommandsSection? global, CommandsSection? repo) => new()
    {
        Build = repo?.Build ?? global?.Build,
        Test = repo?.Test ?? global?.Test,
    };

    private static IReadOnlyList<string> UnionDeniedTools(
        IReadOnlyList<string>? global, IReadOnlyList<string>? repo)
    {
        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in (global ?? []).Concat(repo ?? []))
        {
            if (seen.Add(pattern))
                union.Add(pattern);
        }

        return union;
    }

    private static RolesSection MergeRoles(RolesSection? global, RolesSection? repo) => new()
    {
        Planner = MergeRole(global?.Planner, repo?.Planner),
        Implementer = MergeRole(global?.Implementer, repo?.Implementer),
        Reconciler = MergeRole(global?.Reconciler, repo?.Reconciler),
        Verifier = MergeRole(global?.Verifier, repo?.Verifier),
    };

    private static RoleSection? MergeRole(RoleSection? global, RoleSection? repo)
    {
        if (global is null && repo is null)
            return null;
        return new RoleSection
        {
            AllowedTools = repo?.AllowedTools ?? global?.AllowedTools,
            MaxTurns = repo?.MaxTurns ?? global?.MaxTurns,
            TimeoutSeconds = repo?.TimeoutSeconds ?? global?.TimeoutSeconds,
            Model = repo?.Model ?? global?.Model,
            MaxBudgetUsd = repo?.MaxBudgetUsd ?? global?.MaxBudgetUsd,
        };
    }

    private static OrcaConfig Resolve(ConfigDocument merged, string repoPath, string globalPath)
    {
        // Every check below runs on the merged/effective value — repo and global are
        // layered field-by-field (see Merge), so a value that fails here could have come
        // from either file. Naming both, the way OrcaConfig.GetRole already does, beats
        // guessing which one to blame (BF-16): "{repoPath}: ..." previously blamed the
        // repo file even when the value came only from the global config.
        var vaultName = merged.Vault?.Name;
        if (string.IsNullOrWhiteSpace(vaultName))
            throw new ConfigException($"vault.name is required after merging {repoPath} over {globalPath}");

        var plansDir = merged.Vault?.PlansDir;
        if (string.IsNullOrWhiteSpace(plansDir))
            throw new ConfigException($"vault.plansDir is required after merging {repoPath} over {globalPath}");

        if (merged.Vaults is null || !merged.Vaults.TryGetValue(vaultName, out var vaultRoot))
            throw new ConfigException(
                $"vault '{vaultName}' is not defined in the global config ({globalPath}) — add \"vaults\": {{ \"{vaultName}\": \"<absolute path on this machine>\" }}");
        if (!Path.IsPathRooted(vaultRoot))
            throw new ConfigException(
                $"{globalPath}: vaults['{vaultName}'] must be an absolute path, got '{vaultRoot}'");

        var build = RequireCommand(merged.Commands?.Build, "commands.build", repoPath, globalPath);
        var test = RequireCommand(merged.Commands?.Test, "commands.test", repoPath, globalPath);

        if (merged.GateTimeoutSeconds is < 1)
            throw new ConfigException(
                $"gateTimeoutSeconds must be >= 1, got {merged.GateTimeoutSeconds}, after merging {repoPath} over {globalPath}");
        if (merged.MaxParseRetries is < 0)
            throw new ConfigException(
                $"maxParseRetries must be >= 0, got {merged.MaxParseRetries}, after merging {repoPath} over {globalPath}");
        if (merged.MaxCheckpointAttempts is < 1)
            throw new ConfigException(
                $"maxCheckpointAttempts must be >= 1, got {merged.MaxCheckpointAttempts}, after merging {repoPath} over {globalPath}");
        if (merged.MaxPromptBytes is < 1)
            throw new ConfigException(
                $"maxPromptBytes must be >= 1, got {merged.MaxPromptBytes}, after merging {repoPath} over {globalPath}");
        if (merged.Planning?.Candidates is { } candidates and < 2)
            throw new ConfigException(
                $"planning.candidates must be >= 2, got {candidates}, after merging {repoPath} over {globalPath}");

        var denied = merged.DeniedTools ?? [];
        if (denied.Any(string.IsNullOrWhiteSpace))
            throw new ConfigException(
                $"deniedTools patterns must be non-empty; got a blank entry, after merging {repoPath} over {globalPath}");

        var roles = merged.Roles ?? new RolesSection();
        foreach (var (key, section) in EnumerateRoles(roles))
            ValidateRoleSection(key, section, repoPath, globalPath);

        var claudeVersionPolicy = ParseClaudeVersionPolicy(merged.ClaudeVersionPolicy, repoPath, globalPath);

        return new OrcaConfig
        {
            RepoConfigPath = repoPath,
            GlobalConfigPath = globalPath,
            VaultRoot = vaultRoot,
            VaultName = vaultName,
            PlansDir = plansDir,
            BuildCommand = build,
            TestCommand = test,
            GateTimeout = merged.GateTimeoutSeconds is { } gateSeconds
                ? TimeSpan.FromSeconds(gateSeconds)
                : null,
            MaxParseRetries = merged.MaxParseRetries,
            MaxCheckpointAttempts = merged.MaxCheckpointAttempts,
            MaxPromptBytes = merged.MaxPromptBytes,
            PlanningCandidates = merged.Planning?.Candidates,
            DeniedTools = denied,
            RoleSections = roles,
            ClaudeVersionPolicy = claudeVersionPolicy,
        };
    }

    /// <summary>"warn" (default, absent config) or "refuse" — SUG-15's opt-in, must default
    /// to the non-breaking choice per the plan's constraints.</summary>
    private static ClaudeVersionPolicy ParseClaudeVersionPolicy(string? raw, string repoPath, string globalPath) =>
        raw switch
        {
            null => ClaudeVersionPolicy.Warn,
            "warn" => ClaudeVersionPolicy.Warn,
            "refuse" => ClaudeVersionPolicy.Refuse,
            _ => throw new ConfigException(
                $"claudeVersionPolicy must be 'warn' or 'refuse', got '{raw}', after merging {repoPath} over {globalPath}"),
        };

    private static IReadOnlyList<string> RequireCommand(
        IReadOnlyList<string>? argv, string field, string repoPath, string globalPath)
    {
        if (argv is null || argv.Count == 0)
            throw new ConfigException(
                $"{field} is required and must be a non-empty argv array after merging {repoPath} over {globalPath}");
        if (argv.Any(string.IsNullOrWhiteSpace))
            throw new ConfigException(
                $"{field} must not contain blank argv elements after merging {repoPath} over {globalPath}");
        return argv;
    }

    private static IEnumerable<(string Key, RoleSection? Section)> EnumerateRoles(RolesSection roles)
    {
        yield return ("planner", roles.Planner);
        yield return ("implementer", roles.Implementer);
        yield return ("reconciler", roles.Reconciler);
        yield return ("verifier", roles.Verifier);
    }

    /// <summary>Shape validation for whatever fields a role block does declare; block
    /// completeness is only enforced when the role is invoked (<see cref="OrcaConfig.GetRole"/>).</summary>
    private static void ValidateRoleSection(string key, RoleSection? section, string repoPath, string globalPath)
    {
        if (section is null)
            return;
        if (section.AllowedTools is { } tools && tools.Any(string.IsNullOrWhiteSpace))
            throw new ConfigException(
                $"roles.{key}.allowedTools must not contain blank patterns, after merging {repoPath} over {globalPath}");
        if (section.MaxTurns is < 1)
            throw new ConfigException(
                $"roles.{key}.maxTurns must be >= 1, got {section.MaxTurns}, after merging {repoPath} over {globalPath}");
        if (section.TimeoutSeconds is < 1)
            throw new ConfigException(
                $"roles.{key}.timeoutSeconds must be >= 1, got {section.TimeoutSeconds}, after merging {repoPath} over {globalPath}");
        if (section.Model is not null && string.IsNullOrWhiteSpace(section.Model))
            throw new ConfigException(
                $"roles.{key}.model, when set, must be non-empty, after merging {repoPath} over {globalPath}");
        if (section.MaxBudgetUsd is <= 0)
            throw new ConfigException(
                $"roles.{key}.maxBudgetUsd must be positive, got {section.MaxBudgetUsd}, after merging {repoPath} over {globalPath}");
    }
}
