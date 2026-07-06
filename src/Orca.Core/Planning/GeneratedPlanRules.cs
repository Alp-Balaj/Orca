using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Core.Planning;

/// <summary>Planner-output rules beyond what <see cref="PlanParser"/> enforces
/// (owner-approved 2026-07-06): a generated plan is a <b>draft</b> the human approves —
/// the Planner can never self-approve or mis-target. Violations throw
/// <see cref="VaultSchemaException"/> and feed the same capped Reflexion retry as parse
/// errors.</summary>
public static class GeneratedPlanRules
{
    public static void Validate(Plan plan, PlanningRequest request, PlanningStrategyKind strategy, string filePath)
    {
        // plan_id == request and stem == plan_id are already parser-enforced via the file path.
        if (plan.Status != PlanStatus.Draft)
            throw new VaultSchemaException(filePath,
                $"a generated plan must have status 'draft' (the human approves plans, never the Planner); got '{SchemaEnums.ToYaml(plan.Status)}'");
        if (plan.PlanVersion != 1)
            throw new VaultSchemaException(filePath,
                $"a freshly generated plan must have plan_version 1; got {plan.PlanVersion}");
        if (plan.Planning.Strategy != strategy)
            throw new VaultSchemaException(filePath,
                $"planning.strategy must be '{SchemaEnums.ToYaml(strategy)}' for this planning run; got '{SchemaEnums.ToYaml(plan.Planning.Strategy)}'");
        if (plan.TargetRepo != request.TargetRepoName)
            throw new VaultSchemaException(filePath,
                $"target_repo must be exactly '{request.TargetRepoName}' as requested; got '{plan.TargetRepo}'");
        if (plan.BaseBranch != request.BaseBranch)
            throw new VaultSchemaException(filePath,
                $"base_branch must be exactly '{request.BaseBranch}' as requested; got '{plan.BaseBranch}'");
        if (request.Title is not null && plan.Title != request.Title)
            throw new VaultSchemaException(filePath,
                $"title must be exactly '{request.Title}' as requested; got '{plan.Title}'");

        var notPending = plan.Checkpoints.FirstOrDefault(c => c.Status != CheckpointStatus.Pending);
        if (notPending is not null)
            throw new VaultSchemaException(filePath,
                $"every checkpoint of a generated plan must be 'pending'; '{notPending.Id}' is '{SchemaEnums.ToYaml(notPending.Status)}'");
    }
}
