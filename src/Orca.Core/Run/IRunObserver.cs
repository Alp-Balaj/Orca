using Orca.Core.Config;
using Orca.Core.Execution;

namespace Orca.Core.Run;

/// <summary>Observer of one human-triggered run (checkpoint attempt or planning run).
/// Same threading contract as IClaudeStreamObserver: sequential per run, background
/// thread, must not block or throw.</summary>
public interface IRunObserver
{
    void OnRunEvent(RunObservation observation);
}

/// <summary>Closed union of everything a run surfaces while in flight. Payloads reuse
/// the existing evidence types — nothing here is a second source of truth.</summary>
public abstract record RunObservation
{
    private RunObservation() { }

    /// <summary>One claude invocation is starting. Invocation numbers §5.4 parse
    /// retries within the attempt (1-based), matching the run-log file names.</summary>
    public sealed record RoleStarted(RoleName Role, int Attempt, int Invocation,
        string EventLogPath) : RunObservation;

    public sealed record RoleStreamEvent(RoleName Role, ClaudeStreamEvent Event) : RunObservation;

    public sealed record RoleFinished(RoleName Role, ClaudeExecutionResult Execution) : RunObservation;

    /// <summary>The role invocation's claude_code_version didn't match
    /// <see cref="ClaudeVersionCheck.PinnedVersion"/>, under the default "warn" policy
    /// (SUG-15) — surfaced so the human running Orca sees it; the attempt is not
    /// blocked. Under the opt-in "refuse" policy a mismatch stops the attempt instead
    /// (<c>CheckpointSessionOutcome.ClaudeVersionRefused</c>) and this event is never
    /// raised for it.</summary>
    public sealed record ClaudeVersionMismatch(RoleName Role, string Observed, string Pinned) : RunObservation;

    /// <summary>A §5.4 parse/validation failure entered the capped Reflexion retry.</summary>
    public sealed record RoleParseRetry(RoleName Role, string Error, int NextInvocation) : RunObservation;

    public sealed record GateStarted(string Gate, IReadOnlyList<string> Command) : RunObservation;

    public sealed record GateFinished(GateCommandResult Result) : RunObservation;

    /// <summary>Checkpoint branch created or switched to (Implementer runs only).</summary>
    public sealed record BranchReady(string Branch, bool Created) : RunObservation;

    /// <summary>A canonical vault record landed ("reconciliation"/"verification"/"plan").</summary>
    public sealed record VaultRecordWritten(string Kind, string Path) : RunObservation;
}

/// <summary>The role-tagging bridge between the two observer seams: Core services know
/// which role is running, the executor does not, so each invocation wraps the run
/// observer in one of these and hands it to <see cref="IClaudeExecutor.ExecuteAsync"/>.
/// Inherits the executor observer's threading contract verbatim — same thread, same
/// ordering, same must-not-throw expectation on the wrapped observer.</summary>
internal sealed class RoleStreamForwarder(IRunObserver observer, RoleName role) : IClaudeStreamObserver
{
    public void OnStreamEvent(ClaudeStreamEvent streamEvent) =>
        observer.OnRunEvent(new RunObservation.RoleStreamEvent(role, streamEvent));
}
