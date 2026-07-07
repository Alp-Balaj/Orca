namespace Orca.Core.Domain;

/// <summary>The identity of one checkpoint attempt — what a role's §5.4 output file must
/// echo exactly, so a role can never answer for the wrong plan, checkpoint, or attempt.</summary>
public sealed record AttemptIdentity(
    string PlanId,
    int PlanVersion,
    string CheckpointId,
    int Attempt);

/// <summary>The Verifier's §5.4 output block, parsed from its staging file: the identity
/// header plus exactly one entry per acceptance criterion. Never stored as-is — Orca
/// composes the final <see cref="VerificationRecord"/> from these results plus the
/// machine-run gate outcomes, so the model never transcribes a machine fact.</summary>
public sealed record SemanticChecklist(
    int SchemaVersion,
    string PlanId,
    int PlanVersion,
    string CheckpointId,
    int Attempt,
    IReadOnlyList<SemanticCriterionResult> Criteria,
    string Body);
