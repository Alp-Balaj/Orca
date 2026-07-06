using Orca.Core.Vault;

namespace Orca.Core.Execution;

/// <summary>Path construction for run logs in the <b>target repo</b> (never the vault —
/// transcripts are ephemeral evidence, not durable knowledge):
/// <code>
/// &lt;target-repo&gt;/.orca/runs/&lt;plan-id&gt;/
///   &lt;checkpoint-id&gt;.attempt-&lt;n&gt;.ndjson
///   &lt;checkpoint-id&gt;.attempt-&lt;n&gt;.stderr.txt
///   planning.attempt-&lt;n&gt;.ndjson
///   planning.attempt-&lt;n&gt;.stderr.txt
/// </code>
/// Ids are validated against the same schema regexes as the vault, so nothing can
/// escape the runs directory. The <c>planning</c> stem cannot collide with a checkpoint
/// id, which always starts with a two-digit prefix.</summary>
public sealed class RunLogLayout
{
    public RunLogLayout(string targetRepoRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRepoRoot))
            throw new ArgumentException("target repo root must be a non-empty path", nameof(targetRepoRoot));

        RunsRoot = Path.Combine(Path.GetFullPath(targetRepoRoot), ".orca", "runs");
    }

    public string RunsRoot { get; }

    public RunLogPaths AttemptPaths(string planId, string checkpointId, int attempt)
    {
        VaultLayout.ValidatePlanId(planId);
        VaultLayout.ValidateCheckpointId(checkpointId);
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");

        var directory = Path.Combine(RunsRoot, planId);
        var stem = $"{checkpointId}.attempt-{attempt}";
        return new RunLogPaths
        {
            EventLog = Path.Combine(directory, stem + ".ndjson"),
            Stderr = Path.Combine(directory, stem + ".stderr.txt"),
        };
    }

    /// <summary>Where one attempt's generated <c>--settings</c> payload (the
    /// <c>permissions.deny</c> rules in force) is written — input evidence beside the
    /// attempt's logs, same id validation.</summary>
    public string SettingsFile(string planId, string checkpointId, int attempt)
    {
        VaultLayout.ValidatePlanId(planId);
        VaultLayout.ValidateCheckpointId(checkpointId);
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");

        return Path.Combine(RunsRoot, planId, $"{checkpointId}.attempt-{attempt}.settings.json");
    }

    /// <summary>Log paths for one Planner invocation — same directory as the plan's
    /// checkpoint logs, <c>planning</c> stem.</summary>
    public RunLogPaths PlanningAttemptPaths(string planId, int attempt)
    {
        VaultLayout.ValidatePlanId(planId);
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");

        var stem = $"planning.attempt-{attempt}";
        return new RunLogPaths
        {
            EventLog = Path.Combine(RunsRoot, planId, stem + ".ndjson"),
            Stderr = Path.Combine(RunsRoot, planId, stem + ".stderr.txt"),
        };
    }

    /// <summary>The <c>--settings</c> evidence file for one Planner invocation.</summary>
    public string PlanningSettingsFile(string planId, int attempt)
    {
        VaultLayout.ValidatePlanId(planId);
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");

        return Path.Combine(RunsRoot, planId, $"planning.attempt-{attempt}.settings.json");
    }
}

/// <summary>The two files one invocation writes: the NDJSON event log and the raw
/// stderr capture.</summary>
public sealed record RunLogPaths
{
    public required string EventLog { get; init; }
    public required string Stderr { get; init; }
}
