namespace Orca.Core.Domain;

/// <summary>A parsed plan file: the machine-readable frontmatter spine plus the opaque
/// markdown body (free-form prose for the human, never interpreted by Orca).</summary>
public sealed record Plan(
    int SchemaVersion,
    string PlanId,
    int PlanVersion,
    string Title,
    PlanStatus Status,
    string TargetRepo,
    string BaseBranch,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    PlanningInfo Planning,
    IReadOnlyList<Checkpoint> Checkpoints,
    string Body);
