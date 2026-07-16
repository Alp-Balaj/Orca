namespace Orca.Core.Execution;

public enum ClaudeVersionVerdict
{
    Match,
    Warn,
    Refuse,
}

/// <summary>Whether a version mismatch should only be surfaced, or should stop progress
/// until it's resolved. An orca.json opt-in: absent config means <see cref="Warn"/> —
/// new fields default to the non-breaking choice, never silently start refusing runs.</summary>
public enum ClaudeVersionPolicy
{
    Warn,
    Refuse,
}

/// <summary>Compares the <c>claude_code_version</c> an invocation actually reported
/// against the version Orca's safe-mode contract (deny-rule survival, stream-json shape)
/// was last probed against. Pure: no I/O, no config resolution — callers decide where
/// "pinned" and "policy" come from (SUG-15).</summary>
public static class ClaudeVersionCheck
{
    /// <summary>The version README.md's Status section pins. Bump both together, and only
    /// after re-probing the safe-mode contract (permissions.deny survival,
    /// ClaudeCliContractTests) on the new version — this constant is a claim that the
    /// probe still holds, not just a version number.</summary>
    public const string PinnedVersion = "2.1.201";

    /// <summary>Null (no init event, or one that omitted the field) is never treated as a
    /// mismatch — Orca has no evidence of a problem, so it stays silent rather than
    /// nagging about data it doesn't have.</summary>
    public static ClaudeVersionVerdict Evaluate(string? observed, string pinned, ClaudeVersionPolicy policy)
    {
        if (observed is null || observed == pinned)
            return ClaudeVersionVerdict.Match;
        return policy == ClaudeVersionPolicy.Refuse ? ClaudeVersionVerdict.Refuse : ClaudeVersionVerdict.Warn;
    }
}
