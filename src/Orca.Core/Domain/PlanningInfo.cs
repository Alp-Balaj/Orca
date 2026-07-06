namespace Orca.Core.Domain;

/// <summary>How the plan was produced. Candidate fields are populated exactly when
/// <see cref="Strategy"/> is <see cref="PlanningStrategyKind.MultiCandidate"/>; the parser
/// rejects every other combination.</summary>
public sealed record PlanningInfo(
    PlanningStrategyKind Strategy,
    int? Candidates,
    int? Selected,
    string? Rationale);
