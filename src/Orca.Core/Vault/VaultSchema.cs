namespace Orca.Core.Vault;

public static class VaultSchema
{
    /// <summary>The frontmatter schema version this build reads and writes. Distinct from
    /// any plan's own version; bump only with a migration story.</summary>
    public const int Version = 1;
}
