using System.Text;
using Orca.Core.Run;

namespace Orca.Core.Planning;

/// <summary>Assembles the Scout's prompt (SUG-01): the versioned role prompt file
/// (embedded at build from <c>/prompts/scout.md</c>, SHA-256 recorded in planning state)
/// plus a request block, plus — on a §5.4 Reflexion retry — the previous attempt's exact
/// parse error. The Scout's working directory is the target repository, so the audit's
/// write path is given ABSOLUTE into the vault — the one path shape that works from a
/// repo cwd (the reason the Planner itself never got repo access).</summary>
public static class ScoutPrompt
{
    private const string ResourceName = "Orca.Core.Prompts.scout.md";

    public static string RoleText { get; } = RolePromptResource.Read(ResourceName);

    /// <summary>Hash of the role prompt file itself — same attribution contract as the
    /// other roles ("did the Scout get stricter, or did prompt v2 land").</summary>
    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);

    public static string AssembleInitial(
        PlanningRequest request, string auditFilePath, string timestamp) =>
        Assemble(request, auditFilePath, timestamp, previousError: null);

    /// <summary>The retry prompt: identical request, plus the exact error. The malformed
    /// file is deliberately left in place — the model reads and corrects it rather than
    /// reconstructing from memory.</summary>
    public static string AssembleRetry(
        PlanningRequest request, string auditFilePath, string timestamp, string previousError) =>
        Assemble(request, auditFilePath, timestamp, previousError);

    private static string Assemble(
        PlanningRequest request, string auditFilePath, string timestamp, string? previousError)
    {
        var sb = new StringBuilder();
        sb.Append(RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        sb.Append($"- plan_id: {request.PlanId}\n");
        sb.Append($"- target_repo: {request.TargetRepoName} (your working directory)\n");
        sb.Append($"- created timestamp to use verbatim: {timestamp}\n");
        sb.Append($"- write the audit to this ABSOLUTE path (it is in the vault, outside this repository): {auditFilePath}\n");
        sb.Append("\n### Goal brief (scope the audit to this)\n\n");
        sb.Append(request.Goal.ReplaceLineEndings("\n").TrimEnd());
        sb.Append('\n');

        if (previousError is not null)
        {
            sb.Append("\n## Retry — your previous output failed validation\n\n");
            sb.Append("Orca parsed the file you wrote and rejected it. The exact error:\n\n");
            sb.Append("```\n");
            sb.Append(previousError.ReplaceLineEndings("\n").TrimEnd());
            sb.Append("\n```\n\n");
            sb.Append($"The rejected file is still at {auditFilePath} (unless you never wrote it). ");
            sb.Append("Fix it: correct this error, re-check the frontmatter and section contract, and ");
            sb.Append("write only that file, nothing else.\n");
        }

        return sb.ToString();
    }
}
