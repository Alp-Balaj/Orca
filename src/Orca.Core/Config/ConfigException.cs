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
