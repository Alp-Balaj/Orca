using Orca.Core.Domain;

namespace Orca.Core.Vault;

/// <summary>The vault's answer to "what attempt comes next", derived from durable
/// evidence only: one past the highest attempt recorded by either the Verifier or the
/// Reconciler. A checkpoint whose only durable evidence is a reconciliation record
/// (verification never ran, or its record predates the write) must still advance past
/// it — pass/fail authority stays with verification, but attempt numbering must not
/// repeat a number the vault already has evidence for.</summary>
public static class VaultAttempts
{
    public static int NextAttempt(
        IReadOnlyList<VerificationRecord> verificationRecords,
        IReadOnlyList<ReconciliationRecord> reconciliationRecords)
    {
        var maxAttempt = 0;
        foreach (var record in verificationRecords)
            maxAttempt = Math.Max(maxAttempt, record.Attempt);
        foreach (var record in reconciliationRecords)
            maxAttempt = Math.Max(maxAttempt, record.Attempt);
        return maxAttempt + 1;
    }
}
