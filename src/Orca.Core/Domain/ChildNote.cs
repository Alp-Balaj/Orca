namespace Orca.Core.Domain;

/// <summary>A checkpoint's child note: the detailed instructions that get assembled into
/// the Implementer prompt. The body is the payload; the tiny frontmatter exists purely so
/// the wrong instructions can never be silently assembled for the wrong checkpoint.</summary>
public sealed record ChildNote(
    int SchemaVersion,
    string PlanId,
    string CheckpointId,
    string Body);
