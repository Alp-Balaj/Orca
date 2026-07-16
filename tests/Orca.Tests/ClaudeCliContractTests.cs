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

        // SUG-15: the init event's claude_code_version should still equal what this
        // whole contract test class is pinned against — a captured-but-unchecked field
        // is exactly the blind spot SUG-15 closes. If this starts failing on a real run,
        // it means the CLI has drifted (README.md's Status section pins the same value)
        // and the safe-mode/stream-json contract this class probes needs re-verifying
        // before bumping ClaudeVersionCheck.PinnedVersion to match.
        Assert.Equal(ClaudeVersionCheck.PinnedVersion, result.ClaudeCodeVersion);
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

    /// <summary>Safety layer 2, end to end: a <c>permissions.deny</c> Bash rule must
    /// survive <c>--safe-mode</c> and actually stop the tool call, not just be present in
    /// the settings payload. Proved black-box — a file the denied Bash command would have
    /// created must not exist afterward — rather than by pattern-matching the CLI's own
    /// denial wording in the event stream, which SUG-15's kickoff flagged as exactly the
    /// kind of fact this repo had never pinned (a silently-honored-anyway regression would
    /// only ever be caught by a human noticing something it shouldn't have done).</summary>
    [ClaudeCliFact]
    public async Task A_denied_bash_rule_survives_safe_mode_and_actually_blocks_the_tool()
    {
        var canary = Path.Combine(_dir, "should-not-exist.txt");
        var settingsPath = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settingsPath, PermissionSettings.ToJson(["Bash"]));
        var paths = new RunLogLayout(_dir).AttemptPaths("orca-build", "04-claude-code-executor", 1);
        var options = new ClaudeInvocationOptions
        {
            // Bash is both allowed (proving deny wins over the allowlist, not merely
            // absent from it — the exact contract PermissionSettings.cs documents) and
            // the only tool granted, so there is no other path to the canary file.
            Prompt = $"Use the Bash tool to run exactly this command: touch {canary}\n" +
                     "Then reply in one sentence saying whether it succeeded.",
            WorkingDirectory = _dir,
            AllowedTools = ["Bash"],
            MaxTurns = 8,
            Timeout = TimeSpan.FromMinutes(3),
            SettingsFilePath = settingsPath,
        };

        var result = await new ClaudeCodeExecutor().ExecuteAsync(options, paths);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(canary),
            "permissions.deny for Bash did not survive --safe-mode — the denied tool call actually ran");
    }

    /// <summary>The one path today's fake-process-script tests cannot reach: a real
    /// invocation cancelled mid-flight must kill the whole process tree, drain the
    /// stdout/stderr pumps to completion (proven by this method returning at all, rather
    /// than hanging until the 3-minute Timeout ceiling), and rethrow — never fabricate a
    /// synthetic "succeeded" or "timed out" outcome for a caller-requested cancellation.</summary>
    [ClaudeCliFact(Timeout = 30_000)]
    public async Task Cancelling_mid_flight_kills_the_tree_drains_the_pumps_and_rethrows()
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
            // Generous on purpose: cancellation, not this ceiling, must be what fires.
            Timeout = TimeSpan.FromMinutes(3),
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ClaudeCodeExecutor().ExecuteAsync(options, paths, cancellationToken: cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
        // Never a synthetic result event for a killed attempt.
        Assert.DoesNotContain(File.ReadAllLines(paths.EventLog), l => l.Contains("\"type\":\"result\""));
    }
}
