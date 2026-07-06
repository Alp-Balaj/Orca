namespace Orca.Core.Domain;

/// <summary>Lifecycle of a plan as recorded in vault frontmatter. Transitions are enforced
/// by Orca's orchestration logic; the parser only validates that the value is legal.</summary>
public enum PlanStatus
{
    Draft,
    Approved,
    InProgress,
    Blocked,
    Complete,
    Abandoned,
}
