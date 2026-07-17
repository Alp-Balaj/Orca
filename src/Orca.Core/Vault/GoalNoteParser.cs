using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Strict parser for the Interviewer's goal note
/// (<c>&lt;plan-id&gt;.goal.md</c>, SUG-08). Frontmatter fail-loud like every vault
/// artifact; the body must carry the five contracted section headings, and its open
/// questions are Markdown task items the human resolves in place.</summary>
public static class GoalNoteParser
{
    /// <summary>The body sections prompts/interviewer.md contracts, in order.</summary>
    public static readonly IReadOnlyList<string> RequiredSections =
    [
        "## Outcome",
        "## Constraints",
        "## Non-goals",
        "## Checkpoint guidance",
        "## Open questions",
    ];

    public static GoalNote Parse(string fileText, string filePath)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");

        var type = fm.RequireString("type");
        if (!string.Equals(type, "goal", StringComparison.Ordinal))
            throw new VaultSchemaException(filePath, $"type must be exactly 'goal', got '{type}'");

        var planId = fm.RequireString("plan_id");
        if (!SchemaRegexes.PlanId().IsMatch(planId))
            throw new VaultSchemaException(filePath, $"plan_id '{planId}' must match {SchemaRegexes.PlanId()}");

        var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath));
        if (!string.Equals(stem, planId, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"plan_id '{planId}' must equal the file name stem '{stem}' (expected {planId}.goal.md)");

        var created = fm.RequireTimestamp("created");
        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        if (string.IsNullOrWhiteSpace(body))
            throw new VaultSchemaException(filePath, "goal note body is empty — an empty brief refines nothing");

        var normalizedBody = body.ReplaceLineEndings("\n");
        foreach (var section in RequiredSections)
        {
            if (!normalizedBody.Contains(section + "\n", StringComparison.Ordinal)
                && !normalizedBody.TrimEnd().EndsWith(section, StringComparison.Ordinal))
                throw new VaultSchemaException(filePath,
                    $"goal note body is missing the contracted section '{section}'");
        }

        return new GoalNote(schemaVersion, planId, created, body);
    }
}
