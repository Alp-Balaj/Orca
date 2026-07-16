namespace Orca.Core.Domain;

/// <summary>The Scout's repository audit (SUG-01): a durable, human-reviewable artifact
/// written before planning spends anything, and the only repository knowledge the
/// (deliberately repo-blind) Planner receives. The frontmatter exists for cross-checking;
/// the body is what the human reviews and the Planner prompt embeds.</summary>
public sealed record AuditNote(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset Created,
    string Body);
