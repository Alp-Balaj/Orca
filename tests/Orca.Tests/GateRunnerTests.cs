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
    public void An_empty_command_refuses_loudly()
    {
        var ex = Assert.Throws<GateException>(() => new GateRunner(timeout: null)
            .Run("build", [], _dir, OutputPath("empty")));

        Assert.Contains("commands.build", ex.Message);
    }
}
