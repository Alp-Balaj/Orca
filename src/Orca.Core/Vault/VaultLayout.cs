namespace Orca.Core.Vault;

/// <summary>Path construction for the approved vault layout — one folder per plan:
/// <code>
/// &lt;vault&gt;/&lt;plansDir&gt;/&lt;plan-id&gt;/
///   &lt;plan-id&gt;.md
///   &lt;checkpoint-id&gt;.md
///   verification/&lt;checkpoint-id&gt;.attempt-&lt;n&gt;.md
/// </code>
/// Every id is validated against the schema regexes before touching a path, so a
/// malicious or mistyped id can never escape the plans directory.</summary>
public sealed class VaultLayout
{
    public VaultLayout(string vaultRoot, string plansDir)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
            throw new ArgumentException("vault root must be a non-empty path", nameof(vaultRoot));
        if (string.IsNullOrWhiteSpace(plansDir))
            throw new ArgumentException("plans directory must be non-empty", nameof(plansDir));

        // Config files spell plansDir with forward slashes for cross-machine portability.
        var relative = plansDir.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative))
            throw new ArgumentException("plans directory must be relative to the vault root", nameof(plansDir));
        if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".." or "."))
            throw new ArgumentException("plans directory must not contain '.' or '..' segments", nameof(plansDir));

        VaultRoot = Path.GetFullPath(vaultRoot);
        PlansRoot = Path.Combine(VaultRoot, relative);
    }

    public string VaultRoot { get; }

    public string PlansRoot { get; }

    public string PlanDirectory(string planId) => Path.Combine(PlansRoot, ValidatePlanId(planId));

    public string PlanFile(string planId) => Path.Combine(PlanDirectory(planId), planId + ".md");

    /// <summary>SUG-09: session notes live beside the plans directory, matching the
    /// vault's own Sessions/ convention.</summary>
    public string SessionsDirectory() =>
        Path.Combine(Path.GetDirectoryName(PlansRoot)!, "Sessions");

    public string SessionFile(DateTimeOffset date, string planId, string checkpointId) =>
        Path.Combine(SessionsDirectory(),
            $"{date:yyyy-MM-dd}-{ValidatePlanId(planId)}-{ValidateCheckpointId(checkpointId)}.md");

    /// <summary>SUG-09: decision-note drafts land here for the human to keep/edit/delete.</summary>
    public string DecisionsDirectory() =>
        Path.Combine(Path.GetDirectoryName(PlansRoot)!, "Decisions");

    public string ChildNoteFile(string planId, string checkpointId) =>
        Path.Combine(PlanDirectory(planId), ValidateCheckpointId(checkpointId) + ".md");

    public string VerificationDirectory(string planId) =>
        Path.Combine(PlanDirectory(planId), "verification");

    public string VerificationFile(string planId, string checkpointId, int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");
        return Path.Combine(
            VerificationDirectory(planId),
            $"{ValidateCheckpointId(checkpointId)}.attempt-{attempt}.md");
    }

    public string ReconciliationDirectory(string planId) =>
        Path.Combine(PlanDirectory(planId), "reconciliation");

    public string ReconciliationFile(string planId, string checkpointId, int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");
        return Path.Combine(
            ReconciliationDirectory(planId),
            $"{ValidateCheckpointId(checkpointId)}.attempt-{attempt}.md");
    }

    /// <summary>Where the Reconciler and Verifier write their raw §5.4 output before Orca
    /// validates and re-serializes it into a canonical record. A dot-directory so Obsidian
    /// hides it; record directories only ever hold Orca-written files, so a malformed
    /// leftover here can never poison a later read.</summary>
    public string StagingDirectory(string planId) =>
        Path.Combine(PlanDirectory(planId), ".staging");

    public string StagingReconciliationFile(string planId, string checkpointId, int attempt) =>
        StagingFile(planId, checkpointId, attempt, "reconciliation");

    public string StagingSemanticFile(string planId, string checkpointId, int attempt) =>
        StagingFile(planId, checkpointId, attempt, "semantic");

    private string StagingFile(string planId, string checkpointId, int attempt, string kind)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");
        return Path.Combine(
            StagingDirectory(planId),
            $"{ValidateCheckpointId(checkpointId)}.attempt-{attempt}.{kind}.md");
    }

    internal static string ValidatePlanId(string planId) =>
        SchemaRegexes.PlanId().IsMatch(planId)
            ? planId
            : throw new ArgumentException($"'{planId}' is not a valid plan id", nameof(planId));

    internal static string ValidateCheckpointId(string checkpointId) =>
        SchemaRegexes.CheckpointId().IsMatch(checkpointId)
            ? checkpointId
            : throw new ArgumentException($"'{checkpointId}' is not a valid checkpoint id", nameof(checkpointId));
}
