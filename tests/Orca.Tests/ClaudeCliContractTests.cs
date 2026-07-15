using Orca.Core.Domain;
using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>The trait-gated contract test: one real, billed <c>claude -p --safe-mode</c>
/// invocation through the full executor, asserting the observed 2.1.201 event anatomy.
/// Its job is to fail loudly on a CLI breaking change before the parser silently
/// misparses. Excluded from default runs by the repo-root <c>.runsettings</c>; run it
/// deliberately with <c>dotnet test --settings tests/integration.runsettings</c>
/// (~$0.20 of subscription quota per run). Requires a logged-in machine.</summary>
[Trait("Category", "Integration")]
public class ClaudeCliContractTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orca-contract-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [ClaudeCliFact]
    public async Task One_real_invocation_matches_the_pinned_contract()
    {
        File.WriteAllText(
            Path.Combine(_dir, "README.md"),
            "Orca is a .NET 10 CLI tool that orchestrates Claude Code as a checkpoint-gated implementer.\n");
        var paths = new RunLogLayout(_dir).AttemptPaths("orca-build", "04-claude-code-executor", 1);
        var options = new ClaudeInvocationOptions
        {
            Prompt = "Read the file README.md and summarize it in one sentence",
            WorkingDirectory = _dir,
            AllowedTools = ["Read"],
            MaxTurns = 8,
            Timeout = TimeSpan.FromMinutes(3),
        };

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths);

        // Process-level verdict.
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);

        // Init event: a UUID session id, persisted for within-checkpoint --resume only.
        Assert.True(Guid.TryParse(result.SessionId, out _), $"session id not a UUID: {result.SessionId}");

        // Result event: the verdict field is is_error; cost and usage must be real.
        Assert.NotNull(result.Result);
        Assert.False(result.Result.IsError);
        Assert.Equal(result.SessionId, result.Result.SessionId);
        Assert.True(result.Result.TotalCostUsd > 0m, "expected a positive USD-equivalent cost");
        Assert.True(result.Result.Usage.InputTokens > 0);
        Assert.True(result.Result.Usage.OutputTokens > 0);
        Assert.InRange(result.Result.NumTurns, 1, 8);

        // Run log: complete, ordered, every line either a parseable CLI event or a
        // namespaced orca_executor fact; init first, exactly one result.
        var lines = File.ReadAllLines(paths.EventLog);
        var events = lines.Select(ClaudeStreamEvent.Parse).ToList();
        Assert.True(events[0].IsInit, "first logged event must be the system/init");
        Assert.Single(events, e => e.Type == "result");
        Assert.Single(events, e => e.Type == "orca_executor" && e.Subtype == "process_exit");
        Assert.DoesNotContain(events, e => e.Type == "orca_executor" && e.Subtype == "timeout_kill");
        Assert.True(File.Exists(paths.Stderr));

        // And the run-state merge the checkpoint criterion asks for.
        var state = RunStateUpdates.Apply(
            new RunState
            {
                SchemaVersion = 1,
                PlanId = "orca-build",
                PlanVersion = 1,
                CheckpointIndex = 3,
                CheckpointId = "04-claude-code-executor",
                Attempt = 1,
                Branch = "orca/orca-build/04-claude-code-executor",
                SessionId = null,
                PromptHashes = new Dictionary<string, string>(),
                AccumulatedCostUsd = 0m,
                LastUsage = null,
                LastStopReason = null,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            result,
            DateTimeOffset.UtcNow);

        Assert.Equal(result.SessionId, state.SessionId);
        Assert.Equal(result.Result.TotalCostUsd, state.AccumulatedCostUsd);
        Assert.Equal(result.Result.Usage, state.LastUsage);
    }

    /// <summary>The real CLI, not a fake, receiving the prompt exclusively over stdin
    /// (<see cref="ClaudeArguments.Build"/> puts no prompt element in argv) in
    /// <c>-p --output-format stream-json</c> mode. A CLI version that silently regressed
    /// to expecting the prompt as an argv element would show up here as a hang past the
    /// timeout or an <c>authentication_failed</c>-shaped no-op, not as a compile error.</summary>
    [ClaudeCliFact]
    public async Task Stdin_delivered_prompt_is_accepted_in_stream_json_mode()
    {
        File.WriteAllText(
            Path.Combine(_dir, "README.md"),
            "Orca is a .NET 10 CLI tool that orchestrates Claude Code as a checkpoint-gated implementer.\n");
        var paths = new RunLogLayout(_dir).AttemptPaths("orca-build", "04-claude-code-executor", 1);
        var options = new ClaudeInvocationOptions
        {
            Prompt = "Read the file README.md and summarize it in one sentence",
            WorkingDirectory = _dir,
            AllowedTools = ["Read"],
            MaxTurns = 8,
            Timeout = TimeSpan.FromMinutes(3),
        };

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Result);
        Assert.False(result.Result.IsError);
        Assert.True(result.Result.TotalCostUsd > 0m, "expected a positive USD-equivalent cost — proves the CLI actually processed the stdin-delivered prompt rather than erroring out immediately");
    }
}
