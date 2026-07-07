using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Writes verification and reconciliation results back into the vault. Records
/// are append-only evidence: one file per attempt, never overwritten, written atomically
/// (temp file + rename) so a crash can't leave a half-written record for the next prompt
/// assembly to trip over.</summary>
public sealed class VaultWriter(VaultLayout layout)
{
    /// <summary>Validates, serializes, and atomically writes one verification record.
    /// Returns the path written.</summary>
    public string WriteVerificationRecord(VerificationRecord record)
    {
        var path = layout.VerificationFile(record.PlanId, record.CheckpointId, record.Attempt);
        VerificationRules.Validate(record, path);
        WriteRecord(path, layout.VerificationDirectory(record.PlanId),
            VerificationRecordSerializer.Serialize(record), "verification record");
        return path;
    }

    /// <summary>Validates, serializes, and atomically writes one reconciliation record.
    /// Returns the path written.</summary>
    public string WriteReconciliationRecord(ReconciliationRecord record)
    {
        var path = layout.ReconciliationFile(record.PlanId, record.CheckpointId, record.Attempt);
        ReconciliationRules.Validate(record, path);
        WriteRecord(path, layout.ReconciliationDirectory(record.PlanId),
            ReconciliationRecordSerializer.Serialize(record), "reconciliation record");
        return path;
    }

    private static void WriteRecord(string path, string directory, string text, string kind)
    {
        if (File.Exists(path))
            throw new VaultException(
                $"refusing to overwrite existing {kind}: {path} (records are append-only; use the next attempt number)");

        Directory.CreateDirectory(directory);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, text);
            File.Move(tempPath, path, overwrite: false);
        }
        catch (IOException ex)
        {
            throw new VaultException($"failed to write {kind}: {path}", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
