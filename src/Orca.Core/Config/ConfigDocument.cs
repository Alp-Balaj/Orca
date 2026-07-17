using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orca.Core.Config;

/// <summary>One config file as written on disk — the owner-approved schema with every
/// field optional, because the same shape serves as both the committed target-repo
/// <c>orca.json</c> and the machine-local global defaults, layered by
/// <see cref="ConfigLoader"/>. <see cref="Vaults"/> (vault name → machine-local absolute
/// path, the vault-name indirection) is only legal in the global file; a committed
/// <c>orca.json</c> carrying machine paths is exactly what the indirection exists to
/// prevent, and <see cref="ConfigLoader"/> rejects it.</summary>
public sealed record ConfigDocument
{
    public int? SchemaVersion { get; init; }
    public VaultSection? Vault { get; init; }
    public CommandsSection? Commands { get; init; }

    /// <summary>Wall-clock ceiling for one deterministic-gate command (build or tests),
    /// the runaway-build backstop (owner-approved 2026-07-07). Null/absent = unbounded —
    /// <c>checkpoint-run</c> is interactive and Ctrl-C remains the fallback.</summary>
    public int? GateTimeoutSeconds { get; init; }

    public int? MaxParseRetries { get; init; }
    public int? MaxCheckpointAttempts { get; init; }
    public PlanningSection? Planning { get; init; }
    public IReadOnlyList<string>? DeniedTools { get; init; }
    public RolesSection? Roles { get; init; }

    /// <summary>SUG-06 §5: optional prompt-size ceiling in bytes — Prepare refuses a
    /// prompt above it BEFORE anything is billed (the executor's 512 KiB stdin ceiling
    /// still backstops delivery; this one is a budget guard the human tunes).</summary>
    public int? MaxPromptBytes { get; init; }

    /// <summary>SUG-12 §1: file patterns whose diff content is excluded from the JUDGE
    /// prompts (replaced by explicit markers). Absent means JudgeDiff.DefaultExcludes;
    /// an explicit empty list disables exclusion. Repo value replaces the global one
    /// wholesale — exclusion lists don't union sensibly.</summary>
    public IReadOnlyList<string>? JudgeDiffExcludes { get; init; }
    public IReadOnlyDictionary<string, string>? Vaults { get; init; }

    /// <summary>"warn" or "refuse" for what a <c>claude_code_version</c> mismatch against
    /// Orca's pinned/probed version does (SUG-15). Absent means "warn" — the non-breaking
    /// default an opinion-free config must keep getting.</summary>
    public string? ClaudeVersionPolicy { get; init; }
}

public sealed record VaultSection
{
    public string? Name { get; init; }

    /// <summary>Relative to the vault root, forward slashes, no '.'/'..' segments —
    /// the <c>VaultLayout</c> rules.</summary>
    public string? PlansDir { get; init; }
}

/// <summary>Deterministic-gate commands as argv arrays, never shell strings — approved
/// in session 1 alongside the vault-name indirection.</summary>
public sealed record CommandsSection
{
    public IReadOnlyList<string>? Build { get; init; }
    public IReadOnlyList<string>? Test { get; init; }
}

public sealed record PlanningSection
{
    /// <summary>Candidate count N for multi-candidate planning (spec §8); >= 2, matching
    /// the plan-frontmatter rule.</summary>
    public int? Candidates { get; init; }
}

/// <summary>The roles, fixed by spec §8 (plus SUG-01's optional Scout) — a record rather
/// than a dictionary so an unknown role name fails the parse instead of riding along
/// silently.</summary>
public sealed record RolesSection
{
    public RoleSection? Planner { get; init; }
    public RoleSection? Implementer { get; init; }
    public RoleSection? Reconciler { get; init; }
    public RoleSection? Verifier { get; init; }

    /// <summary>SUG-01: optional — absent means plan-generate runs goal-only, exactly as
    /// before the Scout existed.</summary>
    public RoleSection? Scout { get; init; }
    /// <summary>SUG-08: optional — absent means `orca goal` refuses with a clear
    /// message; every other command is unaffected.</summary>
    public RoleSection? Interviewer { get; init; }
}

/// <summary>Per-role executor settings; maps 1:1 onto <c>ClaudeInvocationOptions</c>.
/// There is deliberately no auth field — all four roles ride the machine's interactive
/// <c>claude</c> subscription login (spec §4).</summary>
public sealed record RoleSection
{
    /// <summary>Verbatim <c>--allowedTools</c> patterns; never normalized (the
    /// <c>Bash(git diff *)</c> vs <c>Bash(git diff*)</c> distinction is load-bearing).</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    public int? MaxTurns { get; init; }
    public int? TimeoutSeconds { get; init; }

    /// <summary>Optional model id. Under subscription auth a model the plan doesn't offer
    /// fails at invocation time with the CLI's own message, not at config load — that is
    /// accepted fail-loud behavior (plan: orca-build, open items).</summary>
    public string? Model { get; init; }

    /// <summary>Passed as <c>--max-budget-usd</c>; enforced by the CLI itself even under
    /// subscription auth (probed 2026-07-06 on 2.1.201: distinct result subtype
    /// <c>error_max_budget_usd</c>).</summary>
    public decimal? MaxBudgetUsd { get; init; }
}

public static class ConfigDocumentParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ConfigDocument Parse(string json, string filePath)
    {
        ConfigDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigDocument>(json, Options);
        }
        catch (JsonException e)
        {
            throw new ConfigException($"{filePath}: not valid config JSON: {e.Message}", e);
        }

        if (document is null)
            throw new ConfigException($"{filePath}: config JSON is null");

        if (document.SchemaVersion is null)
            throw new ConfigException($"{filePath}: schemaVersion is required");
        if (document.SchemaVersion != 1)
            throw new ConfigException(
                $"{filePath}: unsupported schemaVersion {document.SchemaVersion}; this build of Orca supports version 1");

        return document;
    }
}
