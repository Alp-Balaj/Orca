using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Run;
using Orca.Core.Vault;

namespace Orca.Tests;

/// <summary>SUG-09: the transcript filter (deterministic, no LLM), the session-note
/// contract, and the summarizer loop against a fake executor.</summary>
public sealed class SessionSummarizerTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("orca-session-repo-").FullName;
    private readonly string _vault = Directory.CreateTempSubdirectory("orca-session-vault-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
        Directory.Delete(_vault, recursive: true);
    }

    // --- TranscriptExcerpt ---

    [Fact]
    public void Excerpt_keeps_assistant_prose_and_tool_errors_and_drops_payloads()
    {
        var log = Path.Combine(_repo, "events.ndjson");
        File.WriteAllLines(log,
        [
            """{"type":"system","subtype":"init","session_id":"s"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"I will add the Tag entity."},{"type":"tool_use","name":"Write","input":{"content":"HUGE PAYLOAD"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"error CS1002: ; expected"}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"ordinary happy tool output"}]}}""",
            """{"type":"result","result":"Added Tag entity with tests.","total_cost_usd":0.25}""",
            "not json at all",
        ]);

        var excerpt = TranscriptExcerpt.Extract(log);

        Assert.Contains("assistant: I will add the Tag entity.", excerpt);
        Assert.Contains("tool_error: error CS1002", excerpt);
        Assert.Contains("final result: Added Tag entity with tests.", excerpt);
        Assert.DoesNotContain("HUGE PAYLOAD", excerpt);
        Assert.DoesNotContain("ordinary happy tool output", excerpt);
    }

    [Fact]
    public void Excerpt_is_byte_capped_with_a_truncation_marker()
    {
        var log = Path.Combine(_repo, "big.ndjson");
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}}""";
        File.WriteAllLines(log, Enumerable.Repeat(line, 200));

        var excerpt = TranscriptExcerpt.Extract(log, maxBytes: 2000);

        Assert.True(excerpt.Length < 3000);
        Assert.Contains("excerpt truncated", excerpt);
    }

    // --- SessionNoteParser ---

    private const string ValidNote =
        """
        ---
        schema_version: 1
        type: session
        plan_id: recipe-tagging
        checkpoint_id: 01-tag-entity
        attempt: 1
        created: 2026-07-17T15:00:00Z
        ---

        ## What changed

        Added the Tag entity to [[recipe-tagging]] (src/Db/Tag.cs).

        ## Surprises & gotchas

        (none)

        ## Verification

        verdict: pass — nothing flagged.
        """;

    [Fact]
    public void A_contract_conforming_note_parses_and_a_transcript_dump_is_refused()
    {
        SessionNoteParser.Parse(ValidNote, "/vault/Sessions/x.md", "recipe-tagging", "01-tag-entity", 1);

        var dump = ValidNote + "\n" + string.Join('\n', Enumerable.Repeat("transcript line", 100));
        var ex = Assert.Throws<VaultSchemaException>(() =>
            SessionNoteParser.Parse(dump, "/vault/Sessions/x.md", "recipe-tagging", "01-tag-entity", 1));
        Assert.Contains("knowledge, not logs", ex.Message);
    }

    [Fact]
    public void The_note_identity_is_pinned_to_the_request()
    {
        var ex = Assert.Throws<VaultSchemaException>(() =>
            SessionNoteParser.Parse(ValidNote, "/vault/Sessions/x.md", "other-plan", "01-tag-entity", 1));
        Assert.Contains("'plan_id' must be exactly 'other-plan'", ex.Message);
    }

    // --- SessionSummarizer loop ---

    private OrcaConfig Config(bool withSummarizer = true) => new()
    {
        RepoConfigPath = Path.Combine(_repo, "orca.json"),
        GlobalConfigPath = Path.Combine(_repo, "global.json"),
        VaultRoot = _vault,
        VaultName = "test-vault",
        PlansDir = "Plans",
        BuildCommand = ["dotnet", "build"],
        TestCommand = ["dotnet", "test"],
        GateTimeout = null,
        MaxParseRetries = 1,
        MaxCheckpointAttempts = 3,
        PlanningCandidates = null,
        DeniedTools = [],
        RoleSections = new RolesSection
        {
            Summarizer = withSummarizer
                ? new RoleSection { AllowedTools = ["Read", "Write", "Edit"], MaxTurns = 10, TimeoutSeconds = 300, Model = "haiku" }
                : null,
        },
    };

    private sealed class ScriptedExecutor(Queue<(ClaudeExecutionResult Result, Action? SideEffect)> script) : IClaudeExecutor
    {
        public List<ClaudeInvocationOptions> Invocations { get; } = [];

        public Task<ClaudeExecutionResult> ExecuteAsync(
            ClaudeInvocationOptions options, RunLogPaths logPaths,
            IClaudeStreamObserver? observer = null, CancellationToken cancellationToken = default)
        {
            Invocations.Add(options);
            var (result, sideEffect) = script.Dequeue();
            sideEffect?.Invoke();
            return Task.FromResult(result);
        }
    }

    private static ClaudeExecutionResult Success => new()
    {
        Outcome = ExecutionOutcome.Completed,
        ExitCode = 0,
        SessionId = "s",
        Result = new ClaudeResultSummary
        {
            Subtype = "success",
            IsError = false,
            SessionId = "s",
            NumTurns = 2,
            DurationMs = 900,
            TotalCostUsd = 0.02m,
            Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1, CacheCreationInputTokens = 0, CacheReadInputTokens = 0 },
        },
        Elapsed = TimeSpan.FromSeconds(1),
    };

    [Fact]
    public async Task Writes_and_validates_the_session_note_with_the_filtered_evidence_in_the_prompt()
    {
        var config = Config();
        var layout = new VaultLayout(_vault, "Plans");
        var notePath = layout.SessionFile(new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero), "recipe-tagging", "01-tag-entity");
        var executor = new ScriptedExecutor(new([
            (Success, () => { Directory.CreateDirectory(Path.GetDirectoryName(notePath)!); File.WriteAllText(notePath, ValidNote); }),
        ]));
        var summarizer = new SessionSummarizer(config, executor, _repo);

        var outcome = await summarizer.WriteSessionNoteAsync(
            TuiTestData.MakePreview("recipe-tagging", "01-tag-entity"), 1,
            "abc123def456 — orca: recipe-tagging/01-tag-entity attempt 1",
            new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero));

        Assert.Equal(notePath, outcome.NotePath);
        Assert.Null(outcome.FailureDetail);
        Assert.Equal(0.02m, outcome.CostUsd);

        var prompt = Assert.Single(executor.Invocations).Prompt;
        Assert.Contains("# Orca Summarizer", prompt);
        Assert.Contains("approved commit: abc123def456", prompt);
        Assert.Contains("Transcript excerpt", prompt);
        Assert.Equal(_vault, Assert.Single(executor.Invocations).WorkingDirectory);
    }

    [Fact]
    public async Task A_malformed_note_retries_once_then_reports_without_throwing()
    {
        var config = Config();
        var layout = new VaultLayout(_vault, "Plans");
        var notePath = layout.SessionFile(new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero), "recipe-tagging", "01-tag-entity");
        var executor = new ScriptedExecutor(new([
            (Success, () => { Directory.CreateDirectory(Path.GetDirectoryName(notePath)!); File.WriteAllText(notePath, "garbage"); }),
            (Success, () => File.WriteAllText(notePath, "still garbage")),
        ]));
        var summarizer = new SessionSummarizer(config, executor, _repo);

        var outcome = await summarizer.WriteSessionNoteAsync(
            TuiTestData.MakePreview("recipe-tagging", "01-tag-entity"), 1, null,
            new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero));

        Assert.Null(outcome.NotePath);
        Assert.Contains("exhausting maxParseRetries", outcome.FailureDetail);
        Assert.Equal(0.04m, outcome.CostUsd);
        Assert.Contains("Retry — your previous output failed validation", executor.Invocations[1].Prompt);
    }
}
