using Orca.Core.Execution;

namespace Orca.Core.Config;

public enum RoleName
{
    Planner,
    Implementer,
    Reconciler,
    Verifier,
    /// <summary>SUG-01: the read-only repo auditor that grounds the Planner. Optional —
    /// a repo without <c>roles.scout</c> plans goal-only, exactly as before.</summary>
    Scout,
    /// <summary>SUG-08: the goal-refinement interviewer behind <c>orca goal</c>.
    /// Optional — a repo without <c>roles.interviewer</c> just can't run that command.</summary>
    Interviewer,
}

/// <summary>The merged, resolved configuration one run works from: target-repo
/// <c>orca.json</c> layered over the machine's global defaults, vault name already
/// resolved to a machine-local root. Role blocks are validated for completeness only when
/// the role is actually requested (<see cref="GetRole"/>) — step 5 exercises the
/// Implementer only, and forcing owners to invent Planner settings nothing consumes yet
/// would be strictness theater.</summary>
public sealed record OrcaConfig
{
    public required string RepoConfigPath { get; init; }
    public required string GlobalConfigPath { get; init; }

    /// <summary>Machine-local vault root, resolved from the vault name via the global
    /// config's <c>vaults</c> map.</summary>
    public required string VaultRoot { get; init; }

    public required string VaultName { get; init; }
    public required string PlansDir { get; init; }

    public required IReadOnlyList<string> BuildCommand { get; init; }
    public required IReadOnlyList<string> TestCommand { get; init; }

    /// <summary>Wall-clock ceiling per gate command; null = unbounded (Ctrl-C is the
    /// interactive fallback). On timeout the process tree is killed and the gate fails.</summary>
    public required TimeSpan? GateTimeout { get; init; }

    /// <summary>Ceiling for the §5.4 structured-output parse-retry loop; consumed from
    /// step 6 on, required only then.</summary>
    public required int? MaxParseRetries { get; init; }

    /// <summary>Ceiling for verification-failure-fed Implementer attempts per checkpoint
    /// (spec §8); consumed from step 7 on, required only then.</summary>
    public required int? MaxCheckpointAttempts { get; init; }

    /// <summary>SUG-06 §5: optional prompt-size ceiling in bytes. Prepare refuses above
    /// it before anything is billed; absent means only the executor's hard stdin ceiling
    /// applies. Not <c>required</c> so existing fixtures keep building.</summary>
    public int? MaxPromptBytes { get; init; }

    public required int? PlanningCandidates { get; init; }

    /// <summary>Safety layer 2: passed to every role invocation as a generated
    /// <c>--settings</c> <c>permissions.deny</c> payload. Probed 2026-07-06 on 2.1.201:
    /// deny rules survive <c>--safe-mode</c> and win over <c>--allowedTools</c>
    /// (<c>PreToolUse</c> hooks do not survive and are not used). Merged as the union of
    /// global and repo lists — a repo config can add guardrails, never remove
    /// machine-level ones.</summary>
    public required IReadOnlyList<string> DeniedTools { get; init; }

    /// <summary>SUG-12 §1: judge-diff exclusion patterns. Null means the built-in
    /// defaults (JudgeDiff.DefaultExcludes); an explicit empty list disables exclusion.
    /// Not <c>required</c> so existing fixtures keep building.</summary>
    public IReadOnlyList<string>? JudgeDiffExcludes { get; init; }

    /// <summary>Raw merged role blocks; consume through <see cref="GetRole"/>, which is
    /// the validated path.</summary>
    public required RolesSection RoleSections { get; init; }

    /// <summary>What a <c>claude_code_version</c> mismatch does (SUG-15). Not
    /// <c>required</c> and defaults to <see cref="ClaudeVersionPolicy.Warn"/> so every
    /// <c>OrcaConfig</c> built directly (every test fixture that doesn't go through
    /// <see cref="ConfigLoader"/>) gets the same non-breaking default a real, opinion-free
    /// config file gets.</summary>
    public ClaudeVersionPolicy ClaudeVersionPolicy { get; init; } = ClaudeVersionPolicy.Warn;

    /// <summary>The validated executor settings for one role. Throws
    /// <see cref="ConfigException"/> naming every missing field and both files consulted,
    /// so a half-configured role can never launch.</summary>
    public RoleConfig GetRole(RoleName role)
    {
        var section = role switch
        {
            RoleName.Planner => RoleSections.Planner,
            RoleName.Implementer => RoleSections.Implementer,
            RoleName.Reconciler => RoleSections.Reconciler,
            RoleName.Verifier => RoleSections.Verifier,
            RoleName.Scout => RoleSections.Scout,
            RoleName.Interviewer => RoleSections.Interviewer,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unknown role"),
        };

        var name = RoleKey(role);
        if (section is null)
            throw new ConfigException(
                $"role '{name}' is not configured — add roles.{name} to {RepoConfigPath} (or the global defaults at {GlobalConfigPath})");

        var missing = new List<string>();
        if (section.AllowedTools is null)
            missing.Add("allowedTools");
        // BF-11 §1: an EMPTY list is worse than a missing one — under --safe-mode the
        // role has no tools, cannot write its staging file, and burns the entire billed
        // Reflexion retry budget every single time.
        else if (section.AllowedTools.Count == 0)
            throw new ConfigException(
                $"role '{name}' has an empty allowedTools list — with no tools it cannot write its output " +
                $"file and every invocation burns billed retries; list the tools in {RepoConfigPath} (or {GlobalConfigPath})");
        if (section.MaxTurns is null)
            missing.Add("maxTurns");
        if (section.TimeoutSeconds is null)
            missing.Add("timeoutSeconds");
        if (missing.Count > 0)
            throw new ConfigException(
                $"role '{name}' is missing {string.Join(", ", missing)} after merging {RepoConfigPath} over {GlobalConfigPath}");

        return new RoleConfig
        {
            Role = role,
            AllowedTools = section.AllowedTools!,
            MaxTurns = section.MaxTurns!.Value,
            Timeout = TimeSpan.FromSeconds(section.TimeoutSeconds!.Value),
            Model = section.Model,
            MaxBudgetUsd = section.MaxBudgetUsd,
        };
    }

    internal static string RoleKey(RoleName role) => role switch
    {
        RoleName.Planner => "planner",
        RoleName.Implementer => "implementer",
        RoleName.Reconciler => "reconciler",
        RoleName.Verifier => "verifier",
        RoleName.Scout => "scout",
        RoleName.Interviewer => "interviewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unknown role"),
    };
}

/// <summary>One role's validated executor settings — the config-side twin of
/// <c>ClaudeInvocationOptions</c>.</summary>
public sealed record RoleConfig
{
    public required RoleName Role { get; init; }
    public required IReadOnlyList<string> AllowedTools { get; init; }
    public required int MaxTurns { get; init; }
    public required TimeSpan Timeout { get; init; }
    public string? Model { get; init; }
    public decimal? MaxBudgetUsd { get; init; }
}
