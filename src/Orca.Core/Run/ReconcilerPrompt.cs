using System.Text;
using Orca.Core.Domain;

namespace Orca.Core.Run;

/// <summary>Assembles the Reconciler's prompt: the versioned role prompt (embedded from
/// <c>/prompts/reconciler.md</c>), the identity block, the checkpoint's plan section, and
/// the diff — plus, on a Reflexion retry, the previous attempt's exact validation error
/// (spec §5.4). Deliberately thin (spec §8): diff + plan section + one question.</summary>
public static class ReconcilerPrompt
{
    private const string ResourceName = "Orca.Core.Prompts.reconciler.md";

    public static string RoleText { get; } = RolePromptResource.Read(ResourceName);

    /// <summary>Hash of the role prompt file itself — the attribution contract.</summary>
    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);

    public static string Assemble(
        AttemptIdentity identity,
        string relativeStagingPath,
        string planSection,
        string diff,
        string? previousError = null)
    {
        var sb = new StringBuilder();
        sb.Append(RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        AppendIdentity(sb, identity);
        sb.Append($"- write the verdict to this path, relative to your working directory: {relativeStagingPath}\n");
        sb.Append("\n## Plan section for this checkpoint\n\n");
        sb.Append(planSection.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Diff\n\n");
        AppendFenced(sb, "diff", diff);

        if (previousError is not null)
            AppendRetry(sb, relativeStagingPath, previousError);

        return sb.ToString();
    }

    internal static void AppendIdentity(StringBuilder sb, AttemptIdentity identity)
    {
        sb.Append($"- plan_id: {identity.PlanId}\n");
        sb.Append($"- plan_version: {identity.PlanVersion}\n");
        sb.Append($"- checkpoint_id: {identity.CheckpointId}\n");
        sb.Append($"- attempt: {identity.Attempt}\n");
    }

    /// <summary>Four-backtick fences so embedded triple-backtick content cannot close the
    /// block early.</summary>
    internal static void AppendFenced(StringBuilder sb, string language, string content)
    {
        sb.Append("````").Append(language).Append('\n');
        sb.Append(content.ReplaceLineEndings("\n").TrimEnd()).Append('\n');
        sb.Append("````\n");
    }

    internal static void AppendRetry(StringBuilder sb, string relativeStagingPath, string previousError)
    {
        sb.Append("\n## Retry — your previous output failed validation\n\n");
        sb.Append("Orca parsed the file you wrote and rejected it. The exact error:\n\n");
        sb.Append("```\n");
        sb.Append(previousError.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n```\n\n");
        sb.Append($"The rejected file is still at {relativeStagingPath} (unless you never wrote it). ");
        sb.Append("Fix it: correct this error, re-check the whole frontmatter contract, and write ");
        sb.Append("only that file, nothing else.\n");
    }
}
