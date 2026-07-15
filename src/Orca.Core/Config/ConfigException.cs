namespace Orca.Core.Config;

/// <summary>A configuration file violated the schema or the merged result is unusable.
/// Same fail-loud discipline as the vault parsers: a half-understood config silently
/// applied is worse than a crash.</summary>
public class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The target repo has no committed <c>orca.json</c> at its root. A distinct
/// subtype (not a message string-match) so the CLI can append its own onboarding hint —
/// "run 'orca init' to scaffold one." — without Core ever knowing CLI command names.</summary>
public sealed class MissingRepoConfigException : ConfigException
{
    public MissingRepoConfigException(string message)
        : base(message)
    {
    }
}
