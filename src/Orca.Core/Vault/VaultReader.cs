using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Reads vault files through the strict parsers and refuses every cross-reference
/// mismatch — the vault is the only channel between agent roles, so what leaves this class
/// must be exactly what the plan declared.</summary>
public sealed class VaultReader(VaultLayout layout)
{
    public Plan ReadPlan(string planId)
    {
        var path = layout.PlanFile(planId);
        if (!File.Exists(path))
            throw new VaultException($"plan file not found: {path}");
        return PlanParser.Parse(File.ReadAllText(path), path);
    }

    public ChildNote ReadChildNote(Plan plan, string checkpointId)
    {
        if (!plan.Checkpoints.Any(c => string.Equals(c.Id, checkpointId, StringComparison.Ordinal)))
            throw new VaultException($"plan '{plan.PlanId}' has no checkpoint '{checkpointId}'");

        var path = layout.ChildNoteFile(plan.PlanId, checkpointId);
        if (!File.Exists(path))
            throw new VaultException($"child note not found for checkpoint '{checkpointId}': {path}");

        var note = ChildNoteParser.Parse(File.ReadAllText(path), path);
        if (!string.Equals(note.PlanId, plan.PlanId, StringComparison.Ordinal))
            throw new VaultSchemaException(path,
                $"child note belongs to plan '{note.PlanId}' but was read for plan '{plan.PlanId}' — refusing to assemble mismatched instructions");

        return note;
    }

    /// <summary>All verification records for a checkpoint, ordered by attempt. Missing
    /// directory means no attempts yet — that is not an error.</summary>
    public IReadOnlyList<VerificationRecord> ReadVerificationRecords(string planId, string checkpointId)
    {
        VaultLayout.ValidatePlanId(planId);
        VaultLayout.ValidateCheckpointId(checkpointId);

        var directory = layout.VerificationDirectory(planId);
        if (!Directory.Exists(directory))
            return [];

        var records = new List<VerificationRecord>();
        foreach (var file in Directory.EnumerateFiles(directory, $"{checkpointId}.attempt-*.md"))
        {
            // BF-09 §1: the glob also matches sync-tool artifacts
            // ("….attempt-1.sync-conflict-….md"), whose parse used to brick the plan
            // until hand-deleted. Only exact record names are records; foreign files
            // are skipped here and surfaced by `orca doctor`'s sync-artifact scan.
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(file), $@"^{System.Text.RegularExpressions.Regex.Escape(checkpointId)}\.attempt-[0-9]+\.md$"))
                continue;

            var record = VerificationRecordParser.Parse(File.ReadAllText(file), file);
            if (!string.Equals(record.PlanId, planId, StringComparison.Ordinal))
                throw new VaultSchemaException(file,
                    $"verification record belongs to plan '{record.PlanId}' but sits in plan '{planId}''s folder");
            records.Add(record);
        }

        return [.. records.OrderBy(r => r.Attempt)];
    }

    /// <summary>One past the highest attempt evidenced by either record kind — see
    /// <see cref="VaultAttempts"/>.</summary>
    public int NextAttemptNumber(string planId, string checkpointId) => VaultAttempts.NextAttempt(
        ReadVerificationRecords(planId, checkpointId),
        ReadReconciliationRecords(planId, checkpointId));

    /// <summary>All reconciliation records for a checkpoint, ordered by attempt. Only
    /// Orca-validated files ever land in <c>reconciliation/</c> (raw role output stays in
    /// staging), so a parse failure here means the vault was edited by hand.</summary>
    public IReadOnlyList<ReconciliationRecord> ReadReconciliationRecords(string planId, string checkpointId)
    {
        VaultLayout.ValidatePlanId(planId);
        VaultLayout.ValidateCheckpointId(checkpointId);

        var directory = layout.ReconciliationDirectory(planId);
        if (!Directory.Exists(directory))
            return [];

        var records = new List<ReconciliationRecord>();
        foreach (var file in Directory.EnumerateFiles(directory, $"{checkpointId}.attempt-*.md"))
        {
            // BF-09 §1: the glob also matches sync-tool artifacts
            // ("….attempt-1.sync-conflict-….md"), whose parse used to brick the plan
            // until hand-deleted. Only exact record names are records; foreign files
            // are skipped here and surfaced by `orca doctor`'s sync-artifact scan.
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(file), $@"^{System.Text.RegularExpressions.Regex.Escape(checkpointId)}\.attempt-[0-9]+\.md$"))
                continue;

            var record = ReconciliationRecordParser.Parse(File.ReadAllText(file), file);
            if (!string.Equals(record.PlanId, planId, StringComparison.Ordinal))
                throw new VaultSchemaException(file,
                    $"reconciliation record belongs to plan '{record.PlanId}' but sits in plan '{planId}''s folder");
            records.Add(record);
        }

        return [.. records.OrderBy(r => r.Attempt)];
    }
}
