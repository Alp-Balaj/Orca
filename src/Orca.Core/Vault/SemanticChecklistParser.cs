using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Strict parser for the Verifier's staging output. Beyond the shared identity
/// rules, the criteria must correspond 1:1, in order, byte-exact with the checkpoint's
/// acceptance criteria — a paraphrased, skipped, or reordered criterion is drift, and
/// every non-pass verdict must carry a note, because that note is the Reflexion feedback
/// the next attempt runs on.</summary>
public static class SemanticChecklistParser
{
    public static SemanticChecklist ParseStaging(
        string fileText,
        string filePath,
        AttemptIdentity expected,
        IReadOnlyList<string> acceptanceCriteria)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        var planId = fm.RequireString("plan_id");
        var planVersion = fm.RequireInt("plan_version");
        var checkpointId = fm.RequireString("checkpoint_id");
        var attempt = fm.RequireInt("attempt");

        var criteriaNodes = fm.RequireSequence("criteria");
        var criteria = new List<SemanticCriterionResult>();
        for (var i = 0; i < criteriaNodes.Count; i++)
        {
            var item = new MappingReader(criteriaNodes[i], filePath, $"criteria[{i}]");
            criteria.Add(new SemanticCriterionResult(
                item.RequireString("criterion"),
                SchemaEnums.ParseSemanticVerdict(item.RequireString("verdict"), filePath),
                item.OptionalString("note")));
            item.ThrowIfUnknownKeys();
        }

        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");
        RequireExact(filePath, "plan_id", expected.PlanId, planId);
        RequireExact(filePath, "plan_version", expected.PlanVersion.ToString(), planVersion.ToString());
        RequireExact(filePath, "checkpoint_id", expected.CheckpointId, checkpointId);
        RequireExact(filePath, "attempt", expected.Attempt.ToString(), attempt.ToString());

        if (criteria.Count != acceptanceCriteria.Count)
            throw new VaultSchemaException(filePath,
                $"'criteria' must have exactly one entry per acceptance criterion — expected {acceptanceCriteria.Count}, got {criteria.Count}");
        for (var i = 0; i < criteria.Count; i++)
        {
            if (!string.Equals(criteria[i].Criterion, acceptanceCriteria[i], StringComparison.Ordinal))
                throw new VaultSchemaException(filePath,
                    $"criteria[{i}].criterion must be byte-exact the checkpoint's acceptance criterion at that position: " +
                    $"expected \"{acceptanceCriteria[i]}\", got \"{criteria[i].Criterion}\" — copy criteria verbatim, in order, no paraphrase");
            if (criteria[i].Verdict != SemanticVerdict.Pass && string.IsNullOrWhiteSpace(criteria[i].Note))
                throw new VaultSchemaException(filePath,
                    $"criteria[{i}] has verdict '{SchemaEnums.ToYaml(criteria[i].Verdict)}' but no note — " +
                    "every non-pass verdict must explain itself, citing evidence from the diff or gate output");
        }

        return new SemanticChecklist(
            schemaVersion, planId, planVersion, checkpointId, attempt, criteria, body);
    }

    private static void RequireExact(string filePath, string field, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"'{field}' must be exactly '{expected}' (the value in the request), got '{actual}'");
    }
}
