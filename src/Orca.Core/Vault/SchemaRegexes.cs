using System.Text.RegularExpressions;

namespace Orca.Core.Vault;

internal static partial class SchemaRegexes
{
    /// <summary>Plan ids are git-branch- and filename-safe slugs.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    internal static partial Regex PlanId();

    /// <summary>Checkpoint ids carry an ordering prefix and double as the child-note file
    /// stem and the checkpoint branch suffix. The prefix is later parsed with
    /// <c>int.Parse</c>, which only understands ASCII digits — so this uses the explicit
    /// [0-9] class rather than \d, which (being Unicode-aware) would also match non-ASCII
    /// decimal digits and let a FormatException leak out of the parser instead of a clean
    /// VaultSchemaException.</summary>
    [GeneratedRegex("^[0-9]{2}-[a-z0-9-]+$")]
    internal static partial Regex CheckpointId();

    /// <summary>ISO-8601 with an explicit offset — a local time without offset is ambiguous
    /// across the owner's machines and therefore rejected. ASCII [0-9], not \d — see
    /// <see cref="CheckpointId"/>.</summary>
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})$")]
    internal static partial Regex Timestamp();

    /// <summary>Extracts the attempt number from a verification record file name. ASCII
    /// [0-9], not \d — see <see cref="CheckpointId"/>.</summary>
    [GeneratedRegex(@"\.attempt-([0-9]+)\.md$")]
    internal static partial Regex AttemptSuffix();
}
