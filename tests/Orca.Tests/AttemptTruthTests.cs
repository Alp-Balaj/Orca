using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

public sealed class AttemptTruthTests
{
    private static VerificationRecord Verification(int attempt) => new(
        SchemaVersion: 1,
        PlanId: "recipe-tagging",
        PlanVersion: 1,
        CheckpointId: "01-tag-entity",
        Attempt: attempt,
        Timestamp: new DateTimeOffset(2026, 7, 6, 15, 20, 0, TimeSpan.Zero),
        Gates: new GateResults(GateOutcome.Pass, GateOutcome.Fail),
        Semantic: null,
        Verdict: VerificationVerdict.Fail,
        Body: "Failing tests.\n");

    private static ReconciliationRecord Reconciliation(int attempt) => new(
        SchemaVersion: 1,
        PlanId: "recipe-tagging",
        PlanVersion: 1,
        CheckpointId: "01-tag-entity",
        Attempt: attempt,
        ImplementsPlan: ReconciliationVerdict.Full,
        Missing: [],
        Extra: [],
        Body: "Implements the plan.\n");

    private static readonly IReadOnlySet<int> NoRunLogs = new HashSet<int>();

    [Fact]
    public void Derives_from_verification_records_alone()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [Verification(1), Verification(2)],
            reconciliationRecords: [],
            runLogAttempts: NoRunLogs);

        Assert.Equal(3, attempt);
    }

    [Fact]
    public void Derives_from_reconciliation_records_alone()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [],
            reconciliationRecords: [Reconciliation(1), Reconciliation(2)],
            runLogAttempts: NoRunLogs);

        Assert.Equal(3, attempt);
    }

    [Fact]
    public void Takes_the_higher_of_verification_and_reconciliation_when_both_are_present()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [Verification(1), Verification(2)],
            reconciliationRecords: [Reconciliation(1), Reconciliation(2), Reconciliation(3)],
            runLogAttempts: NoRunLogs);

        Assert.Equal(4, attempt);
    }

    [Fact]
    public void Falls_back_to_the_state_derived_value_when_no_evidence_exists()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [],
            reconciliationRecords: [],
            runLogAttempts: NoRunLogs);

        Assert.Equal(1, attempt);
    }

    [Fact]
    public void A_state_value_higher_than_any_evidence_still_wins()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 5,
            verificationRecords: [Verification(1)],
            reconciliationRecords: [],
            runLogAttempts: NoRunLogs);

        Assert.Equal(5, attempt);
    }

    [Fact]
    public void An_orphaned_run_log_with_no_matching_record_still_pushes_the_attempt_forward()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [],
            reconciliationRecords: [],
            runLogAttempts: new HashSet<int> { 1 });

        Assert.Equal(2, attempt);
    }

    [Fact]
    public void A_gap_in_the_run_log_numbering_yields_the_first_free_attempt_not_the_maximum_plus_one()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [],
            reconciliationRecords: [],
            runLogAttempts: new HashSet<int> { 1, 2, 4 });

        Assert.Equal(3, attempt);
    }

    [Fact]
    public void Higher_vault_evidence_overrides_a_lower_run_log_derived_value()
    {
        var attempt = AttemptTruth.NextAttempt(
            stateAttempt: 1,
            verificationRecords: [Verification(1), Verification(2), Verification(3), Verification(4)],
            reconciliationRecords: [],
            runLogAttempts: new HashSet<int> { 1, 2 });

        Assert.Equal(5, attempt);
    }
}
