namespace Orca.Cli;

/// <summary>Whether a real human can answer the interactive prompts on this terminal
/// (TUI-plan §3.6). A redirected stdin or stdout means the prompts have no one to answer
/// them — and Spectre's <c>SelectionPrompt</c>/<c>ConfirmationPrompt</c> throw rather than
/// block — so those runs must take a non-interactive path instead of crashing after quota
/// was spent. On Unix, <c>TERM=dumb</c> is a terminal that cannot host the prompts either.
///
/// Exposed as a static probe for production wiring; the callers take the *result* as a
/// plain <c>bool</c> so their non-interactive behavior is unit-testable by injecting it,
/// rather than only observable through a real redirected console.</summary>
internal static class ConsoleInteractivity
{
    public static bool Detect()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;
        if (!OperatingSystem.IsWindows()
            && string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal))
            return false;
        return true;
    }
}
