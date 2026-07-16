using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Strict parser for reconciliation records. Two entry points for the two places
/// the schema appears: <see cref="Parse"/> reads canonical records
/// (<c>&lt;checkpoint-id&gt;.attempt-&lt;n&gt;.md</c> under a plan's
/// <c>reconciliation/</c> directory, Orca-written, file name must agree with the
/// frontmatter) and <see cref="ParseStaging"/> reads the Reconciler's raw staging output,
/// where the file name is Orca-chosen but every identity field must exactly match the
/// request — a role can never answer for the wrong checkpoint or attempt.</summary>
public static class ReconciliationRecordParser
{
    public static ReconciliationRecord Parse(string fileText, string filePath)
    {
        var record = ParseCore(fileText, filePath);

        var expectedName = $"{record.CheckpointId}.attempt-{record.Attempt}.md";
        if (!string.Equals(Path.GetFileName(filePath), expectedName, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"file name must be '{expectedName}' to match its frontmatter — if this file was created " +
                "by a sync tool (a '.sync-conflict-' or ' (1)' copy), resolve the conflict and delete the copy");

        return record;
    }

    public static ReconciliationRecord ParseStaging(string fileText, string filePath, AttemptIdentity expected)
    {
        var record = ParseCore(fileText, filePath);
        RequireExact(filePath, "plan_id", expected.PlanId, record.PlanId);
        RequireExact(filePath, "plan_version", expected.PlanVersion.ToString(), record.PlanVersion.ToString());
        RequireExact(filePath, "checkpoint_id", expected.CheckpointId, record.CheckpointId);
        RequireExact(filePath, "attempt", expected.Attempt.ToString(), record.Attempt.ToString());
        return record;
    }

    private static ReconciliationRecord ParseCore(string fileText, string filePath)
    {
        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var record = new ReconciliationRecord(
            fm.RequireInt("schema_version"),
            fm.RequireString("plan_id"),
            fm.RequireInt("plan_version"),
            fm.RequireString("checkpoint_id"),
            fm.RequireInt("attempt"),
            SchemaEnums.ParseReconciliationVerdict(fm.RequireString("implements_plan"), filePath),
            fm.RequireStringListAllowEmpty("missing"),
            fm.RequireStringListAllowEmpty("extra"),
            body);
        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        ReconciliationRules.Validate(record, filePath);
        return record;
    }

    private static void RequireExact(string filePath, string field, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath,
                $"'{field}' must be exactly '{expected}' (the value in the request), got '{actual}'");
    }
}
