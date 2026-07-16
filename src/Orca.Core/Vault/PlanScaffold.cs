namespace Orca.Core.Vault;

/// <summary>Creates a new plan directory in the vault with a schema-valid skeleton plan
/// file — the <c>plan-init</c> mechanics. The generated text is round-tripped through the
/// real <see cref="PlanParser"/> before anything touches disk, so <c>plan-init</c> can
/// never produce a file Orca itself refuses to read.</summary>
public static class PlanScaffold
{
    public static string CreatePlan(
        VaultLayout layout,
        string planId,
        string title,
        string targetRepoName,
        string baseBranch,
        DateTimeOffset now)
    {
        VaultLayout.ValidatePlanId(planId);
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title must be non-empty", nameof(title));
        if (string.IsNullOrWhiteSpace(targetRepoName))
            throw new ArgumentException("target repo name must be non-empty", nameof(targetRepoName));
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw new ArgumentException("base branch must be non-empty", nameof(baseBranch));

        var path = layout.PlanFile(planId);
        if (File.Exists(path))
            throw new VaultException($"refusing to overwrite existing plan file: {path}");

        var timestamp = $"{now.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z";
        var text = $"""
            ---
            schema_version: 1
            plan_id: {planId}
            plan_version: 1
            title: {QuoteYaml(title)}
            status: draft
            target_repo: {QuoteYaml(targetRepoName)}
            base_branch: {QuoteYaml(baseBranch)}
            created: {timestamp}
            updated: {timestamp}
            planning:
              strategy: single_candidate
            checkpoints:
              - id: 01-replace-me
                description: Replace with the first checkpoint's goal
                status: pending
                acceptance_criteria:
                  - Replace with a criterion a verifier could check
            ---

            <!-- Plan body: free prose for the human. Orca machine-reads only the frontmatter. -->

            ## Context

            Describe intent, constraints, and anything the Implementer must know.
            """.ReplaceLineEndings("\n") + "\n";

        // Fail loud before touching disk if the skeleton and the parser ever drift apart.
        PlanParser.Parse(text, path);

        Directory.CreateDirectory(layout.PlanDirectory(planId));
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, text);
            File.Move(tempPath, path, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException($"failed to write plan file: {path}", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        return path;
    }

    /// <summary>Double-quoted YAML scalar — the only escaping the skeleton needs, and the
    /// parse-before-write check catches anything this gets wrong.</summary>
    private static string QuoteYaml(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
