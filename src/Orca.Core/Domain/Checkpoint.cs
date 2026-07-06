namespace Orca.Core.Domain;

public sealed record Checkpoint(
    string Id,
    string Description,
    CheckpointStatus Status,
    IReadOnlyList<string> AcceptanceCriteria);
