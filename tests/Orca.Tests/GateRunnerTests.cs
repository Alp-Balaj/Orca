using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>Deterministic gates as plain process calls: outcome derived from the exit
/// code, combined output streamed to the run log, tail captured for record bodies, and
/// the timeout backstop killing a hung command. Stand-in scripts, no dotnet builds.</summary>
public sealed class GateRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orca-gate-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string OutputPath(string name) => Path.Combine(_dir, "logs", name + ".txt");

    /// <summary>An OS-appropriate script that prints two lines (one to stdout, one to
    /// stderr) and exits with the given code.</summary>
    private string[] EchoingScript(int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, $"gate-{exitCode}.cmd");
            File.WriteAllText(cmd, $"@echo off\r\necho out-line\r\necho err-line 1>&2\r\nexit /b {exitCode}\r\n");
            return [cmd];
        }

        var sh = Path.Combine(_dir, $"gate-{exitCode}.sh");
        File.WriteAllText(sh, $"#!/bin/sh\necho out-line\necho err-line 1>&2\nexit {exitCode}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return [sh];
    }

    private static string[] SleepingScript() =>
        OperatingSystem.IsWindows()
            ? ["ping", "-n", "30", "127.0.0.1"]
            : ["sleep", "30"];

    /// <summary>A script that prints <paramref name="count"/> numbered lines
    /// (<c>line-1</c> .. <c>line-N</c>) and exits 0.</summary>
    private string[] NumberedLinesScript(int count)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, $"lines-{count}.cmd");
            var body = string.Concat(Enumerable.Range(1, count).Select(i => $"echo line-{i}\r\n"));
            File.WriteAllText(cmd, $"@echo off\r\n{body}exit /b 0\r\n");
            return [cmd];
        }

        var sh = Path.Combine(_dir, $"lines-{count}.sh");
        var shBody = string.Concat(Enumerable.Range(1, count).Select(i => $"echo line-{i}\n"));
        File.WriteAllText(sh, $"#!/bin/sh\n{shBody}exit 0\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return [sh];
    }

    [Fact]
    public void Exit_zero_is_a_pass_with_output_captured()
    {
        var result = new GateRunner(timeout: null)
            .Run("build", EchoingScript(0), _dir, OutputPath("build"));

        Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        var output = File.ReadAllText(result.OutputPath);
        Assert.Contains("out-line", output);
        Assert.Contains("err-line", output);
        Assert.Contains("out-line", result.OutputTail);
    }

    [Fact]
    public void A_nonzero_exit_is_a_fail_not_an_exception()
    {
        var result = new GateRunner(timeout: null)
            .Run("tests", EchoingScript(3), _dir, OutputPath("tests"));

        Assert.Equal(GateOutcome.Fail, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("err-line", result.OutputTail);
    }

    [Fact]
    public void A_missing_command_is_an_environment_error_not_a_gate_fail()
    {
        var ex = Assert.Throws<GateException>(() => new GateRunner(timeout: null)
            .Run("build", ["orca-no-such-binary-xyz"], _dir, OutputPath("missing")));

        Assert.Contains("could not start", ex.Message);
        Assert.Contains("on PATH", ex.Message);
    }

    [Fact]
    public void The_timeout_kills_the_process_tree_and_fails_the_gate()
    {
        var result = new GateRunner(TimeSpan.FromMilliseconds(500))
            .Run("tests", SleepingScript(), _dir, OutputPath("hung"));

        Assert.True(result.TimedOut);
        Assert.Equal(GateOutcome.Fail, result.Outcome);
        Assert.Contains("process tree killed", File.ReadAllText(result.OutputPath));
        Assert.Contains("process tree killed", result.OutputTail);
    }

    [Fact]
    public void The_tail_keeps_exactly_the_last_60_lines_verbatim()
    {
        var result = new GateRunner(timeout: null)
            .Run("tests", NumberedLinesScript(75), _dir, OutputPath("many-lines"));

        var tailLines = result.OutputTail.Split('\n');
        Assert.Equal(GateRunner.TailLineCount, tailLines.Length);
        Assert.Equal("line-16", tailLines[0]); // lines 1-15 pushed out by the 60-line cap
        Assert.Equal("line-75", tailLines[^1]);
        Assert.DoesNotContain("line-15", result.OutputTail.Split('\n'));

        // The full output on disk is unaffected by the tail cap — only the excerpt is capped.
        var fullOutput = File.ReadAllText(result.OutputPath);
        Assert.Contains("line-1", fullOutput);
        Assert.Contains("line-75", fullOutput);
    }

    [Fact]
    public void An_empty_command_refuses_loudly()
    {
        var ex = Assert.Throws<GateException>(() => new GateRunner(timeout: null)
            .Run("build", [], _dir, OutputPath("empty")));

        Assert.Contains("commands.build", ex.Message);
    }

    [Fact]
    public void A_pre_cancelled_token_never_spawns_the_gate()
    {
        // BF-05: Ctrl-C confirmed before the gate starts must not launch it at all.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new GateRunner(timeout: null);

        Assert.Throws<OperationCanceledException>(() =>
            runner.Run("build", EchoingScript(0), _dir, OutputPath("pre-cancelled"), cts.Token));

        Assert.False(File.Exists(OutputPath("pre-cancelled")));
    }

    [Fact]
    public async Task Cancellation_mid_run_kills_the_gate_and_leaves_the_note_in_the_log()
    {
        // BF-05: the interrupt used to wait out the whole suite while the modal claimed
        // "process tree killed" — now the running gate actually dies.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var runner = new GateRunner(timeout: null);
        var outputPath = OutputPath("cancelled-mid-run");

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => runner.Run("tests", SleepingScript(), _dir, outputPath, cts.Token)));
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20),
            $"gate should die on cancellation, not run out its 30s sleep (took {elapsed.Elapsed})");
        Assert.Contains("interrupted — process tree killed", File.ReadAllText(outputPath));
    }

    [Fact]
    public void A_gate_that_exits_but_leaves_a_child_holding_the_pipes_returns_after_the_drain_grace()
    {
        // BF-06 variant 1: `npm test`-style — exits 0, but a spawned watcher inherits
        // stdout and holds it open. The old unconditional WaitForExit() hung forever.
        if (OperatingSystem.IsWindows())
            return; // the POSIX shape (inherited fd held by a backgrounded child) is the captured bug

        var sh = Path.Combine(_dir, "lingering.sh");
        File.WriteAllText(sh, "#!/bin/sh\nsleep 30 &\necho done\nexit 0\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new GateRunner(timeout: null, drainGrace: TimeSpan.FromMilliseconds(500));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = runner.Run("tests", [sh], _dir, OutputPath("lingering"));
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15),
            $"the drain must be grace-bounded, not wait out the 30s child (took {elapsed.Elapsed})");
        Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Contains("done", result.OutputTail);
        Assert.Contains("output truncated — a child process kept the pipes open", result.OutputTail);
    }

    [Fact]
    public void The_tail_keeps_the_LAST_sixty_lines_not_the_first()
    {
        // BF-17: nothing pinned the direction — a first-N regression would hand the
        // Verifier the wrong excerpt (the interesting failures are at the end).
        var runner = new GateRunner(timeout: null);

        var result = runner.Run("build", NumberedLinesScript(100), _dir, OutputPath("tail"));

        Assert.DoesNotContain("line-1\n", result.OutputTail + "\n");
        Assert.Contains("line-41", result.OutputTail);
        Assert.Contains("line-100", result.OutputTail);
    }
}
