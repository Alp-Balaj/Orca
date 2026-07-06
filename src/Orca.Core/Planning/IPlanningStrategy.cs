using Orca.Core.Domain;
using Orca.Core.Execution;

namespace Orca.Core.Planning;

/// <summary>A planning run was refused — wrong state, not a bug (the planning twin of
/// <c>CheckpointRunException</c>).</summary>
public class PlanningException(string message) : Exception(message);

/// <summary>What the human asked the Planner to plan. The goal brief is the only
/// free-form input; everything else is identity the generated plan must reproduce
/// exactly.</summary>
public sealed record PlanningRequest
{
    public required string PlanId { get; init; }

    /// <summary>The human's brief, passed to the Planner verbatim.</summary>
    public required string Goal { get; init; }

    /// <summary>Optional title constraint; when null the Planner derives one from the
    /// goal.</summary>
    public string? Title { get; init; }

    public required string TargetRepoName { get; init; }
    public required string BaseBranch { get; init; }
}

/// <summary>Everything one planning run would do, computed without mutating anything —
/// what <c>--dry-run</c> prints and what <see cref="IPlanningStrategy.GenerateAsync"/>
/// executes. <see cref="Attempt"/> already accounts for a resumed run.</summary>
public sealed record PlanningPreview
{
    public required PlanningRequest Request { get; init; }
    public required PlanningStrategyKind Strategy { get; init; }
    public required string PlanFilePath { get; init; }

    /// <summary>Forward-slash path relative to the vault root — what the Planner is told
    /// to write, with the vault as its working directory.</summary>
    public required string RelativePlanPath { get; init; }

    public required int Attempt { get; init; }
    public required int MaxParseRetries { get; init; }
    public required PlanningState? PriorState { get; init; }

    /// <summary>The created/updated value the Planner is told to use — one timestamp per
    /// planning run, so retry prompts stay consistent with the file already written.</summary>
    public required string RequestedTimestamp { get; init; }

    public required string Prompt { get; init; }
    public required string PromptSha256 { get; init; }
    public required ClaudeInvocationOptions Options { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public required string? SettingsJson { get; init; }
    public required RunLogPaths LogPaths { get; init; }
}

/// <summary>How one planning run ended. <see cref="Plan"/> is non-null iff
/// <see cref="PlanningState.Status"/> is <see cref="PlanningRunStatus.Succeeded"/>;
/// <see cref="FailureDetail"/> carries the human-actionable explanation otherwise (the
/// CLI's own words, or the last parse error after retry exhaustion).</summary>
public sealed record PlanningOutcome
{
    public required PlanningState State { get; init; }
    public required string PlanFilePath { get; init; }
    public required Plan? Plan { get; init; }
    public required ClaudeExecutionResult LastExecution { get; init; }
    public required string? FailureDetail { get; init; }
}

/// <summary>Spec §8: the Planner behind a strategy seam. Single-candidate is the MVP
/// path; multi-candidate slots in here later without touching callers. Which strategy a
/// given plan deserves is the human's call at the CLI — never decided in code.</summary>
public interface IPlanningStrategy
{
    PlanningStrategyKind Kind { get; }

    /// <summary>Read-only planning of the run: resolves resume state, assembles the
    /// first prompt, computes the exact argv. Throws instead of guessing.</summary>
    PlanningPreview Prepare(PlanningRequest request);

    /// <summary>The mutating half: executor invocations (initial plus capped parse
    /// retries), planning state, run logs. One call = one human-authorized planning run;
    /// invocation-level failures (auth, rate limit, budget, timeout) end it loudly.</summary>
    Task<PlanningOutcome> GenerateAsync(PlanningPreview preview, CancellationToken cancellationToken = default);
}
