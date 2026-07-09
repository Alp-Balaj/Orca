using System.Diagnostics;
using Orca.Core.Domain;

namespace Orca.Core.Execution;

/// <summary>Process wrapper around exactly one <c>claude -p --safe-mode</c> invocation
/// (spec §6) — a wrapper, not a reasoning loop. One invocation per checkpoint attempt is
/// an architectural boundary owned by callers; this class knows nothing about checkpoints
/// beyond the run-log paths it is handed.
/// <para>Auth is the machine's interactive <c>claude</c> subscription login. There is no
/// <c>ANTHROPIC_API_KEY</c> handling and no pre-flight credential check, by owner
/// decision: a logged-out machine fails loudly via the CLI's own
/// <c>authentication_failed</c> event (exit 1, <c>result.is_error: true</c> — see the
/// logged-out fixture).</para></summary>
public sealed class ClaudeCodeExecutor : IClaudeExecutor
{
    public async Task<ClaudeExecutionResult> ExecuteAsync(
        ClaudeInvocationOptions options,
        RunLogPaths logPaths,
        IClaudeStreamObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        var argv = ClaudeArguments.Build(options);

        var executable = options.ClaudeExecutable ?? ClaudeExecutableResolver.Resolve()
            ?? throw new ExecutorException(
                "claude CLI not found on PATH — install Claude Code and log in ('claude' interactive) before running the executor");
        if (!File.Exists(executable))
            throw new ExecutorException($"claude executable does not exist: {executable}");
        if (!Directory.Exists(options.WorkingDirectory))
            throw new ExecutorException($"working directory does not exist: {options.WorkingDirectory}");

        using var runLog = RunLogWriter.Create(logPaths.EventLog);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in argv)
            startInfo.ArgumentList.Add(argument);

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new ExecutorException($"failed to start claude process: {executable}");

        // 2.1.201 stalls ~3s waiting on stdin in -p mode; close it at launch.
        process.StandardInput.Close();

        string? sessionId = null;
        ClaudeResultSummary? result = null;
        var authenticationFailed = false;
        RateLimitInfo? lastRateLimit = null;
        StreamParseException? parseFailure = null;
        var observerFailureRecorded = false;

        // Log-before-parse, and keep draining after a parse failure: the pipe must not
        // fill up (that would block the CLI), and the log must stay complete evidence.
        // The observer sees the parsed event only after the raw line is logged — a second
        // sink beside the log, never a gate: its exceptions are recorded once and never
        // fail the invocation, and it never sees an unparseable line.
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                runLog.WriteLine(line);
                ClaudeStreamEvent streamEvent;
                try
                {
                    streamEvent = ClaudeStreamEvent.Parse(line);
                }
                catch (StreamParseException e)
                {
                    parseFailure ??= e;
                    continue;
                }

                if (streamEvent.IsInit)
                    sessionId ??= streamEvent.SessionId;
                if (streamEvent.Result is not null)
                    result = streamEvent.Result;
                if (streamEvent.Error == "authentication_failed")
                    authenticationFailed = true;
                if (streamEvent.RateLimit is not null)
                    lastRateLimit = streamEvent.RateLimit;

                if (observer is null)
                    continue;
                try
                {
                    observer.OnStreamEvent(streamEvent);
                }
                catch (Exception e)
                {
                    if (observerFailureRecorded)
                        continue;
                    observerFailureRecorded = true;
                    runLog.WriteExecutorEvent("observer_failure", new Dictionary<string, object?>
                    {
                        ["observer"] = observer.GetType().FullName,
                        ["exception"] = e.GetType().FullName,
                        ["message"] = e.Message,
                    });
                }
            }
        }, CancellationToken.None);

        var stderrTask = Task.Run(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPaths.Stderr)!);
            using var stderr = new FileStream(logPaths.Stderr, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None);
        }, CancellationToken.None);

        var timedOut = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Hard kill of the whole tree; recorded as a timeout, never conflated with
            // the CLI's own exit code. A caller-requested cancellation kills the same
            // way, then rethrows below.
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Lost the race: the process exited between the deadline and the kill.
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }

        // Both pumps end at EOF once the process (and its descendants' pipe handles) are gone.
        await stdoutTask;
        await stderrTask;
        stopwatch.Stop();

        if (timedOut)
        {
            runLog.WriteExecutorEvent("timeout_kill", new Dictionary<string, object?>
            {
                ["timeout_ms"] = (long)options.Timeout.TotalMilliseconds,
                ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
            });
        }

        var execution = new ClaudeExecutionResult
        {
            Outcome = timedOut ? ExecutionOutcome.TimedOut : ExecutionOutcome.Completed,
            ExitCode = process.ExitCode,
            SessionId = sessionId,
            Result = result,
            AuthenticationFailed = authenticationFailed,
            LastRateLimit = lastRateLimit,
            Elapsed = stopwatch.Elapsed,
        };

        runLog.WriteExecutorEvent("process_exit", new Dictionary<string, object?>
        {
            ["exit_code"] = process.ExitCode,
            ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
            ["timed_out"] = timedOut,
            ["stop_reason"] = InvocationStopReasons.ToWire(execution.StopReason),
        });

        cancellationToken.ThrowIfCancellationRequested();

        if (parseFailure is not null)
        {
            runLog.WriteExecutorEvent("stream_parse_failure", new Dictionary<string, object?>
            {
                ["message"] = parseFailure.Message,
            });
            throw parseFailure;
        }

        return execution;
    }
}

public enum ExecutionOutcome
{
    /// <summary>The process exited on its own; consult <see cref="ClaudeExecutionResult.ExitCode"/>
    /// and <see cref="ClaudeResultSummary.IsError"/> for the actual verdict.</summary>
    Completed,

    /// <summary>Killed by the wall-clock ceiling. The exit code is the kill's, not the
    /// CLI's — surfaced separately so a timeout is never mistaken for a CLI failure.</summary>
    TimedOut,
}

/// <summary>What one invocation produced. <see cref="Result"/> is null when no
/// <c>result</c> event arrived (timeout kill, crash); <see cref="SessionId"/> is null
/// when not even the init event arrived.</summary>
public sealed record ClaudeExecutionResult
{
    public required ExecutionOutcome Outcome { get; init; }
    public required int ExitCode { get; init; }
    public required string? SessionId { get; init; }
    public required ClaudeResultSummary? Result { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>True when any stream event carried <c>error: "authentication_failed"</c> —
    /// the machine-readable auth signal (the <c>result</c> event hides auth failures
    /// behind <c>subtype: "success"</c>).</summary>
    public bool AuthenticationFailed { get; init; }

    /// <summary>The last <c>rate_limit_event</c> observed, if any — healthy runs emit
    /// these too (<c>status: "allowed"</c>), so presence alone is not a failure.</summary>
    public RateLimitInfo? LastRateLimit { get; init; }

    /// <summary>Derived, never stored separately, so it can't disagree with the
    /// evidence.</summary>
    public InvocationStopReason StopReason => InvocationStopReasons.Classify(this);
}
