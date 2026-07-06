using Orca.Core.Execution;

namespace Orca.Tests;

public class ClaudeArgumentsTests
{
    private static ClaudeInvocationOptions Options() => new()
    {
        Prompt = "Read the file README.md and summarize it in one sentence",
        WorkingDirectory = "/repo",
        AllowedTools = ["Read"],
        MaxTurns = 12,
        Timeout = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public void Builds_the_pinned_argv_shape()
    {
        var argv = ClaudeArguments.Build(Options());

        Assert.Equal(
        [
            "--safe-mode",
            "-p",
            "Read the file README.md and summarize it in one sentence",
            "--allowedTools",
            "Read",
            "--output-format",
            "stream-json",
            "--verbose",
            "--max-turns",
            "12",
        ], argv);
    }

    [Fact]
    public void Allowlist_patterns_pass_verbatim_one_element_each()
    {
        // The spec's sharp edge: the space before '*' changes what the CLI matches.
        // `Bash(git diff *)` matches commands starting with `git diff`;
        // `Bash(git diff*)` also matches `git diff-index`. Never normalize.
        var argv = ClaudeArguments.Build(Options() with
        {
            AllowedTools = ["Bash(git diff *)", "Bash(git diff*)", "Read"],
        });

        var start = argv.IndexOf("--allowedTools");
        Assert.Equal("Bash(git diff *)", argv[start + 1]);
        Assert.Equal("Bash(git diff*)", argv[start + 2]);
        Assert.Equal("Read", argv[start + 3]);
        Assert.Equal("--output-format", argv[start + 4]);
        Assert.Single(argv, a => a == "--allowedTools");
    }

    [Fact]
    public void Empty_allowlist_omits_the_flag()
    {
        var argv = ClaudeArguments.Build(Options() with { AllowedTools = [] });

        Assert.DoesNotContain("--allowedTools", argv);
    }

    [Fact]
    public void Optional_model_and_budget_render_invariantly()
    {
        var argv = ClaudeArguments.Build(Options() with
        {
            Model = "claude-opus-4-8",
            MaxBudgetUsd = 0.75m,
        });

        Assert.Equal("claude-opus-4-8", argv[argv.IndexOf("--model") + 1]);
        Assert.Equal("0.75", argv[argv.IndexOf("--max-budget-usd") + 1]);
    }

    [Fact]
    public void Optional_flags_absent_by_default()
    {
        var argv = ClaudeArguments.Build(Options());

        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("--max-budget-usd", argv);
        Assert.DoesNotContain("--settings", argv);
    }

    [Fact]
    public void Settings_file_rides_as_the_last_flag()
    {
        // Safety layer 2: the permissions.deny payload, probed to survive --safe-mode.
        var argv = ClaudeArguments.Build(Options() with
        {
            SettingsFilePath = "/repo/.orca/runs/p/01-x.attempt-1.settings.json",
        });

        Assert.Equal("/repo/.orca/runs/p/01-x.attempt-1.settings.json", argv[argv.IndexOf("--settings") + 1]);
        Assert.Equal(argv.Count - 1, argv.IndexOf("--settings") + 1);
    }

    [Fact]
    public void Rejects_a_blank_settings_path() =>
        Assert.Throws<ExecutorException>(() =>
            ClaudeArguments.Build(Options() with { SettingsFilePath = " " }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_blank_prompt(string prompt) =>
        Assert.Throws<ExecutorException>(() => ClaudeArguments.Build(Options() with { Prompt = prompt }));

    [Fact]
    public void Rejects_a_blank_allowlist_pattern() =>
        Assert.Throws<ExecutorException>(() =>
            ClaudeArguments.Build(Options() with { AllowedTools = ["Read", " "] }));

    [Fact]
    public void Rejects_non_positive_max_turns() =>
        Assert.Throws<ExecutorException>(() => ClaudeArguments.Build(Options() with { MaxTurns = 0 }));

    [Fact]
    public void Rejects_a_non_positive_timeout() =>
        Assert.Throws<ExecutorException>(() =>
            ClaudeArguments.Build(Options() with { Timeout = TimeSpan.Zero }));

    [Fact]
    public void Rejects_a_non_positive_budget() =>
        Assert.Throws<ExecutorException>(() =>
            ClaudeArguments.Build(Options() with { MaxBudgetUsd = 0m }));
}

file static class ArgvExtensions
{
    public static int IndexOf(this IReadOnlyList<string> argv, string element)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == element)
                return i;
        }

        return -1;
    }
}
