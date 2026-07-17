using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>The Critic's verdict over N candidate plans (SUG-13): which one wins and
/// why, with the full scoring in the body — durable vault evidence, reviewable like
/// everything else.</summary>
public sealed record Critique(int Selected, string Rationale, string Body);

/// <summary>Strict parser for the Critic's §5.4 verdict file.</summary>
public static class CritiqueParser
{
    public static Critique Parse(string fileText, string filePath, string planId, int candidateCount)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");

        var type = fm.RequireString("type");
        if (!string.Equals(type, "critique", StringComparison.Ordinal))
            throw new VaultSchemaException(filePath, $"type must be exactly 'critique', got '{type}'");

        var actualPlanId = fm.RequireString("plan_id");
        if (!string.Equals(actualPlanId, planId, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"'plan_id' must be exactly '{planId}' (the value in the request), got '{actualPlanId}'");

        var selected = fm.RequireInt("selected");
        if (selected < 1 || selected > candidateCount)
            throw new VaultSchemaException(filePath,
                $"'selected' must be a 1-based index between 1 and {candidateCount}, got {selected}");

        var rationale = fm.RequireString("rationale");
        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        if (string.IsNullOrWhiteSpace(body))
            throw new VaultSchemaException(filePath, "critique body is empty — the scoring is the evidence");

        return new Critique(selected, rationale, body);
    }
}
