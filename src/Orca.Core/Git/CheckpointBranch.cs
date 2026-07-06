using Orca.Core.Vault;

namespace Orca.Core.Git;

/// <summary>The approved branch naming: <c>orca/{plan-id}/{checkpoint-id}</c>. Both ids
/// are validated against the vault schema regexes, which were chosen to be git-ref-safe —
/// but validate anyway; a branch name is the last place to discover a bad id.</summary>
public static class CheckpointBranch
{
    public static string Name(string planId, string checkpointId) =>
        $"orca/{VaultLayout.ValidatePlanId(planId)}/{VaultLayout.ValidateCheckpointId(checkpointId)}";
}
