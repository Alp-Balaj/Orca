namespace Orca.Core.Execution;

/// <summary>Read-only, per-invocation observer of the parsed stream. Called once per
/// event, in arrival order, on the executor's stdout pump thread — never the caller's.
/// Implementations must return quickly and must not throw: an observer exception is
/// caught, recorded once in the run log as an orca_executor/observer_failure event, and
/// the invocation continues unharmed — the run is never hostage to its audience.</summary>
public interface IClaudeStreamObserver
{
    void OnStreamEvent(ClaudeStreamEvent streamEvent);
}
