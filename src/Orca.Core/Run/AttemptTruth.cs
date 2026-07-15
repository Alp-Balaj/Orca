using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>Derives the next attempt number from every independent witness, never trusting
/// a single one: run state can be hand-edited or stale, the vault can hold evidence for an
/// attempt whose state update never landed, and a crashed invocation can leave a run log
/// with no matching vault record. Each witness only ever pushes the number forward, so the
/// derived attempt is the maximum of all three — a pure function of the evidence handed to
/// it, with no filesystem access and no mutation of <see cref="RunState"/> itself.</summary>
public static class AttemptTruth
{
    /// <param name="stateAttempt">The attempt number <see cref="RunState"/> would produce
    /// on its own (e.g. <c>state.Attempt + 1</c> for a matching checkpoint, or 1 otherwise).</param>
    /// <param name="verificationRecords">This checkpoint's verification records, any order.</param>
    /// <param name="reconciliationRecords">This checkpoint's reconciliation records, any order.</param>
    /// <param name="runLogAttempts">Attempt numbers that already have a run log under
    /// <c>.orca/runs/&lt;plan&gt;/</c> for this checkpoint.</param>
    public static int NextAttempt(
        int stateAttempt,
        IReadOnlyList<VerificationRecord> verificationRecords,
        IReadOnlyList<ReconciliationRecord> reconciliationRecords,
        IReadOnlySet<int> runLogAttempts)
    {
        var vaultAttempt = VaultAttempts.NextAttempt(verificationRecords, reconciliationRecords);
        var runLogAttempt = NextFreeSequence.Find(1, runLogAttempts.Contains);

        return Math.Max(stateAttempt, Math.Max(vaultAttempt, runLogAttempt));
    }
}
