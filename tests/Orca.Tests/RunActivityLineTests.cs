using Orca.Cli.Tui;
using Orca.Core.Config;
using Orca.Core.Execution;
using Orca.Core.Run;

namespace Orca.Tests;

/// <summary>The pure projection behind the run view's activity pane (tui-plan build-order
/// steps 5–6). Mirrors StreamEventDisplayTests' shape: assert on the flattened text and the
/// styled runs' roles, no Terminal.Gui shell. Covers both the LIFECYCLE lines (step 5) and
/// the per-token STREAM projected through StreamEventDisplay for all four roles (step 6).</summary>
public sealed class RunActivityLineTests
{
    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    private static string[] FixtureLines(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", name))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private static TuiRole RoleOf(IReadOnlyList<StyledLine> lines, string runText)
    {
        foreach (var line in lines)
            foreach (var run in line.Runs)
                if (run.Text == runText)
                    return run.Role;
        throw new Xunit.Sdk.XunitException($"no run with text '{runText}' in:\n{Plain(lines)}");
    }

    [Fact]
    public void Role_started_tags_the_role_and_shows_the_attempt()
    {
        var lines = RunActivityLine.Describe(
            new RunObservation.RoleStarted(RoleName.Implementer, Attempt: 2, Invocation: 1, EventLogPath: ".orca/x"));

        Assert.Contains("[implementer] ", Plain(lines));
        Assert.Contains("started  attempt 2", Plain(lines));
        Assert.DoesNotContain("invocation", Plain(lines)); // first invocation is not a re-ask
    }

    [Fact]
    public void Role_started_marks_a_parse_retry_invocation_past_the_first()
    {
        var lines = RunActivityLine.Describe(
            new RunObservation.RoleStarted(RoleName.Verifier, Attempt: 1, Invocation: 2, EventLogPath: ".orca/x"));

        Assert.Contains("invocation 2", Plain(lines));
    }

    [Fact]
    public void Role_finished_success_shows_the_result_facts()
    {
        var lines = RunActivityLine.Describe(new RunObservation.RoleFinished(
            RoleName.Implementer, TuiTestData.MakeExecution(cost: 0.5m, turns: 12, durationMs: 63_000)));

        var text = Plain(lines);
        Assert.Contains("[implementer] ", text);
        Assert.Contains("done  $0.50 · 12 turns · 1m3s", text);
        Assert.Equal(TuiRole.Success, RoleOf(lines, "✓ "));
    }

    [Fact]
    public void Role_finished_failure_reads_as_failed_never_done()
    {
        var lines = RunActivityLine.Describe(new RunObservation.RoleFinished(
            RoleName.Implementer, TuiTestData.MakeExecution(error: true)));

        Assert.Contains("[implementer] ✗ failed", Plain(lines));
        Assert.Equal(TuiRole.Error, RoleOf(lines, "✗ "));
    }

    [Fact]
    public void Role_finished_timeout_says_so_and_has_no_result_facts()
    {
        var lines = RunActivityLine.Describe(new RunObservation.RoleFinished(
            RoleName.Reconciler, TuiTestData.MakeExecution(outcome: ExecutionOutcome.TimedOut)));

        var text = Plain(lines);
        Assert.Contains("failed", text);
        Assert.Contains("timed out (killed)", text);
        Assert.DoesNotContain("turns", text);
    }

    [Fact]
    public void Parse_retry_shows_the_next_invocation_and_clips_the_error()
    {
        var lines = RunActivityLine.Describe(new RunObservation.RoleParseRetry(
            RoleName.Verifier, Error: "criterion 2 missing a note\nsecond line", NextInvocation: 2));

        var text = Plain(lines);
        Assert.Contains("parse retry → invocation 2", text);
        Assert.Contains("criterion 2 missing a note", text);
        Assert.DoesNotContain("second line", text); // first line only
        Assert.Equal(TuiRole.Warning, RoleOf(lines, "↻ "));
    }

    [Fact]
    public void Gate_started_and_finished_mirror_the_cli_gate_line()
    {
        var started = RunActivityLine.Describe(
            new RunObservation.GateStarted("build", ["dotnet", "build"]));
        Assert.Contains("[gates] ", Plain(started));
        Assert.Contains("build  dotnet build", Plain(started));

        var passed = RunActivityLine.Describe(
            new RunObservation.GateFinished(TuiTestData.MakeGate("build", exitCode: 0, seconds: 3.2)));
        Assert.Contains("[gates] build ✓ (exit 0, 3.2s)", Plain(passed));
        Assert.Equal(TuiRole.Success, RoleOf(passed, "✓"));

        var failed = RunActivityLine.Describe(
            new RunObservation.GateFinished(TuiTestData.MakeGate("tests", exitCode: 1, seconds: 1.5)));
        Assert.Contains("[gates] tests ✗ (exit 1, 1.5s)", Plain(failed));

        var killed = RunActivityLine.Describe(
            new RunObservation.GateFinished(TuiTestData.MakeGate("tests", exitCode: -1, seconds: 600, timedOut: true)));
        Assert.Contains("killed after 600.0s — timeout", Plain(killed));
    }

    [Fact]
    public void Branch_ready_marks_new_versus_existing()
    {
        Assert.Contains("⎇ orca/alpha/01-a  (new)",
            Plain(RunActivityLine.Describe(new RunObservation.BranchReady("orca/alpha/01-a", Created: true))));
        Assert.Contains("(existing)",
            Plain(RunActivityLine.Describe(new RunObservation.BranchReady("orca/alpha/01-a", Created: false))));
    }

    [Fact]
    public void Vault_record_tags_the_kind_and_shortens_the_path()
    {
        var lines = RunActivityLine.Describe(new RunObservation.VaultRecordWritten(
            "verification", "/vault/Plans/alpha/verification/01-a.attempt-1.md"));

        var text = Plain(lines);
        Assert.Contains("[verification] ", text);
        Assert.Contains("verification/01-a.attempt-1.md", text);
    }

    [Fact]
    public void Stream_tool_use_is_role_tagged_and_carries_the_display_text_verbatim()
    {
        var raw = ClaudeStreamEvent.Parse(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"src/Foo.cs"}}]}}""");
        var lines = RunActivityLine.Describe(new RunObservation.RoleStreamEvent(RoleName.Implementer, raw));

        var line = Assert.Single(lines);
        // Same faint [role] gutter the lifecycle lines wear, plus the ⚒ tool-use glyph, and
        // the text is StreamEventDisplay's verbatim — so the two sources stay aligned.
        Assert.Equal("[implementer] ⚒ Read(src/Foo.cs)", line.PlainText());
        Assert.Equal(TuiRole.Faint, RoleOf(lines, "[implementer] "));
        Assert.Equal(TuiRole.Accent, RoleOf(lines, "⚒ "));
    }

    [Fact]
    public void Stream_assistant_text_reads_as_a_write_line()
    {
        var raw = ClaudeStreamEvent.Parse(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Adding the endpoint"}]}}""");
        var lines = RunActivityLine.Describe(new RunObservation.RoleStreamEvent(RoleName.Planner, raw));

        Assert.Equal("[planner] ✎ Adding the endpoint", Assert.Single(lines).PlainText());
        Assert.Equal(TuiRole.Accent, RoleOf(lines, "✎ "));
    }

    [Fact]
    public void One_stream_event_can_yield_several_lines()
    {
        // An assistant message carrying text plus two tool_use blocks projects to three lines.
        var raw = ClaudeStreamEvent.Parse(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"go"},{"type":"tool_use","name":"Read","input":{"file_path":"a"}},{"type":"tool_use","name":"Write","input":{"file_path":"b"}}]}}""");
        var lines = RunActivityLine.Describe(new RunObservation.RoleStreamEvent(RoleName.Verifier, raw));

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("[verifier] ", l.PlainText()));
    }

    [Theory]
    [InlineData(RoleName.Planner)]
    [InlineData(RoleName.Implementer)]
    [InlineData(RoleName.Reconciler)]
    [InlineData(RoleName.Verifier)]
    public void Every_success_transcript_line_projects_to_a_role_tagged_line_for_every_role(RoleName role)
    {
        // The live pane is the flagship feature precisely because it covers all four roles;
        // each committed transcript event must project to a non-empty, gutter-tagged line.
        var gutter = $"[{role.ToString().ToLowerInvariant()}] ";
        foreach (var raw in FixtureLines("success.ndjson").Select(ClaudeStreamEvent.Parse))
        {
            var lines = RunActivityLine.Describe(new RunObservation.RoleStreamEvent(role, raw));

            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.StartsWith(gutter, l.PlainText()));
        }
    }

    [Fact]
    public void Stream_result_line_carries_the_same_facts_as_the_lifecycle_finish()
    {
        // The continuity seam: a finished role reads the same via the stream's Result event
        // as via the lifecycle RoleFinished line ("$x · n turns · Nm Ns").
        var result = ClaudeStreamEvent.Parse(FixtureLines("success.ndjson").Last());
        var streamLines = RunActivityLine.Describe(new RunObservation.RoleStreamEvent(RoleName.Implementer, result));

        Assert.Contains("$0.16 · 2 turns · 9.5s", Plain(streamLines));
    }
}
