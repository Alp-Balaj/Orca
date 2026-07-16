using Orca.Core.Domain;
using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>Process mechanics only — no quota. The timeout test launches the real CLI
/// and kills it in 250ms, long before any API response; the fixture-replay tests spawn a
/// stand-in script that cats a captured transcript; the failure-path tests never spawn at
/// all. The full billed loop lives in <see cref="ClaudeCliContractTests"/>.</summary>
public class ClaudeCodeExecutorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orca-executor-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ClaudeInvocationOptions Options() => new()
    {
        Prompt = "Read the file README.md and summarize it in one sentence",
        WorkingDirectory = _dir,
        AllowedTools = ["Read"],
        MaxTurns = 1,
        Timeout = TimeSpan.FromMilliseconds(250),
    };

    private RunLogPaths Paths(int attempt = 1) =>
        new RunLogLayout(_dir).AttemptPaths("orca-build", "04-claude-code-executor", attempt);

    /// <summary>An OS-appropriate script that replays a captured transcript on stdout
    /// and exits with the fixture's real exit code — full stream mechanics, no CLI, no
    /// quota. <paramref name="appendLine"/> adds one trailing stdout line after the
    /// fixture (the unparseable-line tests).</summary>
    private string FakeCliReplaying(string fixtureName, int exitCode, string? appendLine = null)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", fixtureName);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "fake-claude.cmd");
            var append = appendLine is null ? "" : $"echo {appendLine}\r\n";
            File.WriteAllText(cmd, $"@echo off\r\ntype \"{fixture}\"\r\n{append}exit /b {exitCode}\r\n");
            return cmd;
        }

        var sh = Path.Combine(_dir, "fake-claude.sh");
        var appendSh = appendLine is null ? "" : $"echo '{appendLine}'\n";
        File.WriteAllText(sh, $"#!/bin/sh\ncat \"{fixture}\"\n{appendSh}exit {exitCode}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private static string[] FixtureLines(string fixtureName) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", fixtureName))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    /// <summary>A fake CLI that proves what actually crossed the process boundary: it
    /// dumps its own argv to <paramref name="argvCapture"/> and copies stdin verbatim to
    /// <paramref name="stdinCapture"/> before replaying the fixture, exactly like
    /// <see cref="FakeCliReplaying"/> otherwise.</summary>
    private string FakeCliCapturingStdin(string stdinCapture, string argvCapture, string fixtureName, int exitCode)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", fixtureName);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "fake-claude-stdin.cmd");
            File.WriteAllText(cmd,
                "@echo off\r\n" +
                $"echo %* > \"{argvCapture}\"\r\n" +
                // `more` appends a trailing CRLF; capture stdin byte-for-byte instead.
                $"powershell -NoProfile -Command \"[IO.File]::WriteAllText('{stdinCapture}', [Console]::In.ReadToEnd())\"\r\n" +
                $"type \"{fixture}\"\r\n" +
                $"exit /b {exitCode}\r\n");
            return cmd;
        }

        var sh = Path.Combine(_dir, "fake-claude-stdin.sh");
        File.WriteAllText(sh,
            "#!/bin/sh\n" +
            $"printf '%s' \"$*\" > \"{argvCapture}\"\n" +
            $"cat > \"{stdinCapture}\"\n" +
            $"cat \"{fixture}\"\n" +
            $"exit {exitCode}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    [Fact]
    public async Task Auth_failure_replay_is_classified_and_logged_distinctly()
    {
        var options = Options() with
        {
            ClaudeExecutable = FakeCliReplaying("logged-out.ndjson", exitCode: 1),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var paths = Paths();

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.AuthenticationFailed);
        Assert.Equal(InvocationStopReason.AuthFailed, result.StopReason);

        // Recorded distinctly in the run log too (the approved stop-reason contract).
        var log = File.ReadAllLines(paths.EventLog);
        Assert.Contains(log, l => l.Contains("\"process_exit\"") && l.Contains("\"stop_reason\":\"auth_failed\""));
    }

    [Fact]
    public async Task Healthy_replay_succeeds_and_captures_the_allowed_rate_limit_evidence()
    {
        var options = Options() with
        {
            ClaudeExecutable = FakeCliReplaying("success.ndjson", exitCode: 0),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var paths = Paths(attempt: 2);

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths);

        Assert.Equal(InvocationStopReason.Succeeded, result.StopReason);
        Assert.False(result.AuthenticationFailed);
        Assert.Equal("allowed", result.LastRateLimit?.Status);

        var log = File.ReadAllLines(paths.EventLog);
        Assert.Contains(log, l => l.Contains("\"process_exit\"") && l.Contains("\"stop_reason\":\"succeeded\""));
    }

    [Fact]
    public async Task Prompt_arrives_over_stdin_intact_while_argv_never_carries_it()
    {
        var stdinCapture = Path.Combine(_dir, "stdin-capture.txt");
        var argvCapture = Path.Combine(_dir, "argv-capture.txt");
        var options = Options() with
        {
            ClaudeExecutable = FakeCliCapturingStdin(stdinCapture, argvCapture, "success.ndjson", exitCode: 0),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, Paths());

        Assert.Equal(InvocationStopReason.Succeeded, result.StopReason);
        Assert.Equal(options.Prompt, File.ReadAllText(stdinCapture));
        Assert.DoesNotContain(options.Prompt, File.ReadAllText(argvCapture));
    }

    [Fact]
    public async Task Child_exiting_before_reading_stdin_does_not_crash_the_executor()
    {
        // Larger than any OS pipe buffer, so the write blocks until the fake CLI — which
        // never touches stdin — exits and closes its end, guaranteeing a broken-pipe
        // IOException rather than racing to see if the whole prompt fit in the buffer.
        var options = Options() with
        {
            Prompt = new string('a', 300_000),
            ClaudeExecutable = FakeCliReplaying("success.ndjson", exitCode: 0),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var paths = Paths();

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths);

        Assert.Equal(InvocationStopReason.Succeeded, result.StopReason);

        var log = File.ReadAllLines(paths.EventLog);
        Assert.Contains(log, l => l.Contains("\"orca_executor\"") && l.Contains("\"stdin_write_failed\""));
    }

    [Fact]
    public async Task Refuses_an_oversized_prompt_before_touching_the_executable_or_the_run_log()
    {
        var promptBytes = ClaudeCodeExecutor.MaxPromptBytes + 1;
        var options = Options() with
        {
            Prompt = new string('a', promptBytes),
            // Nonexistent path: if the guard didn't fire first, this would fail with a
            // different, later message ("does not exist"), proving ordering by contrast.
            ClaudeExecutable = Path.Combine(_dir, "no-such-claude"),
        };
        var paths = Paths();

        var e = await Assert.ThrowsAsync<ExecutorException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(options, paths));

        Assert.Contains(promptBytes.ToString(), e.Message);
        Assert.Contains(ClaudeCodeExecutor.MaxPromptBytes.ToString(), e.Message);
        Assert.False(File.Exists(paths.EventLog));
    }

    [Fact]
    public async Task Process_start_failure_is_wrapped_never_a_raw_Win32Exception()
    {
        if (OperatingSystem.IsWindows())
            return;

        var fakeExe = Path.Combine(_dir, "not-executable");
        File.WriteAllText(fakeExe, "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(fakeExe, UnixFileMode.UserRead | UnixFileMode.UserWrite); // no execute bit
        var options = Options() with { ClaudeExecutable = fakeExe };

        var e = await Assert.ThrowsAsync<ExecutorException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(options, Paths()));

        Assert.Contains(fakeExe, e.Message);
    }

    [ClaudeCliFact]
    public async Task Timeout_is_a_hard_kill_reported_distinctly_from_exit()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "A README for the timeout test.\n");
        var paths = Paths();

        var result = await new ClaudeCodeExecutor().ExecuteAsync(Options(), paths);

        Assert.Equal(ExecutionOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Result);
        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(250));

        var log = File.ReadAllLines(paths.EventLog);
        Assert.Contains(log, l => l.Contains("\"orca_executor\"") && l.Contains("\"timeout_kill\""));
        Assert.Contains(log, l => l.Contains("\"orca_executor\"") && l.Contains("\"timed_out\":true"));
        Assert.True(File.Exists(paths.Stderr));
    }

    [ClaudeCliFact]
    public void Resolver_finds_the_cli_on_path()
    {
        var resolved = ClaudeExecutableResolver.Resolve();

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public async Task Observer_sees_every_parsed_event_in_arrival_order_before_return()
    {
        var options = Options() with
        {
            ClaudeExecutable = FakeCliReplaying("success.ndjson", exitCode: 0),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var observer = new RecordingObserver();

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, Paths(), observer);

        // ExecuteAsync awaits the pump, so by now the observer has seen the whole
        // stream, in arrival order, one call per parsed event.
        Assert.Equal(InvocationStopReason.Succeeded, result.StopReason);
        Assert.Equal(FixtureLines("success.ndjson"), observer.Events.Select(e => e.RawLine).ToArray());
    }

    [Fact]
    public async Task Observer_exceptions_never_fail_the_run_and_are_recorded_once()
    {
        var options = Options() with
        {
            ClaudeExecutable = FakeCliReplaying("success.ndjson", exitCode: 0),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var paths = Paths();
        var observer = new ThrowingObserver();

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths, observer);

        Assert.Equal(InvocationStopReason.Succeeded, result.StopReason);
        Assert.Equal(FixtureLines("success.ndjson").Length, observer.Calls); // still offered every event

        var log = File.ReadAllLines(paths.EventLog);
        Assert.Equal(1, log.Count(l => l.Contains("\"observer_failure\"")));
        Assert.Contains(log, l =>
            l.Contains("\"orca_executor\"") && l.Contains("\"observer_failure\"") && l.Contains("observer boom"));
    }

    [Fact]
    public async Task Observer_never_sees_an_unparseable_line()
    {
        var options = Options() with
        {
            ClaudeExecutable = FakeCliReplaying("success.ndjson", exitCode: 0, appendLine: "this is not json"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var observer = new RecordingObserver();

        // The invocation still fails loudly at process exit, exactly as without an observer.
        await Assert.ThrowsAsync<StreamParseException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(options, Paths(), observer));

        Assert.Equal(FixtureLines("success.ndjson").Length, observer.Events.Count);
        Assert.DoesNotContain(observer.Events, e => e.RawLine.Contains("not json"));
    }

    private sealed class RecordingObserver : IClaudeStreamObserver
    {
        public List<ClaudeStreamEvent> Events { get; } = [];

        public void OnStreamEvent(ClaudeStreamEvent streamEvent) => Events.Add(streamEvent);
    }

    private sealed class ThrowingObserver : IClaudeStreamObserver
    {
        public int Calls { get; private set; }

        public void OnStreamEvent(ClaudeStreamEvent streamEvent)
        {
            Calls++;
            throw new InvalidOperationException("observer boom");
        }
    }

    [Fact]
    public async Task Fails_loud_when_the_executable_does_not_exist()
    {
        var options = Options() with { ClaudeExecutable = Path.Combine(_dir, "no-such-claude") };

        var e = await Assert.ThrowsAsync<ExecutorException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(options, Paths()));

        Assert.Contains("does not exist", e.Message);
    }

    [Fact]
    public async Task Fails_loud_when_the_working_directory_does_not_exist()
    {
        var fakeExe = Path.Combine(_dir, "claude-stand-in");
        File.WriteAllText(fakeExe, "");
        var options = Options() with
        {
            ClaudeExecutable = fakeExe,
            WorkingDirectory = Path.Combine(_dir, "missing"),
        };

        var e = await Assert.ThrowsAsync<ExecutorException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(options, Paths()));

        Assert.Contains("working directory", e.Message);
    }

    [Fact]
    public async Task Refuses_to_overwrite_an_existing_attempt_log_before_spawning()
    {
        var fakeExe = Path.Combine(_dir, "claude-stand-in");
        File.WriteAllText(fakeExe, "");
        var paths = Paths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.EventLog)!);
        File.WriteAllText(paths.EventLog, "prior attempt evidence");

        var e = await Assert.ThrowsAsync<ExecutorException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(Options() with { ClaudeExecutable = fakeExe }, paths));

        Assert.Contains("refusing to overwrite", e.Message);
    }
}
