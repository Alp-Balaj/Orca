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
    public IReadOnlyDictionary<string, string>? Vaults { get; init; }
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

/// <summary>The four roles, fixed by spec §8 — a record rather than a dictionary so an
/// unknown role name fails the parse instead of riding along silently.</summary>
public sealed record RolesSection
{
    public RoleSection? Planner { get; init; }
    public RoleSection? Implementer { get; init; }
    public RoleSection? Reconciler { get; init; }
    public RoleSection? Verifier { get; init; }
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
