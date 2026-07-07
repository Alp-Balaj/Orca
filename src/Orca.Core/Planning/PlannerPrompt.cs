using System.Text;
using Orca.Core.Run;

namespace Orca.Core.Planning;

/// <summary>Assembles the Planner's prompt: the versioned role prompt file (embedded at
/// build from <c>/prompts/planner.md</c>, SHA-256 recorded in planning state) plus a
/// request block, plus — on a Reflexion retry — the previous attempt's exact parse error
/// (spec §5.4: "your last output did not parse — here is the exact error — try again").
/// Every assembly is deterministic LF text; its hash is recorded per attempt.</summary>
public static class PlannerPrompt
{
    private const string ResourceName = "Orca.Core.Prompts.planner.md";

    public static string RoleText { get; } = RolePromptResource.Read(ResourceName);

    /// <summary>Hash of the role prompt file itself — the attribution contract ("did the
    /// Planner get stricter, or did prompt v3 land").</summary>
    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);

    public static string AssembleInitial(PlanningRequest request, string relativePlanPath, string timestamp) =>
        Assemble(request, relativePlanPath, timestamp, previousError: null);

    /// <summary>The retry prompt: identical request, plus the exact error and the
    /// instruction to fix the file in place. The malformed file is deliberately left in
    /// the vault — the model reads and corrects it rather than reconstructing from
    /// memory.</summary>
    public static string AssembleRetry(
        PlanningRequest request, string relativePlanPath, string timestamp, string previousError) =>
        Assemble(request, relativePlanPath, timestamp, previousError);

    private static string Assemble(
        PlanningRequest request, string relativePlanPath, string timestamp, string? previousError)
    {
        var sb = new StringBuilder();
        sb.Append(RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        sb.Append($"- plan_id: {request.PlanId}\n");
        sb.Append($"- target_repo: {request.TargetRepoName}\n");
        sb.Append($"- base_branch: {request.BaseBranch}\n");
        sb.Append($"- created/updated timestamp to use verbatim: {timestamp}\n");
        sb.Append($"- write the plan to this path, relative to your working directory: {relativePlanPath}\n");
        if (request.Title is not null)
            sb.Append($"- title to use verbatim: {request.Title}\n");
        sb.Append("\n### Goal brief\n\n");
        sb.Append(request.Goal.ReplaceLineEndings("\n").TrimEnd());
        sb.Append('\n');

        if (previousError is not null)
        {
            sb.Append("\n## Retry — your previous output failed validation\n\n");
            sb.Append("Orca parsed the file you wrote and rejected it. The exact error:\n\n");
            sb.Append("```\n");
            sb.Append(previousError.ReplaceLineEndings("\n").TrimEnd());
            sb.Append("\n```\n\n");
            sb.Append($"The rejected file is still at {relativePlanPath} (unless you never wrote it). ");
            sb.Append("Fix it: correct this error, re-check the whole frontmatter contract, and write ");
            sb.Append("only that file, nothing else.\n");
        }

        return sb.ToString();
    }
}
