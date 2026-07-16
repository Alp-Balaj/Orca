using System.Security.Cryptography;
using System.Text;
using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>The prior findings a later attempt is prompted with — the Reflexion loop's
/// memory, read from the vault, never from conversation state (spec §8). Each record is
/// the latest of its kind for the checkpoint; they may belong to different attempts
/// (a gate-failed attempt has no reconciliation).</summary>
public sealed record AttemptFeedback
{
    public required VerificationRecord? Verification { get; init; }
    public required ReconciliationRecord? Reconciliation { get; init; }

    public bool IsEmpty => Verification is null && Reconciliation is null;
}

/// <summary>Assembles the Implementer's prompt fresh from the vault every time (spec §8:
/// plan-file section + acceptance criteria for that checkpoint only, plus the child
/// note's detailed instructions when one exists, plus — from the second attempt on — the
/// previous attempt's verification findings). Deterministic LF output; the SHA-256 goes
/// into run state so behaviour changes stay attributable — the Implementer has no prompt
/// file in <c>prompts/</c>, so the assembled text is what gets hashed.</summary>
public static class CheckpointPrompt
{
    public static string Assemble(
        Plan plan, Checkpoint checkpoint, ChildNote? childNote, AttemptFeedback? feedback = null)
    {
        if (!plan.Checkpoints.Contains(checkpoint))
            throw new ArgumentException(
                $"checkpoint '{checkpoint.Id}' is not part of plan '{plan.PlanId}'", nameof(checkpoint));

        var sb = new StringBuilder();
        sb.Append($"# Orca checkpoint: {plan.PlanId}/{checkpoint.Id}\n\n");
        sb.Append("You are the Implementer for exactly one checkpoint of an approved plan. Work only\n");
        sb.Append("on this checkpoint, in the current working directory (the target repository, on a\n");
        sb.Append("branch created for this checkpoint). Do not run git write commands; Orca owns all\n");
        sb.Append("branching and committing. When the acceptance criteria are met, stop and summarize\n");
        sb.Append("what you changed. If you need throwaway scratch work — a standalone script to probe\n");
        sb.Append("an API, a one-off dump — put it under .orca/scratch/, which is already gitignored\n");
        sb.Append("and never reaches the diff; do not leave scratch files anywhere else, and delete\n");
        sb.Append("them once you're done either way.\n\n");
        sb.Append(SpecSections(checkpoint, childNote));

        if (feedback is { IsEmpty: false })
            AppendFeedback(sb, feedback);

        return sb.ToString();
    }

    /// <summary>The checkpoint's plan section — what the Implementer is asked to do and
    /// what the Reconciler judges the diff against. One rendering for both, so they can
    /// never drift apart.</summary>
    internal static string SpecSections(Checkpoint checkpoint, ChildNote? childNote)
    {
        var sb = new StringBuilder();
        sb.Append($"## Checkpoint\n\n{checkpoint.Id}: {checkpoint.Description}\n\n");
        sb.Append("## Acceptance criteria\n\n");
        foreach (var criterion in checkpoint.AcceptanceCriteria)
            sb.Append($"- {criterion}\n");
        sb.Append('\n');
        sb.Append("## Detailed instructions\n\n");
        sb.Append(childNote is null
            ? "(none — the acceptance criteria above are the full specification)\n"
            : childNote.Body.ReplaceLineEndings("\n").TrimEnd() + "\n");
        return sb.ToString();
    }

    private static void AppendFeedback(StringBuilder sb, AttemptFeedback feedback)
    {
        sb.Append("\n## Previous attempt findings\n\n");
        sb.Append("A previous attempt at this checkpoint was verified; the findings below are why it\n");
        sb.Append("did not stand. Address them — the checkpoint specification above is unchanged.\n");

        if (feedback.Verification is { } record)
        {
            sb.Append($"\n### Verification of attempt {record.Attempt} — verdict: {SchemaEnums.ToYaml(record.Verdict)}\n\n");
            sb.Append($"- build gate: {SchemaEnums.ToYaml(record.Gates.Build)}\n");
            sb.Append($"- tests gate: {SchemaEnums.ToYaml(record.Gates.Tests)}\n");
            if (record.Semantic is { } semantic)
            {
                sb.Append("- criteria:\n");
                foreach (var item in semantic)
                {
                    sb.Append($"  - [{SchemaEnums.ToYaml(item.Verdict)}] {item.Criterion}");
                    if (item.Note is not null)
                        sb.Append($" — {item.Note}");
                    sb.Append('\n');
                }
            }

            if (record.Body.Trim().Length > 0)
            {
                sb.Append('\n');
                ReconcilerPrompt.AppendFenced(sb, "text", record.Body);
            }
        }

        if (feedback.Reconciliation is { } reconciliation)
        {
            sb.Append($"\n### Reconciliation of attempt {reconciliation.Attempt} — implements_plan: {SchemaEnums.ToYaml(reconciliation.ImplementsPlan)}\n\n");
            AppendItems(sb, "missing", reconciliation.Missing);
            AppendItems(sb, "extra", reconciliation.Extra);
            if (reconciliation.Body.Trim().Length > 0)
            {
                sb.Append('\n');
                ReconcilerPrompt.AppendFenced(sb, "text", reconciliation.Body);
            }
        }
    }

    private static void AppendItems(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            sb.Append($"- {label}: (none)\n");
            return;
        }

        sb.Append($"- {label}:\n");
        foreach (var item in items)
            sb.Append($"  - {item}\n");
    }

    public static string Sha256(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
}
