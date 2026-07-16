namespace Orca.Core.Domain;

/// <param name="Model">Optional per-checkpoint override of <c>roles.implementer.model</c>
/// (SUG-05) — escalate a checkpoint the Planner judges unusually hard, or downgrade one
/// it judges trivial, without changing every other checkpoint's tier.</param>
/// <param name="MaxBudgetUsd">Optional per-checkpoint override of
/// <c>roles.implementer.maxBudgetUsd</c>, same rationale as <see cref="Model"/>.</param>
/// <param name="DependsOn">SUG-03 stage A: the checkpoint ids this one requires complete
/// first. Null means the default the flat list always implied — the previous checkpoint
/// (nothing for the first); an explicit empty list means "no dependencies" (a parallel
/// root). Ids must name earlier-listed checkpoints, so numbering stays presentation
/// order while <c>depends_on</c> is the truth — see <see cref="CheckpointGraph"/>.</param>
public sealed record Checkpoint(
    string Id,
    string Description,
    CheckpointStatus Status,
    IReadOnlyList<string> AcceptanceCriteria,
    string? Model = null,
    decimal? MaxBudgetUsd = null,
    IReadOnlyList<string>? DependsOn = null);
