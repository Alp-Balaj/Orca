namespace Orca.Core.Domain;

/// <summary>The Reconciler's answer to "does this diff implement what the plan says".
/// Wire spellings are <c>full|partial|none</c> rather than the spec's literal
/// yes/no/partial: unquoted <c>yes</c>/<c>no</c> are YAML 1.1 booleans and render as
/// checkboxes in Obsidian's properties view (owner-approved deviation, 2026-07-07).</summary>
public enum ReconciliationVerdict
{
    Full,
    Partial,
    None,
}

/// <summary>One reconciliation attempt for one checkpoint, stored append-only in the
/// vault beside the verification records. Advisory by design: it informs the human gate
/// and the next attempt's prompt, while pass/fail authority stays with the verification
/// record. <c>Missing</c> is what the plan asks for and the diff lacks; <c>Extra</c> is
/// what the diff does beyond the plan (legal under any verdict — extras are flagged, not
/// judged).</summary>
public sealed record ReconciliationRecord(
    int SchemaVersion,
    string PlanId,
    int PlanVersion,
    string CheckpointId,
    int Attempt,
    ReconciliationVerdict ImplementsPlan,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    string Body);
