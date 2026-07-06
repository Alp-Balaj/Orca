namespace Orca.Core.Vault;

/// <summary>Vault contract violation that is not a parse error: missing files,
/// cross-reference mismatches, refusal to overwrite append-only records.</summary>
public class VaultException : Exception
{
    public VaultException(string message)
        : base(message)
    {
    }

    public VaultException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Schema violation in a vault file. Always thrown, never downgraded to a
/// warning: a half-parsed plan silently executed is exactly the drift Orca exists to
/// prevent.</summary>
public sealed class VaultSchemaException : VaultException
{
    public VaultSchemaException(string filePath, string message)
        : base($"{filePath}: {message}")
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}
