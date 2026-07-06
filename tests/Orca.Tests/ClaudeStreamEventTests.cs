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
