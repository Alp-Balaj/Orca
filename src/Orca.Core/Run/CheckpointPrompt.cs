using System.Security.Cryptography;
using System.Text;
using Orca.Core.Domain;

namespace Orca.Core.Run;

/// <summary>Assembles the Implementer's prompt fresh from the vault every time (spec §8:
/// plan-file section + acceptance criteria for that checkpoint only, plus the child
/// note's detailed instructions when one exists). Deterministic LF output; the SHA-256
/// goes into run state so behaviour changes stay attributable — the Implementer has no
/// prompt file in <c>prompts/</c>, so the assembled text is what gets hashed.</summary>
public static class CheckpointPrompt
{
    public static string Assemble(Plan plan, Checkpoint checkpoint, ChildNote? childNote)
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
        sb.Append("what you changed.\n\n");
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

    public static string Sha256(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
}
