using System.Text.RegularExpressions;

namespace Orca.Core.Vault;

internal static partial class SchemaRegexes
{
    /// <summary>Plan ids are git-branch- and filename-safe slugs.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    internal static partial Regex PlanId();

    /// <summary>Checkpoint ids carry an ordering prefix and double as the child-note file
    /// stem and the checkpoint branch suffix.</summary>
    [GeneratedRegex(@"^\d{2}-[a-z0-9-]+$")]
    internal static partial Regex CheckpointId();

    /// <summary>ISO-8601 with an explicit offset — a local time without offset is ambiguous
    /// across the owner's machines and therefore rejected.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    internal static partial Regex Timestamp();

    /// <summary>Extracts the attempt number from a verification record file name.</summary>
    [GeneratedRegex(@"\.attempt-(\d+)\.md$")]
    internal static partial Regex AttemptSuffix();
}
