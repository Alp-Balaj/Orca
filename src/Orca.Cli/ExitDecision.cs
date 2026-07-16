using Orca.Core.Config;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Orca.Core.Run;
using Orca.Core.Vault;
using Spectre.Console.Cli;

namespace Orca.Cli;

/// <summary>Pure projection of a top-level exception into Program.cs's exit behavior: a
/// one-line message to print and exit 1 for a recognized refusal/misconfiguration, or
/// <c>null</c> to mean "let it crash" — an Orca wiring bug (a <see cref="NullReferenceException"/>,
/// a <see cref="CommandConfigurationException"/> from a broken Spectre command registration)
/// should surface with its full stack, not get paraphrased into a quiet exit 1. No I/O, no
/// Spectre rendering, no dependency on Program.cs — the catch blocks there call this and act
/// on the result.</summary>
internal static class ExitDecision
{
    /// <summary>The message to print before exiting 1, or <c>null</c> if the exception
    /// should instead propagate and crash the process.</summary>
    public static string? Describe(Exception exception) => exception switch
    {
        // A distinct subtype (not a message string-match) so the CLI can append its own
        // onboarding hint — "run 'orca init' to scaffold one." — without Core ever knowing
        // CLI command names.
        MissingRepoConfigException e => $"{e.Message} — run 'orca init' to scaffold one.",

        ConfigException or VaultException or GitException or ExecutorException
            or CheckpointRunException or PlanningException or ArgumentException
            or GateException or UnauthorizedAccessException or IOException
            or CommandParseException or CommandRuntimeException
            => exception.Message,

        // Everything else — including CommandConfigurationException and
        // NullReferenceException — is an Orca wiring bug, not a refusal: crash loud.
        _ => null,
    };
}
