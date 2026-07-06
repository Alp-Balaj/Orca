using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Consistency rules for verification records, enforced on both read and write so
/// an inconsistent record can neither enter nor leave the vault. The rules encode §8's
/// two-layer discipline: semantic results exist exactly when both deterministic gates pass,
/// and the overall verdict is derived, never asserted.</summary>
public static class VerificationRules
{
    public static void Validate(VerificationRecord record, string context)
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

        var gatesPass = record.Gates.Build == GateOutcome.Pass && record.Gates.Tests == GateOutcome.Pass;
        if (!gatesPass)
        {
            if (record.Semantic is not null)
                throw new VaultSchemaException(context,
                    "semantic must be null when a deterministic gate failed — semantic verification only runs after both gates pass");
            if (record.Verdict != VerificationVerdict.Fail)
                throw new VaultSchemaException(context, "verdict must be fail when a deterministic gate failed");
            return;
        }

        if (record.Semantic is null || record.Semantic.Count == 0)
            throw new VaultSchemaException(context,
                "semantic results are required (and non-empty) when both deterministic gates pass");

        foreach (var item in record.Semantic)
        {
            if (string.IsNullOrWhiteSpace(item.Criterion))
                throw new VaultSchemaException(context, "semantic criterion must be a non-empty string");
            if (item.Note is not null && string.IsNullOrWhiteSpace(item.Note))
                throw new VaultSchemaException(context, "semantic note must be either null or non-empty");
        }

        var derived = record.Semantic.All(s => s.Verdict == SemanticVerdict.Pass)
            ? VerificationVerdict.Pass
            : VerificationVerdict.Fail;
        if (record.Verdict != derived)
            throw new VaultSchemaException(context,
                $"verdict '{SchemaEnums.ToYaml(record.Verdict)}' is inconsistent with the recorded results " +
                $"(derived '{SchemaEnums.ToYaml(derived)}'; any non-pass criterion, including unclear, fails the checkpoint)");
    }
}
