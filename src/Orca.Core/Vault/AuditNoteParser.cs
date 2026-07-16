using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Strict parser for the Scout's audit note
/// (<c>&lt;plan-id&gt;.audit.md</c>, SUG-01). Frontmatter fail-loud like every vault
/// artifact; the body must carry the five contracted section headings so the Planner
/// prompt embeds a predictable shape, but their content stays opaque prose.</summary>
public static class AuditNoteParser
{
    /// <summary>The body sections prompts/scout.md contracts, in order.</summary>
    public static readonly IReadOnlyList<string> RequiredSections =
    [
        "## Verified current state",
        "## Key files and patterns",
        "## Dependencies",
        "## Risks",
        "## Suggested checkpoint boundaries",
    ];

    public static AuditNote Parse(string fileText, string filePath)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");

        var type = fm.RequireString("type");
        if (!string.Equals(type, "audit", StringComparison.Ordinal))
            throw new VaultSchemaException(filePath, $"type must be exactly 'audit', got '{type}'");

        var planId = fm.RequireString("plan_id");
        if (!SchemaRegexes.PlanId().IsMatch(planId))
            throw new VaultSchemaException(filePath, $"plan_id '{planId}' must match {SchemaRegexes.PlanId()}");

        var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath));
        if (!string.Equals(stem, planId, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"plan_id '{planId}' must equal the file name stem '{stem}' (expected {planId}.audit.md)");

        var created = fm.RequireTimestamp("created");
        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        if (string.IsNullOrWhiteSpace(body))
            throw new VaultSchemaException(filePath, "audit body is empty — an empty audit grounds nothing");

        var normalizedBody = body.ReplaceLineEndings("\n");
        foreach (var section in RequiredSections)
        {
            if (!normalizedBody.Contains(section + "\n", StringComparison.Ordinal)
                && !normalizedBody.TrimEnd().EndsWith(section, StringComparison.Ordinal))
                throw new VaultSchemaException(filePath,
                    $"audit body is missing the contracted section '{section}'");
        }

        return new AuditNote(schemaVersion, planId, created, body);
    }
}
