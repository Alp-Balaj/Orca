using System.Globalization;
using Orca.Core.Domain;
using YamlDotNet.RepresentationModel;

namespace Orca.Core.Vault;

/// <summary>Strict parser for plan files. Every schema violation throws
/// <see cref="VaultSchemaException"/> — a half-parsed plan silently executed is exactly
/// the drift Orca exists to prevent.</summary>
public static class PlanParser
{
    public static Plan Parse(string fileText, string filePath)
    {
        // BF-14 §1: hand-editor traps caught as clear errors before YAML gets a say.
        FrontmatterLint.Check(fileText, filePath);

        var (root, body) = Frontmatter.Parse(fileText, filePath);
        var fm = new MappingReader(root, filePath, "frontmatter");

        var schemaVersion = fm.RequireInt("schema_version");
        if (schemaVersion != VaultSchema.Version)
            throw new VaultSchemaException(filePath,
                $"unsupported schema_version {schemaVersion}; this build of Orca supports version {VaultSchema.Version}");

        var planId = fm.RequireString("plan_id");
        if (!SchemaRegexes.PlanId().IsMatch(planId))
            throw new VaultSchemaException(filePath, $"plan_id '{planId}' must match {SchemaRegexes.PlanId()}");

        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (!string.Equals(stem, planId, StringComparison.Ordinal))
            throw new VaultSchemaException(filePath, $"plan_id '{planId}' must equal the file name stem '{stem}'");

        var planVersion = fm.RequireInt("plan_version");
        if (planVersion < 1)
            throw new VaultSchemaException(filePath, "plan_version must be >= 1");

        var title = fm.RequireString("title");
        var status = SchemaEnums.ParsePlanStatus(fm.RequireString("status"), filePath);
        var targetRepo = fm.RequireString("target_repo");
        var baseBranch = fm.RequireString("base_branch");
        var created = fm.RequireTimestamp("created");
        var updated = fm.RequireTimestamp("updated");
        var planning = ParsePlanning(fm.RequireMapping("planning"), filePath);
        var checkpoints = ParseCheckpoints(fm.RequireSequence("checkpoints"), filePath);

        fm.ThrowIfUnknownKeys(MappingReader.ObsidianTolerated);

        return new Plan(
            schemaVersion, planId, planVersion, title, status, targetRepo, baseBranch,
            created, updated, planning, checkpoints, body);
    }

    private static PlanningInfo ParsePlanning(MappingReader planning, string filePath)
    {
        var strategy = SchemaEnums.ParseStrategy(planning.RequireString("strategy"), filePath);

        if (strategy == PlanningStrategyKind.SingleCandidate)
        {
            foreach (var key in (string[])["candidates", "selected", "rationale"])
            {
                if (planning.Has(key))
                    throw new VaultSchemaException(filePath,
                        $"planning: '{key}' must be absent when strategy is single_candidate");
            }

            planning.ThrowIfUnknownKeys();
            return new PlanningInfo(strategy, null, null, null);
        }

        var candidates = planning.RequireInt("candidates");
        if (candidates < 2)
            throw new VaultSchemaException(filePath, "planning: candidates must be >= 2 for multi_candidate");

        var selected = planning.RequireInt("selected");
        if (selected < 1 || selected > candidates)
            throw new VaultSchemaException(filePath,
                $"planning: selected must be a 1-based index between 1 and candidates ({candidates})");

        var rationale = planning.RequireString("rationale");
        planning.ThrowIfUnknownKeys();
        return new PlanningInfo(strategy, candidates, selected, rationale);
    }

