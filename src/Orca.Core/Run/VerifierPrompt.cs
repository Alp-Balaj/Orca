using System.Text;
using Orca.Core.Domain;

namespace Orca.Core.Run;

/// <summary>Assembles the Verifier's prompt: the versioned role prompt (embedded from
/// <c>/prompts/verifier.md</c>), the identity block, the acceptance criteria to judge,
/// the deterministic-gate evidence, and the diff — spec-exact inputs for the semantic
/// layer (§8: "the diff, the deterministic-gate output, and the checkpoint's acceptance
/// criteria"; deliberately no child note).</summary>
public static class VerifierPrompt
{
    private const string ResourceName = "Orca.Core.Prompts.verifier.md";

    public static string RoleText { get; } = RolePromptResource.Read(ResourceName);

    /// <summary>Hash of the role prompt file itself — the attribution contract.</summary>
    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);

    public static string Assemble(
        AttemptIdentity identity,
        string relativeStagingPath,
        IReadOnlyList<string> acceptanceCriteria,
        GateCommandResult buildGate,
        GateCommandResult testsGate,
        string diff,
        string? previousError = null)
    {
        var sb = new StringBuilder();
        sb.Append(RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        ReconcilerPrompt.AppendIdentity(sb, identity);
        sb.Append($"- write the checklist to this path, relative to your working directory: {relativeStagingPath}\n");
        sb.Append("- if a file already exists at that path (a crashed earlier run), replace it\n");
        // BF-11 §3: the model's only work is verdicts and notes — the criterion lines
        // below are pre-escaped YAML, ready to copy verbatim into the checklist, so a
        // criterion starting with "1. " or containing quotes can't invite mis-stripping.
        sb.Append("\n### Acceptance criteria to judge (copy each `criterion:` line verbatim, in this order)\n\n");
        foreach (var criterion in acceptanceCriteria)
            sb.Append($"  - criterion: {Orca.Core.Vault.YamlEmit.Quote(criterion)}\n");
        sb.Append("\n## Deterministic gate evidence (both passed)\n\n");
        AppendGate(sb, buildGate);
        AppendGate(sb, testsGate);
        sb.Append("## Diff\n\n");
        ReconcilerPrompt.AppendFenced(sb, "diff", diff);

        if (previousError is not null)
            ReconcilerPrompt.AppendRetry(sb, relativeStagingPath, previousError);

        return sb.ToString();
    }

    private static void AppendGate(StringBuilder sb, GateCommandResult gate)
    {
        sb.Append($"### {gate.Gate} — `{string.Join(' ', gate.Command)}` ");
        sb.Append($"(exit {gate.ExitCode}, {gate.Elapsed.TotalSeconds:0.0}s; last {GateRunner.TailLineCount} lines)\n\n");
        ReconcilerPrompt.AppendFenced(sb, "text", gate.OutputTail);
        sb.Append('\n');
    }
}
