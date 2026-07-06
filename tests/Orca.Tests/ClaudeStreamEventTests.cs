using Orca.Core.Execution;

namespace Orca.Tests;

/// <summary>Driven by the sanitized real transcripts in Fixtures/Transcripts — the
/// contract is whatever CLI 2.1.201 actually emitted, not what any document guesses.</summary>
public class ClaudeStreamEventTests
{
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", name));

    [Fact]
    public void Success_fixture_parses_line_by_line()
    {
        var events = Fixture("success.ndjson").Select(ClaudeStreamEvent.Parse).ToList();

        Assert.Equal(
            ["system", "rate_limit_event", "assistant", "assistant", "user", "assistant", "result"],
            events.Select(e => e.Type));
        Assert.Single(events, e => e.IsInit);
        Assert.Single(events, e => e.Result is not null);
    }

    [Fact]
    public void Init_event_carries_the_session_id()
    {
        var init = Fixture("success.ndjson").Select(ClaudeStreamEvent.Parse).Single(e => e.IsInit);

        Assert.Equal("0f058114-37ac-4e08-a46e-b2e1dfd7b83b", init.SessionId);
    }

    [Fact]
    public void Unknown_event_types_pass_through_unparsed()
    {
        // rate_limit_event is undocumented and appears in successful runs; a closed
        // union would be a lie about the CLI.
        var events = Fixture("success.ndjson").Select(ClaudeStreamEvent.Parse);
        var rateLimit = Assert.Single(events, e => e.Type == "rate_limit_event");

        Assert.Null(rateLimit.Result);
        Assert.Contains("resetsAt", rateLimit.RawLine);
    }

    [Fact]
    public void Rate_limit_event_yields_the_rate_limit_info_evidence()
    {
        // A healthy run's rate_limit_event says "allowed" — presence is not failure;
        // the status is the classification signal (checkpoint 06 stop-reason contract).
        var rateLimit = Fixture("success.ndjson").Select(ClaudeStreamEvent.Parse)
            .Single(e => e.Type == "rate_limit_event").RateLimit;

        Assert.NotNull(rateLimit);
        Assert.Equal("allowed", rateLimit.Status);
        Assert.Equal(1783361400, rateLimit.ResetsAt);
        Assert.Equal("five_hour", rateLimit.RateLimitType);
    }

    [Fact]
    public void Rate_limit_event_without_readable_info_is_no_signal_not_a_crash()
    {
        // The event is undocumented diagnostics: shape drift yields null evidence (the
        // raw line is in the run log regardless), unlike the strict result contract.
        Assert.Null(ClaudeStreamEvent.Parse("{\"type\":\"rate_limit_event\"}").RateLimit);
        Assert.Null(ClaudeStreamEvent.Parse(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"resetsAt\":1}}").RateLimit);
        Assert.Null(ClaudeStreamEvent.Parse(
            "{\"type\":\"assistant\",\"rate_limit_info\":{\"status\":\"allowed\"}}").RateLimit);
    }

    [Fact]
    public void Authentication_failure_rides_a_machine_readable_error_field()
    {
        // Both auth fixtures carry error:"authentication_failed" on a synthetic
        // assistant event — the classification signal, since the result event hides
        // auth failures behind subtype "success".
        foreach (var fixture in new[] { "logged-out.ndjson", "invalid-api-key.ndjson" })
        {
            var events = Fixture(fixture).Select(ClaudeStreamEvent.Parse).ToList();
            Assert.Contains(events, e => e.Type == "assistant" && e.Error == "authentication_failed");
        }

        Assert.DoesNotContain(
            Fixture("success.ndjson").Select(ClaudeStreamEvent.Parse),
            e => e.Error is not null);
    }

    [Fact]
    public void Result_event_yields_cost_usage_and_turns()
    {
        var result = Fixture("success.ndjson").Select(ClaudeStreamEvent.Parse).Last().Result;

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(0.16075699999999998m, result.TotalCostUsd);
        Assert.Equal(2, result.NumTurns);
        Assert.Equal(2716, result.Usage.InputTokens);
        Assert.Equal(206, result.Usage.OutputTokens);
        Assert.Equal(5289, result.Usage.CacheCreationInputTokens);
        Assert.Equal(16913, result.Usage.CacheReadInputTokens);
        Assert.Null(result.ApiErrorStatus);
        Assert.Equal("0f058114-37ac-4e08-a46e-b2e1dfd7b83b", result.SessionId);
    }

