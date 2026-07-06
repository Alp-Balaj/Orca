using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Strict parser for checkpoint child notes (<c>&lt;checkpoint-id&gt;.md</c>).
/// The frontmatter exists purely for cross-checking; the body is the Implementer's
/// instruction source and stays opaque.</summary>
public static class ChildNoteParser
{
    public static ChildNote Parse(string fileText, string filePath)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");

        var planId = fm.RequireString("plan_id");
        if (!SchemaRegexes.PlanId().IsMatch(planId))
            throw new VaultSchemaException(filePath, $"plan_id '{planId}' must match {SchemaRegexes.PlanId()}");

        var checkpointId = fm.RequireString("checkpoint_id");
        if (!SchemaRegexes.CheckpointId().IsMatch(checkpointId))
            throw new VaultSchemaException(filePath, $"checkpoint_id '{checkpointId}' must match {SchemaRegexes.CheckpointId()}");

        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (!string.Equals(stem, checkpointId, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath, $"checkpoint_id '{checkpointId}' must equal the file name stem '{stem}'");

        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        return new ChildNote(schemaVersion, planId, checkpointId, body);
    }
}
