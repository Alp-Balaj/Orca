namespace Orca.Core.Domain;

/// <summary>Token counts from a single <c>result</c> event, mirrored into
/// <see cref="RunState.LastUsage"/>. Field set matches what CLI 2.1.201 reports; the
/// stream parser fails loudly if any of these are missing from a <c>result</c> event.</summary>
public sealed record TokenUsage
{
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long CacheCreationInputTokens { get; init; }
    public required long CacheReadInputTokens { get; init; }

    public static readonly TokenUsage Zero = new()
    {
        InputTokens = 0,
        OutputTokens = 0,
        CacheCreationInputTokens = 0,
        CacheReadInputTokens = 0,
    };

    /// <summary>Component-wise sum — SUG-06's per-role accumulation.</summary>
    public TokenUsage Plus(TokenUsage other) => new()
    {
        InputTokens = InputTokens + other.InputTokens,
        OutputTokens = OutputTokens + other.OutputTokens,
        CacheCreationInputTokens = CacheCreationInputTokens + other.CacheCreationInputTokens,
        CacheReadInputTokens = CacheReadInputTokens + other.CacheReadInputTokens,
    };
}
