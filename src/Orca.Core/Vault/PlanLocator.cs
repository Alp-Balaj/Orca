namespace Orca.Core.Vault;

/// <summary>Resolves which plan a command operates on: an explicit id wins; otherwise
/// exactly one plan directory must exist under the plans root — anything else is asked
/// back to the human rather than guessed (spec §0: silent assumptions are the failure
/// mode).</summary>
public static class PlanLocator
{
    /// <summary>Every plan id under the plans root, ordinal-ordered: directories whose
    /// name is a valid plan id and that contain their <c>&lt;plan-id&gt;.md</c> spine.
    /// Empty when the plans directory holds no plans; a missing plans directory is a
    /// misconfigured vault and throws.</summary>
    public static IReadOnlyList<string> ListPlanIds(VaultLayout layout)
    {
        if (!Directory.Exists(layout.PlansRoot))
            throw new VaultException($"plans directory does not exist: {layout.PlansRoot}");

        return Directory.EnumerateDirectories(layout.PlansRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => SchemaRegexes.PlanId().IsMatch(name)
                           && File.Exists(Path.Combine(layout.PlansRoot, name, name + ".md")))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>BF-09 §3: plan-shaped directories <see cref="ListPlanIds"/> silently
    /// skips, each with the reason a human can act on. The classic two-machine trap: a
    /// `FOO.md` spine inside `foo/` works on Windows and is invisible on Linux — same
    /// vault, different plan list per machine, and nothing said so.</summary>
    public static IReadOnlyList<string> ListSkippedPlanDirectories(VaultLayout layout)
    {
        if (!Directory.Exists(layout.PlansRoot))
            return [];

        var warnings = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(layout.PlansRoot))
        {
            var name = Path.GetFileName(dir);
            if (SchemaRegexes.PlanId().IsMatch(name))
            {
                if (File.Exists(Path.Combine(dir, name + ".md")))
                    continue;

                var differentCase = Directory.EnumerateFiles(dir, "*.md")
                    .Select(Path.GetFileName)
                    .FirstOrDefault(f => string.Equals(f, name + ".md", StringComparison.OrdinalIgnoreCase));
                warnings.Add(differentCase is not null
                    ? $"plan directory '{name}' skipped: found '{differentCase}', expected '{name}.md' — case matters on this machine even if it didn't on the other one"
                    : $"plan directory '{name}' skipped: no '{name}.md' spine file");
            }
            else if (SchemaRegexes.PlanId().IsMatch(name.ToLowerInvariant()))
            {
                warnings.Add(
                    $"directory '{name}' skipped: plan ids are lowercase — rename it (and its spine) to '{name.ToLowerInvariant()}'");
            }
        }

        return warnings;
    }

    public static string ResolvePlanId(VaultLayout layout, string? explicitPlanId)
    {
        if (explicitPlanId is not null)
        {
            VaultLayout.ValidatePlanId(explicitPlanId);
            return explicitPlanId;
        }

        return ListPlanIds(layout) switch
        {
            [] => throw new VaultException(
                $"no plans found under {layout.PlansRoot} — create one with 'orca plan-init'"),
            [var only] => only,
            var candidates => throw new VaultException(
                $"multiple plans found under {layout.PlansRoot} ({string.Join(", ", candidates)}) — pass --plan to choose"),
        };
    }
}
