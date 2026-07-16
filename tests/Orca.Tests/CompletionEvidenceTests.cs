using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The pure core of SUG-17's mark-complete guard: a checkpoint counts as
/// complete-by-evidence exactly when its branch head is an Orca approval commit or its
/// latest verification record passes — the same two signals CheckpointRunner.Prepare's
/// guard reads in the other direction.</summary>
public sealed class CompletionEvidenceTests
{
    private static VerificationRecord Verification(int attempt, VerificationVerdict verdict) => new(
        1, "recipe-tagging", 1, "02-tag-endpoint", attempt,
        new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero),
        new GateResults(GateOutcome.Pass, verdict == VerificationVerdict.Pass ? GateOutcome.Pass : GateOutcome.Fail),
        verdict == VerificationVerdict.Pass ? [] : null,
        verdict,
        "body");

    [Fact]
    public void No_branch_and_no_records_is_no_evidence()
    {
        var evidence = CompletionEvidence.From(
            "recipe-tagging", "02-tag-endpoint", branchExists: false, branchHeadSubject: null, []);

        Assert.False(evidence.Any);
        Assert.Null(evidence.Description);
        Assert.Equal("orca/recipe-tagging/02-tag-endpoint", evidence.Branch);
    }

    [Fact]
    public void An_orca_approval_commit_at_the_branch_head_is_evidence()
    {
        var evidence = CompletionEvidence.From(
            "recipe-tagging", "02-tag-endpoint", branchExists: true,
            branchHeadSubject: "orca: recipe-tagging/02-tag-endpoint attempt 2", []);

        Assert.True(evidence.Any);
        Assert.True(evidence.ApprovalCommitAtHead);
        Assert.Contains("approval commit", evidence.Description);
    }

    [Fact]
    public void An_arbitrary_commit_at_the_branch_head_is_not_evidence()
    {
        var evidence = CompletionEvidence.From(
            "recipe-tagging", "02-tag-endpoint", branchExists: true,
            branchHeadSubject: "wip: half-finished endpoint", []);

        Assert.False(evidence.Any);
    }

    [Fact]
    public void A_passing_latest_verification_is_evidence_and_names_the_attempt()
    {
        var evidence = CompletionEvidence.From(
            "recipe-tagging", "02-tag-endpoint", branchExists: false, branchHeadSubject: null,
            [Verification(1, VerificationVerdict.Fail), Verification(2, VerificationVerdict.Pass)]);

        Assert.True(evidence.Any);
        Assert.Equal(2, evidence.PassingVerificationAttempt);
        Assert.Contains("attempt 2", evidence.Description);
    }

    [Fact]
    public void A_failing_latest_verification_is_not_evidence_even_after_an_earlier_pass()
    {
        var evidence = CompletionEvidence.From(
            "recipe-tagging", "02-tag-endpoint", branchExists: false, branchHeadSubject: null,
            [Verification(1, VerificationVerdict.Pass), Verification(2, VerificationVerdict.Fail)]);

        Assert.False(evidence.Any);
    }
}