    private static IReadOnlyList<Checkpoint> ParseCheckpoints(IReadOnlyList<YamlNode> nodes, string filePath)
    {
        if (nodes.Count == 0)
            throw new VaultSchemaException(filePath, "checkpoints must be a non-empty list");

        var checkpoints = new List<Checkpoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lastPrefix = -1;

        for (var i = 0; i < nodes.Count; i++)
        {
            var cp = new MappingReader(nodes[i], filePath, $"checkpoints[{i}]");

            var id = cp.RequireString("id");
            if (!SchemaRegexes.CheckpointId().IsMatch(id))
                throw new VaultSchemaException(filePath, $"checkpoint id '{id}' must match {SchemaRegexes.CheckpointId()}");
            if (!seen.Add(id))
                throw new VaultSchemaException(filePath, $"duplicate checkpoint id '{id}'");

            var prefix = int.Parse(id.AsSpan(0, 2), CultureInfo.InvariantCulture);
            if (prefix <= lastPrefix)
                throw new VaultSchemaException(filePath,
                    $"checkpoint id '{id}': numeric prefixes must be strictly increasing in list order");
            lastPrefix = prefix;

            var description = cp.RequireString("description");
            var status = SchemaEnums.ParseCheckpointStatus(cp.RequireString("status"), filePath);
            var criteria = cp.RequireStringList("acceptance_criteria");
            var model = cp.OptionalString("model");
            var maxBudgetUsd = cp.OptionalDecimal("max_budget_usd");
            if (maxBudgetUsd is <= 0)
                throw new VaultSchemaException(filePath,
                    $"checkpoint '{id}': max_budget_usd, when set, must be positive, got {maxBudgetUsd}");

            // SUG-03 stage A: optional depends_on — absent means the flat list's implied
            // default (the previous checkpoint), [] means an explicit parallel root.
            // Every named dependency must be an EARLIER checkpoint: numbering stays
            // presentation order, depends_on is the truth, and backward-only edges make
            // cycles structurally impossible rather than needing detection.
            IReadOnlyList<string>? dependsOn = null;
            if (cp.Has("depends_on"))
            {
                dependsOn = cp.RequireStringListAllowEmpty("depends_on");
                var dupes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dep in dependsOn)
                {
                    if (string.Equals(dep, id, StringComparison.Ordinal))
                        throw new VaultSchemaException(filePath,
                            $"checkpoint '{id}': depends_on must not name the checkpoint itself");
                    if (!seen.Contains(dep))
                        throw new VaultSchemaException(filePath,
                            $"checkpoint '{id}': depends_on names '{dep}', which is not an earlier checkpoint " +
                            "in this plan — dependencies may only point backwards in list order");
                    if (!dupes.Add(dep))
                        throw new VaultSchemaException(filePath,
                            $"checkpoint '{id}': depends_on names '{dep}' more than once");
                }
            }

            cp.ThrowIfUnknownKeys();

            checkpoints.Add(new Checkpoint(id, description, status, criteria, model, maxBudgetUsd, dependsOn));
        }

        return checkpoints;
    }

    /// <summary>BF-14 §3: cheap cross-invariant warnings — hand-edited nonsense states
    /// that parse cleanly but contradict themselves. Warnings, not refusals, to keep
    /// hand-editing friendly; `orca status` surfaces them.</summary>
    public static IReadOnlyList<string> LintInvariants(Plan plan)
    {
        var warnings = new List<string>();

        if (plan.Status == PlanStatus.Complete
            && plan.Checkpoints.Any(c => c.Status != CheckpointStatus.Complete))
            warnings.Add($"plan '{plan.PlanId}' says complete but has non-complete checkpoints");

        var inProgress = plan.Checkpoints.Where(c => c.Status == CheckpointStatus.InProgress).ToList();
        if (inProgress.Count > 1)
            warnings.Add(
                $"plan '{plan.PlanId}' has {inProgress.Count} checkpoints in_progress at once " +
                $"({string.Join(", ", inProgress.Select(c => c.Id))}) — Orca runs one at a time");

        if (plan.Updated < plan.Created)
            warnings.Add($"plan '{plan.PlanId}': updated ({plan.Updated:yyyy-MM-dd}) is before created ({plan.Created:yyyy-MM-dd})");

        return warnings;
    }
}
