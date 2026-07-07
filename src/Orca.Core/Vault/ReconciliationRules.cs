using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Consistency rules for reconciliation records, enforced on the Reconciler's
/// staging output, on write, and on read — an inconsistent record can neither enter nor
/// leave the vault. A conformance claim must be self-consistent: <c>full</c> cannot name
/// missing items, and <c>partial</c>/<c>none</c> must say what is missing — a bare
/// nonconformance verdict is useless as Reflexion feedback.</summary>
public static class ReconciliationRules
{
    public static void Validate(ReconciliationRecord record, string context)
    {
        if (record.SchemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(context,
                $"unsupported schema_version {record.SchemaVersion}; this build of Orca supports version {VaultSchema.Version}");
        if (!SchemaRegexes.PlanId().IsMatch(record.PlanId))
            throw new VaultSchemaException(context, $"plan_id '{record.PlanId}' must match {SchemaRegexes.PlanId()}");
        if (!SchemaRegexes.CheckpointId().IsMatch(record.CheckpointId))
            throw new VaultSchemaException(context, $"checkpoint_id '{record.CheckpointId}' must match {SchemaRegexes.CheckpointId()}");
        if (record.PlanVersion < 1)
            throw new VaultSchemaException(context, "plan_version must be >= 1");
        if (record.Attempt < 1)
            throw new VaultSchemaException(context, "attempt must be >= 1");

        ValidateItems(record.Missing, "missing", context);
        ValidateItems(record.Extra, "extra", context);

        if (record.ImplementsPlan == ReconciliationVerdict.Full && record.Missing.Count > 0)
            throw new VaultSchemaException(context,
                "implements_plan 'full' is inconsistent with a non-empty missing list — a full implementation lacks nothing");
        if (record.ImplementsPlan != ReconciliationVerdict.Full && record.Missing.Count == 0)
            throw new VaultSchemaException(context,
                $"implements_plan '{SchemaEnums.ToYaml(record.ImplementsPlan)}' requires a non-empty missing list — say what the diff lacks");
    }

    private static void ValidateItems(IReadOnlyList<string> items, string key, string context)
    {
        if (items.Any(string.IsNullOrWhiteSpace))
            throw new VaultSchemaException(context, $"'{key}' items must be non-empty strings");
    }
}