    [Fact]
    public void Logged_out_result_is_error_despite_subtype_success()
    {
        // The trap this fixture exists to pin: the CLI says subtype "success" on an
        // authentication failure. is_error is the verdict field, never subtype.
        var result = Fixture("logged-out.ndjson").Select(ClaudeStreamEvent.Parse).Last().Result;

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(0m, result.TotalCostUsd);
        Assert.Null(result.ApiErrorStatus);
        Assert.Contains("Not logged in", result.ResultText);
    }

    [Fact]
    public void Invalid_api_key_result_carries_the_401()
    {
        var result = Fixture("invalid-api-key.ndjson").Select(ClaudeStreamEvent.Parse).Last().Result;

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Equal(401, result.ApiErrorStatus);
        Assert.Equal(0m, result.TotalCostUsd);
    }

    [Fact]
    public void Budget_kill_result_has_its_own_subtype_and_is_error()
    {
        // --max-budget-usd IS enforced under subscription auth (probed 2026-07-06):
        // the run dies mid-checkpoint with a distinct subtype, so a budget kill is never
        // conflated with a model refusal or a timeout.
        var result = Fixture("budget-exceeded.ndjson").Select(ClaudeStreamEvent.Parse).Last().Result;

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Equal("error_max_budget_usd", result.Subtype);
        Assert.True(result.TotalCostUsd > 0.05m);
        Assert.Null(result.ResultText);
    }

    [Fact]
    public void Permission_deny_rules_block_the_tool_call_not_the_run()
    {
        // Safety layer 2: permissions.deny via --settings survives --safe-mode and wins
        // over --allowedTools. The denied call surfaces as an is_error tool_result while
        // the run itself completes successfully.
        var events = Fixture("denied-by-permission-rules.ndjson").Select(ClaudeStreamEvent.Parse).ToList();

        Assert.Contains(events, e => e.Type == "user" && e.RawLine.Contains("has been denied"));
        var result = events.Last().Result;
        Assert.NotNull(result);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Safe_mode_suppresses_settings_hooks_the_negative_that_shaped_layer_2()
    {
        // The same command under a --settings PreToolUse deny hook executed anyway —
        // hooks are dead under --safe-mode on 2.1.201, which is why the deny list rides
        // permissions.deny instead (spec §7 amendment, 2026-07-06).
        var events = Fixture("safe-mode-hook-suppressed.ndjson").Select(ClaudeStreamEvent.Parse).ToList();

        Assert.Contains(events, e => e.Type == "user" && e.RawLine.Contains("hello-from-probe"));
        Assert.DoesNotContain(events, e => e.RawLine.Contains("orca-probe-deny-fired"));
        Assert.False(events.Last().Result!.IsError);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("{\"no_type_field\": true}")]
    [InlineData("{\"type\": \"\"}")]
    [InlineData("{\"type\": 42}")]
    public void Rejects_lines_without_a_usable_type(string line) =>
        Assert.Throws<StreamParseException>(() => ClaudeStreamEvent.Parse(line));

    [Fact]
    public void Rejects_an_init_event_without_a_session_id() =>
        Assert.Throws<StreamParseException>(() =>
            ClaudeStreamEvent.Parse("{\"type\": \"system\", \"subtype\": \"init\", \"tools\": []}"));

    private const string MinimalResult =
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1," +
        "\"duration_ms\":10,\"total_cost_usd\":0.01,\"session_id\":\"s-1\"," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"cache_creation_input_tokens\":3,\"cache_read_input_tokens\":4}}";

    [Fact]
    public void Accepts_a_minimal_result_event()
    {
        var result = ClaudeStreamEvent.Parse(MinimalResult).Result;

        Assert.NotNull(result);
        Assert.Equal(0.01m, result.TotalCostUsd);
        Assert.Equal(4, result.Usage.CacheReadInputTokens);
    }

    [Theory]
    [InlineData("\"total_cost_usd\":0.01,")]
    [InlineData("\"is_error\":false,")]
    [InlineData("\"num_turns\":1,")]
    [InlineData("\"input_tokens\":1,")]
    public void Rejects_a_result_event_missing_a_consumed_field(string fragment)
    {
        var mutilated = MinimalResult.Replace(fragment, "");
        Assert.NotEqual(MinimalResult, mutilated);

        Assert.Throws<StreamParseException>(() => ClaudeStreamEvent.Parse(mutilated));
    }

    [Fact]
    public void Rejects_a_result_event_with_a_wrongly_typed_field() =>
        Assert.Throws<StreamParseException>(() =>
            ClaudeStreamEvent.Parse(MinimalResult.Replace("\"is_error\":false", "\"is_error\":\"no\"")));
}
