using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>Process mechanics only — no quota. The timeout test launches the real CLI
/// and kills it in 250ms, long before any API response; the failure-path tests never
/// spawn at all. The full billed loop lives in <see cref="ClaudeCliContractTests"/>.</summary>
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
