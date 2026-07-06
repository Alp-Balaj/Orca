namespace Orca.Core.Domain;

/// <summary>Outcome of a deterministic gate (build or tests). Produced by running the
/// configured command — never by asking a model for a fact the machine can state.</summary>
public enum GateOutcome
{
    Pass,
    Fail,
}

public enum SemanticVerdict
{
    Pass,
    Fail,
    Unclear,
}

public enum VerificationVerdict
{
    Pass,
    Fail,
}

public sealed record GateResults(GateOutcome Build, GateOutcome Tests);

public sealed record SemanticCriterionResult(string Criterion, SemanticVerdict Verdict, string? Note);

/// <summary>One verification attempt for one checkpoint, stored append-only in the vault.
/// Semantic results exist exactly when both gates pass; the overall verdict is derived from
/// the recorded results, and <c>VerificationRules</c> rejects inconsistent records on both
/// read and write. The body holds relevant excerpts only — full transcripts stay in the
/// gitignored .orca/runs/ directory.</summary>
public sealed record VerificationRecord(
    int SchemaVersion,
    string PlanId,
    int PlanVersion,
    string CheckpointId,
    int Attempt,
    DateTimeOffset Timestamp,
    GateResults Gates,
    IReadOnlyList<SemanticCriterionResult>? Semantic,
    VerificationVerdict Verdict,
    string Body);
