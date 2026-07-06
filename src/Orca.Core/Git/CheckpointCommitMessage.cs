using System.Globalization;
using Orca.Core.Vault;

namespace Orca.Core.Git;

/// <summary>The structured commit message written on human approval — subject line
/// greppable back to the plan (<c>orca: plan/checkpoint attempt N</c>), trailer carrying
/// the attribution facts (spec §7.1: target-repo history stays traceable to plans).</summary>
public static class CheckpointCommitMessage
{
    public static string Build(
        string planId,
        int planVersion,
        string checkpointId,
        string description,
        int attempt,
        string? sessionId,
        decimal? costUsd)
    {
        VaultLayout.ValidatePlanId(planId);
        VaultLayout.ValidateCheckpointId(checkpointId);
        if (planVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(planVersion), planVersion, "plan version must be >= 1");
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "attempt must be >= 1");
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("checkpoint description must be non-empty", nameof(description));

        var cost = costUsd is { } c
            ? "$" + c.ToString("0.00####", CultureInfo.InvariantCulture)
            : "unknown";

        return $"""
            orca: {planId}/{checkpointId} attempt {attempt}

            Plan: {planId} v{planVersion}
            Checkpoint: {checkpointId} — {description}
            Session: {sessionId ?? "none"}
            Cost: {cost}
            """;
    }
}
