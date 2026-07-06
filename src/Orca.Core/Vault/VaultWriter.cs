using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>Writes verification results back into the vault. Records are append-only
/// evidence: one file per attempt, never overwritten, written atomically (temp file +
/// rename) so a crash can't leave a half-written record for the next prompt assembly to
/// trip over.</summary>
public sealed class VaultWriter(VaultLayout layout)
{
    /// <summary>Validates, serializes, and atomically writes one verification record.
    /// Returns the path written.</summary>
    public string WriteVerificationRecord(VerificationRecord record)
    {
        var path = layout.VerificationFile(record.PlanId, record.CheckpointId, record.Attempt);
        VerificationRules.Validate(record, path);

        if (File.Exists(path))
            throw new VaultException(
                $"refusing to overwrite existing verification record: {path} (records are append-only; use the next attempt number)");

        Directory.CreateDirectory(layout.VerificationDirectory(record.PlanId));

        var text = VerificationRecordSerializer.Serialize(record);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, text);
            File.Move(tempPath, path, overwrite: false);
        }
        catch (IOException ex)
        {
            throw new VaultException($"failed to write verification record: {path}", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        return path;
    }
}
