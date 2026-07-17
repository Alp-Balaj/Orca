namespace Orca.Core.Vault;

/// <summary>Strict validation for the Summarizer's session note (SUG-09): frontmatter
/// pinned to the requested identity, contracted body sections present, and the
/// vault-is-knowledge length discipline enforced mechanically.</summary>
public static class SessionNoteParser
{
    public static readonly IReadOnlyList<string> RequiredSections =
    [
        "## What changed",
        "## Surprises & gotchas",
        "## Verification",
    ];

    /// <summary>Hard ceiling on body lines — the prompt says 40; the parser allows slack
    /// but refuses a transcript dump outright.</summary>
    public const int MaxBodyLines = 80;

    public static void Parse(
        string fileText, string filePath, string planId, string checkpointId, int attempt)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");

        RequireExact(filePath, "type", "session", fm.RequireString("type"));
        RequireExact(filePath, "plan_id", planId, fm.RequireString("plan_id"));
        RequireExact(filePath, "checkpoint_id", checkpointId, fm.RequireString("checkpoint_id"));
        RequireExact(filePath, "attempt", attempt.ToString(), fm.RequireInt("attempt").ToString());
        fm.RequireTimestamp("created");
        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        if (string.IsNullOrWhiteSpace(body))
            throw new VaultSchemaException(filePath, "session note body is empty — an empty note distills nothing");

        var normalized = body.ReplaceLineEndings("\n");
        foreach (var section in RequiredSections)
        {
            if (!normalized.Contains(section + "\n", StringComparison.Ordinal)
                && !normalized.TrimEnd().EndsWith(section, StringComparison.Ordinal))
                throw new VaultSchemaException(filePath,
                    $"session note body is missing the contracted section '{section}'");
        }

        var lineCount = normalized.Split('\n').Length;
        if (lineCount > MaxBodyLines)
            throw new VaultSchemaException(filePath,
                $"session note body is {lineCount} lines — the vault is knowledge, not logs; distill it under {MaxBodyLines}");
    }

    private static void RequireExact(string filePath, string field, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"'{field}' must be exactly '{expected}' (the value in the request), got '{actual}'");
    }
}
