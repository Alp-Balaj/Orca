using System.Globalization;

namespace Orca.Core.Execution;

/// <summary>Builds the exact argv for one invocation. Every element is passed to the
/// process verbatim via <c>ArgumentList</c> — no shell, no quoting, no normalization —
/// so the allowlist prefix-match subtleties survive untouched. Flag set and order mirror
/// the capture command the stream contract was derived from (see the transcript
/// fixtures): <c>--safe-mode</c> (not <c>--bare</c>, which cannot authenticate from a
/// subscription), <c>stream-json</c>, and the empirically still-mandatory
/// <c>--verbose</c>.
/// <para>The prompt itself never appears here: <c>-p</c> takes its value from stdin (see
/// <see cref="ClaudeCodeExecutor"/>), not from an argv element — argv is visible to every
/// other process on the machine via <c>/proc/&lt;pid&gt;/cmdline</c> or <c>ps</c>, stdin is
/// not.</para></summary>
public static class ClaudeArguments
{
    public static IReadOnlyList<string> Build(ClaudeInvocationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Prompt))
            throw new ExecutorException("prompt must be non-empty");
        if (options.AllowedTools.Any(string.IsNullOrWhiteSpace))
            throw new ExecutorException("allowed-tool patterns must be non-empty; got a blank entry");
        if (options.MaxTurns < 1)
            throw new ExecutorException($"max turns must be >= 1, got {options.MaxTurns}");
        if (options.Timeout <= TimeSpan.Zero)
            throw new ExecutorException($"timeout must be positive, got {options.Timeout}");
        if (options.Model is not null && string.IsNullOrWhiteSpace(options.Model))
            throw new ExecutorException("model, when set, must be non-empty");
        if (options.MaxBudgetUsd is <= 0)
            throw new ExecutorException($"max budget, when set, must be positive, got {options.MaxBudgetUsd}");
        if (options.SettingsFilePath is not null && string.IsNullOrWhiteSpace(options.SettingsFilePath))
            throw new ExecutorException("settings file path, when set, must be non-empty");

        var argv = new List<string>
        {
            "--safe-mode",
            "-p",
        };

        if (options.AllowedTools.Count > 0)
        {
            argv.Add("--allowedTools");
            argv.AddRange(options.AllowedTools);
        }

        argv.Add("--output-format");
        argv.Add("stream-json");
        argv.Add("--verbose");
        argv.Add("--max-turns");
        argv.Add(options.MaxTurns.ToString(CultureInfo.InvariantCulture));

        if (options.Model is not null)
        {
            argv.Add("--model");
            argv.Add(options.Model);
        }

        if (options.MaxBudgetUsd is { } budget)
        {
            argv.Add("--max-budget-usd");
            argv.Add(budget.ToString(CultureInfo.InvariantCulture));
        }

        // Deny rules survive --safe-mode via an explicit settings file and win over the
        // allowlist (probed 2026-07-06 on 2.1.201) — PreToolUse hooks do not.
        if (options.SettingsFilePath is not null)
        {
            argv.Add("--settings");
            argv.Add(options.SettingsFilePath);
        }

        return argv;
    }
}
