using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Strict parser for verification-record files
/// (<c>&lt;checkpoint-id&gt;.attempt-&lt;n&gt;.md</c> under a plan's <c>verification/</c>
/// directory). The file name must agree with the frontmatter — a mislabeled record is
/// drift, not a cosmetic problem.</summary>
public static class VerificationRecordParser
{
    public static VerificationRecord Parse(string fileText, string filePath)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        var planId = fm.RequireString("plan_id");
        var planVersion = fm.RequireInt("plan_version");
        var checkpointId = fm.RequireString("checkpoint_id");
        var attempt = fm.RequireInt("attempt");
        var timestamp = fm.RequireTimestamp("timestamp");

        var gatesReader = fm.RequireMapping("gates");
        var gates = new GateResults(
            SchemaEnums.ParseGateOutcome(gatesReader.RequireString("build"), filePath),
            SchemaEnums.ParseGateOutcome(gatesReader.RequireString("tests"), filePath));
        gatesReader.ThrowIfUnknownKeys();

        var workingTreeHash = fm.OptionalString("working_tree_hash");

        IReadOnlyList<SemanticCriterionResult>? semantic = null;
        if (fm.RequireSequenceOrNull("semantic") is { } semanticNodes)
        {
            var items = new List<SemanticCriterionResult>();
            for (var i = 0; i < semanticNodes.Count; i++)
            {
                var item = new MappingReader(semanticNodes[i], filePath, $"semantic[{i}]");
                items.Add(new SemanticCriterionResult(
                    item.RequireString("criterion"),
                    SchemaEnums.ParseSemanticVerdict(item.RequireString("verdict"), filePath),
                    item.OptionalString("note")));
                item.ThrowIfUnknownKeys();
            }

            semantic = items;
        }

        var verdict = SchemaEnums.ParseVerificationVerdict(fm.RequireString("verdict"), filePath);
        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        var expectedName = $"{checkpointId}.attempt-{attempt}.md";
        if (!string.Equals(Path.GetFileName(filePath), expectedName, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath, $"file name must be '{expectedName}' to match its frontmatter");

        var record = new VerificationRecord(
            schemaVersion, planId, planVersion, checkpointId, attempt, timestamp,
            gates, semantic, verdict, body, workingTreeHash);
        VerificationRules.Validate(record, filePath);
        return record;
    }
}
