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

    /// <summary>The human's one-line reason for rejecting a prior attempt (SUG-11 §4) —
    /// the strongest feedback signal in the system, especially when the records were all
    /// green and the rejection happened on diff review.</summary>
    public HumanRejection? Rejection { get; init; }

    public bool IsEmpty => Verification is null && Reconciliation is null && Rejection is null;
}

/// <summary>A human's rejection reason for one attempt, read back from the vault's
/// <c>rejection/</c> notes.</summary>
public sealed record HumanRejection(int Attempt, string Note);

/// <summary>The exact gate argvs (<c>commands.build</c> / <c>commands.test</c>) so the
/// Implementer can be told what mechanically decides its fate (SUG-11 §1) — its allowlist
/// includes both precisely so it can pre-run them.</summary>
public sealed record GateCommands(IReadOnlyList<string> Build, IReadOnlyList<string> Test);

/// <summary>Assembles the Implementer's prompt fresh from the vault every time (spec §8:
/// plan-file section + acceptance criteria for that checkpoint only, plus the child
/// note's detailed instructions when one exists, plus — from the second attempt on — the
/// previous attempt's verification findings). Deterministic LF output; the SHA-256 goes
/// into run state so behaviour changes stay attributable — the Implementer has no prompt
/// file in <c>prompts/</c>, so the assembled text is what gets hashed.</summary>
public static class CheckpointPrompt
{
    public static string Assemble(
        Plan plan, Checkpoint checkpoint, ChildNote? childNote, AttemptFeedback? feedback = null,
        GateCommands? gates = null, int attempt = 1)
    {
        if (!plan.Checkpoints.Contains(checkpoint))
            throw new ArgumentException(
                $"checkpoint '{checkpoint.Id}' is not part of plan '{plan.PlanId}'", nameof(checkpoint));

        var sb = new StringBuilder();
        sb.Append($"# Orca checkpoint: {plan.PlanId}/{checkpoint.Id}\n\n");
        sb.Append("You are the Implementer for exactly one checkpoint of an approved plan. Work only\n");
        // SUG-11 §5: truthful attempt framing — "a branch created for this checkpoint" is
        // wrong from attempt 2 on (the branch is reused, possibly with prior undiscarded
        // changes still in the tree).
        if (attempt <= 1)
        {
            sb.Append("on this checkpoint, in the current working directory (the target repository, on a\n");
            sb.Append("branch created for this checkpoint). ");
        }
        else
        {
            sb.Append("on this checkpoint, in the current working directory (the target repository, on\n");
            sb.Append($"the branch this checkpoint has used since attempt 1 — this is attempt {attempt}, and\n");
            sb.Append("the tree may still hold changes from a prior attempt that was not discarded;\n");
            sb.Append("build on or correct them as the criteria demand). ");
        }
        sb.Append("Do not run git write commands; Orca owns all\n");
        sb.Append("branching and committing. Change nothing this checkpoint does not ask for —\n");
        sb.Append("unrequested changes are flagged in review and count against the attempt. When the\n");
        sb.Append("acceptance criteria are met, stop and summarize what you changed. If a criterion\n");
        sb.Append("cannot be met, stop early and state exactly why in your final summary — that\n");
        sb.Append("summary is shown to the human deciding this attempt. If you need throwaway scratch\n");
        sb.Append("work — a standalone script to probe an API, a one-off dump — put it under\n");
        sb.Append(".orca/scratch/, which is already gitignored and never reaches the diff; do not\n");
        sb.Append("leave scratch files anywhere else, and delete them once you're done either way.\n\n");
        sb.Append(SpecSections(checkpoint, childNote));

        if (gates is not null)
            AppendGates(sb, gates);

        if (feedback is { IsEmpty: false })
            AppendFeedback(sb, feedback);

        return sb.ToString();
    }

    /// <summary>SUG-11 §1: the Implementer is told exactly what decides its fate — both
    /// gate argvs verbatim, and that it should pre-run them (its allowlist covers them)
    /// rather than burn a whole attempt discovering a red gate.</summary>
    private static void AppendGates(StringBuilder sb, GateCommands gates)
    {
        sb.Append("## Gates\n\n");
        sb.Append("These exact commands verify this checkpoint mechanically; either exiting non-zero\n");
        sb.Append("fails the attempt automatically:\n\n");
        sb.Append($"- build: {string.Join(' ', gates.Build)}\n");
        sb.Append($"- tests: {string.Join(' ', gates.Test)}\n\n");
        sb.Append("Run both before you stop, and fix what they surface.\n\n");
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

        // SUG-11 §4: the framing must match the evidence. When the human rejected an
        // attempt whose records were green, "the findings below are why it did not stand"
        // over all-passing findings is actively misleading — the human's reason is why.
        var rejectedDespitePassing = feedback.Rejection is not null
            && feedback.Verification is { Verdict: VerificationVerdict.Pass };
        if (rejectedDespitePassing)
        {
            sb.Append("A previous attempt at this checkpoint passed verification but was rejected by\n");
            sb.Append("the human on review — their reason below is why it did not stand. Address it —\n");
            sb.Append("the checkpoint specification above is unchanged.\n");
        }
        else
        {
            sb.Append("A previous attempt at this checkpoint was verified; the findings below are why it\n");
            sb.Append("did not stand. Address them — the checkpoint specification above is unchanged.\n");
        }

        if (feedback.Rejection is { } rejection)
        {
            sb.Append($"\n### Human rejection of attempt {rejection.Attempt}\n\n");
            sb.Append(rejection.Note.ReplaceLineEndings("\n").TrimEnd()).Append('\n');
        }

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
