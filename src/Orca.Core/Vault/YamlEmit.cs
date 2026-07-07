namespace Orca.Core.Vault;

/// <summary>Shared emission helpers for the deterministic record serializers — one
/// quoting rule for every free-text string Orca writes into the vault.</summary>
internal static class YamlEmit
{
    internal static string Quote(string value) =>
        "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t") + "\"";
}
