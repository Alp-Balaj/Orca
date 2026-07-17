using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>The lenient display projection (tui-plan §3.5), exercised against the
/// committed 2.1.201 transcripts. It must never throw, must fall back to the bare event
/// type on anything unrecognized, and must never widen the strict
/// <see cref="ClaudeStreamEvent"/> contract — assistant text and tool calls come from
/// <see cref="ClaudeStreamEvent.RawLine"/> only.</summary>
public class StreamEventDisplayTests
{
    private static string[] FixtureLines(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", name))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private static List<DisplayLine> DescribeFixture(string name) =>
        FixtureLines(name)
            .Select(ClaudeStreamEvent.Parse)
            .SelectMany(StreamEventDisplay.Describe)
            .ToList();

    [Fact]
    public void Success_transcript_projects_the_full_anatomy_in_order()
    {
        var lines = DescribeFixture("success.ndjson");

        Assert.Equal(7, lines.Count);

        Assert.Equal(DisplayKind.Init, lines[0].Kind);
        Assert.Equal("init · claude-fable-5", lines[0].Text);

        Assert.Equal(DisplayKind.RateLimit, lines[1].Kind);
        Assert.Equal("rate limit allowed · five_hour", lines[1].Text);

        // The thinking-only assistant message is not display content; it falls back to
        // the bare event type rather than vanishing from the pane.
        Assert.Equal(DisplayKind.Other, lines[2].Kind);
        Assert.Equal("assistant", lines[2].Text);

        Assert.Equal(DisplayKind.ToolUse, lines[3].Kind);
        Assert.Equal("Read(/home/user/scratch/README.md)", lines[3].Text);

        Assert.Equal(DisplayKind.ToolResult, lines[4].Kind);
        Assert.Equal(
            "1\tOrca is a .NET 10 CLI tool that orchestrates Claude Code as a checkpoint-gated implementer.",
            lines[4].Text);

        // The final text is longer than one display line; it is clipped, never wrapped.
        Assert.Equal(DisplayKind.AssistantText, lines[5].Kind);
        Assert.StartsWith("The README says Orca is a .NET 10 CLI tool", lines[5].Text);
        Assert.EndsWith("…", lines[5].Text);
        Assert.Equal(160, lines[5].Text.Length);

        Assert.Equal(DisplayKind.Result, lines[6].Kind);
        Assert.Equal("$0.16 · 2 turns · 9.5s", lines[6].Text);
    }

    [Fact]
    public void Auth_failure_transcript_shows_the_cli_text_and_an_error_result()
    {
        var lines = DescribeFixture("logged-out.ndjson");

        Assert.Contains(lines, l =>
            l.Kind == DisplayKind.AssistantText && l.Text == "Not logged in · Please run /login");

        // is_error with the lying subtype "success" reads as a plain error, never "success".
        var result = Assert.Single(lines, l => l.Kind == DisplayKind.Result);
        Assert.Equal("error · $0.00 · 1 turn · 0s", result.Text);
    }

    [Fact]
    public void Budget_kill_result_shows_its_distinct_subtype()
    {
        var lines = DescribeFixture("budget-exceeded.ndjson");

        var result = Assert.Single(lines, l => l.Kind == DisplayKind.Result);
        Assert.Equal("error_max_budget_usd · $0.06 · 3 turns · 8.1s", result.Text);
    }

    [Fact]
    public void Denied_tool_call_and_its_denial_are_both_visible()
    {
        var lines = DescribeFixture("denied-by-permission-rules.ndjson");

        Assert.Contains(lines, l =>
            l.Kind == DisplayKind.ToolUse && l.Text == "Bash(echo hello-from-probe, Echo a probe string)");
        Assert.Contains(lines, l =>
            l.Kind == DisplayKind.ToolResult
            && l.Text == "Permission to use Bash with command echo hello-from-probe has been denied.");
    }

    [Theory]
    [InlineData("success.ndjson")]
    [InlineData("logged-out.ndjson")]
    [InlineData("invalid-api-key.ndjson")]
    [InlineData("safe-mode-hook-suppressed.ndjson")]
    [InlineData("denied-by-permission-rules.ndjson")]
    [InlineData("budget-exceeded.ndjson")]
    public void Every_committed_transcript_line_projects_to_nonempty_text(string fixture)
    {
        foreach (var line in FixtureLines(fixture))
        {
            var displayLines = StreamEventDisplay.Describe(ClaudeStreamEvent.Parse(line));

            Assert.NotEmpty(displayLines);
            Assert.All(displayLines, l => Assert.False(string.IsNullOrWhiteSpace(l.Text)));
        }
    }

    [Fact]
    public void Unreadable_or_unknown_shapes_come_back_as_the_bare_event_type()
    {
        // A RawLine the lenient reader cannot even parse: never throws.
        var garbage = new ClaudeStreamEvent { Type = "assistant", RawLine = "not json at all" };
        var line = Assert.Single(StreamEventDisplay.Describe(garbage));
        Assert.Equal(DisplayKind.Other, line.Kind);
        Assert.Equal("assistant", line.Text);

        // An event type the projection has never heard of.
        var unknown = new ClaudeStreamEvent { Type = "telemetry", RawLine = """{"type":"telemetry"}""" };
        Assert.Equal("telemetry", Assert.Single(StreamEventDisplay.Describe(unknown)).Text);

        // The subtype rides along in the fallback name.
        var subtyped = new ClaudeStreamEvent { Type = "system", Subtype = "warning", RawLine = "{}" };
        Assert.Equal("system/warning", Assert.Single(StreamEventDisplay.Describe(subtyped)).Text);

        // A rate_limit_event whose info block drifted out of readable shape.
        var drifted = new ClaudeStreamEvent { Type = "rate_limit_event", RawLine = """{"type":"rate_limit_event"}""" };
        Assert.Equal("rate_limit_event", Assert.Single(StreamEventDisplay.Describe(drifted)).Text);
    }

    [Fact]
    public void The_clip_cap_never_splits_a_grapheme_cluster()
    {
        // BF-15: 200 ZWJ emoji are 1000 code units but 200 clusters; the old code-unit
        // slice cut mid-cluster into replacement glyphs at the ellipsis.
        var emoji = "\U0001F9D1\u200D\U0001F4BB"; // person + ZWJ + laptop
        var text = string.Concat(Enumerable.Repeat(emoji, 200));
        var raw = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { content = new object[] { new { type = "text", text } } },
        });

        var line = StreamEventDisplay.Describe(ClaudeStreamEvent.Parse(raw))
            .Single(l => l.Kind == DisplayKind.AssistantText);

        Assert.EndsWith("\u2026", line.Text);
        // Every emoji before the ellipsis is intact — the length is an exact multiple
        // of the cluster's code-unit size.
        Assert.Equal(0, (line.Text.Length - 1) % emoji.Length);
    }
}
