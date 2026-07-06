using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>A fact that needs the real <c>claude</c> binary on PATH (but no quota).
/// Skips <em>visibly</em> when it is absent rather than passing vacuously.</summary>
public sealed class ClaudeCliFactAttribute : FactAttribute
{
    public ClaudeCliFactAttribute()
    {
        if (ClaudeExecutableResolver.Resolve() is null)
            Skip = "claude CLI not found on PATH";
    }
}
