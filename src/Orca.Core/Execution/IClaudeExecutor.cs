namespace Orca.Core.Execution;

/// <summary>The one-invocation seam <see cref="ClaudeCodeExecutor"/> implements. Exists so
/// checkpoint orchestration is testable without spawning a process — not as an invitation
/// to write a second executor.</summary>
public interface IClaudeExecutor
{
    Task<ClaudeExecutionResult> ExecuteAsync(
        ClaudeInvocationOptions options,
        RunLogPaths logPaths,
        CancellationToken cancellationToken = default);
}
