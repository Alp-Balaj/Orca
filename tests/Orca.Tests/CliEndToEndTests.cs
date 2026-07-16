namespace Orca.Tests;

/// <summary>Proves the process boundary end to end by spawning the real built CLI (plan
/// clean-exits, checkpoint 2) — the one thing no in-process call into <c>CommandApp</c> can
/// show: that a refusal actually lands on stderr, stdout stays empty, the exit code is the
/// one a script would branch on, and nothing leaks an unhandled-exception stack.</summary>
public sealed class CliEndToEndTests
{
    private static string NewEmptyDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orca-cli-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("nosuchcmd")]
    [InlineData("plan-generate", "myplan")]
    [InlineData("plan-init", "x")]
    public void Bad_invocations_refuse_clean_with_the_message_on_stderr_and_empty_stdout(params string[] args)
    {
        var dir = NewEmptyDir();
        try
        {
            var result = CliProcess.Run(dir, args);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StdOut);
            Assert.StartsWith("error: ", result.StdErr, StringComparison.Ordinal);
            Assert.False(result.LooksLikeACrash);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Status_in_a_non_git_directory_refuses_clean_with_the_message_on_stderr_and_empty_stdout()
    {
        var dir = NewEmptyDir();
        try
        {
            var result = CliProcess.Run(dir, "status");

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StdOut);
            Assert.StartsWith("error: ", result.StdErr, StringComparison.Ordinal);
            Assert.False(result.LooksLikeACrash);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Help_exits_0_with_help_on_stdout_and_nothing_on_stderr()
    {
        var dir = NewEmptyDir();
        try
        {
            var result = CliProcess.Run(dir, "--help");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("USAGE", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.StdErr);
            Assert.False(result.LooksLikeACrash);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
