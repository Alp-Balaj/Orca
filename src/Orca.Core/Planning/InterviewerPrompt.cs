using System.Text;
using Orca.Core.Run;

namespace Orca.Core.Planning;

/// <summary>Assembles the Interviewer's prompt (SUG-08): the versioned role prompt file
/// (embedded at build from <c>/prompts/interviewer.md</c>) plus a request block carrying
/// the raw goal, plus — on a §5.4 Reflexion retry — the previous attempt's exact parse
/// error. Vault-scoped like the Planner: the goal note path is relative to the vault.</summary>
public static class InterviewerPrompt
{
    private const string ResourceName = "Orca.Core.Prompts.interviewer.md";

    public static string RoleText { get; } = RolePromptResource.Read(ResourceName);

    /// <summary>Hash of the role prompt file itself — same attribution contract as the
    /// other roles.</summary>
    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);

    public static string AssembleInitial(
        string planId, string rawGoal, string relativeGoalPath, string timestamp) =>
        Assemble(planId, rawGoal, relativeGoalPath, timestamp, previousError: null);

    public static string AssembleRetry(
        string planId, string rawGoal, string relativeGoalPath, string timestamp, string previousError) =>
        Assemble(planId, rawGoal, relativeGoalPath, timestamp, previousError);

    private static string Assemble(
        string planId, string rawGoal, string relativeGoalPath, string timestamp, string? previousError)
    {
        var sb = new StringBuilder();
        sb.Append(RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        sb.Append($"- plan_id: {planId}\n");
        sb.Append($"- created timestamp to use verbatim: {timestamp}\n");
        sb.Append($"- write the goal note to this path, relative to your working directory: {relativeGoalPath}\n");
        sb.Append("\n### Raw goal (refine this)\n\n");
        sb.Append(rawGoal.ReplaceLineEndings("\n").TrimEnd());
        sb.Append('\n');

        if (previousError is not null)
        {
            sb.Append("\n## Retry — your previous output failed validation\n\n");
            sb.Append("Orca parsed the file you wrote and rejected it. The exact error:\n\n");
            sb.Append("```\n");
            sb.Append(previousError.ReplaceLineEndings("\n").TrimEnd());
            sb.Append("\n```\n\n");
            sb.Append($"The rejected file is still at {relativeGoalPath} (unless you never wrote it). ");
            sb.Append("Fix it: correct this error, re-check the frontmatter and section contract, and ");
            sb.Append("write only that file, nothing else.\n");
        }

        return sb.ToString();
    }
}
