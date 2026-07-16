using Orca.Core.Execution;

namespace Orca.Tests;

public class ClaudeVersionCheckTests
{
    [Fact]
    public void Exact_match_is_a_match()
    {
        Assert.Equal(ClaudeVersionVerdict.Match,
            ClaudeVersionCheck.Evaluate("2.1.201", "2.1.201", ClaudeVersionPolicy.Warn));
    }

    [Fact]
    public void An_older_observed_version_warns_under_the_default_policy()
    {
        Assert.Equal(ClaudeVersionVerdict.Warn,
            ClaudeVersionCheck.Evaluate("2.1.190", "2.1.201", ClaudeVersionPolicy.Warn));
    }

    [Fact]
    public void A_newer_observed_version_warns_under_the_default_policy()
    {
        // Today's real case: the installed CLI has drifted to 2.1.211 against the
        // README's 2.1.201 pin.
        Assert.Equal(ClaudeVersionVerdict.Warn,
            ClaudeVersionCheck.Evaluate("2.1.211", "2.1.201", ClaudeVersionPolicy.Warn));
    }

    [Fact]
    public void A_mismatch_escalates_to_refuse_under_the_opt_in_refuse_policy()
    {
        Assert.Equal(ClaudeVersionVerdict.Refuse,
            ClaudeVersionCheck.Evaluate("2.1.211", "2.1.201", ClaudeVersionPolicy.Refuse));
    }

    [Fact]
    public void A_null_observed_version_is_never_a_mismatch_under_either_policy()
    {
        // No init event, or one that omitted the field — Orca has no evidence of a
        // problem, so it must not warn or refuse on data it doesn't have.
        Assert.Equal(ClaudeVersionVerdict.Match,
            ClaudeVersionCheck.Evaluate(null, "2.1.201", ClaudeVersionPolicy.Warn));
        Assert.Equal(ClaudeVersionVerdict.Match,
            ClaudeVersionCheck.Evaluate(null, "2.1.201", ClaudeVersionPolicy.Refuse));
    }
}
