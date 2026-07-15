using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of a <see cref="CheckpointRunPreview"/> into the styled body of
/// the preview modal — the authorize-the-spend moment (build-order step 5, item 1). It
/// mirrors the CONTENT that <c>checkpoint-run --dry-run</c> prints today
/// (<c>CheckpointRenderer.RenderPreview</c> + <c>RenderDryRun</c>): the run facts, then the
/// exact argv, the <c>--settings</c> payload if any, and the assembled prompt with its
/// sha. Nothing here executes or mutates; the modal's Run button is what authorizes the
/// spend. No Terminal.Gui types, so the projection is unit-testable without the shell.</summary>
internal static class RunPreviewText
{
    public static IReadOnlyList<StyledLine> Describe(CheckpointRunPreview preview)
    {
        var lines = new List<StyledLine>();

        if (preview.PendingDecision)
        {
            lines.Add(StyledLine.Of(
                new("● ", TuiRole.Warning),
                new($"attempt {preview.Attempt - 1} is still awaiting your decision", TuiRole.Warning)));
            lines.Add(StyledLine.Plain(
                "reviewing it now instead of launching — and paying for — a new invocation.", TuiRole.Dim));
            lines.Add(StyledLine.Empty);
        }

        Fact(lines, "plan", $"{preview.Plan.PlanId} v{preview.Plan.PlanVersion}");
        Fact(lines, "checkpoint", $"{preview.Checkpoint.Id} — {preview.Checkpoint.Description}");
        Fact(lines, "attempt", preview.Attempt.ToString());
        Fact(lines, "branch", preview.Branch + (preview.BranchExists
            ? " (exists)"
            : $" (new, from {preview.Plan.BaseBranch})"));
        Fact(lines, "model", preview.Options.Model ?? "CLI default");
        Fact(lines, "allowed tools", string.Join(", ", preview.Options.AllowedTools));
        Fact(lines, "denied tools", preview.SettingsJson is null ? "(none configured)" : "via --settings permissions.deny");
        Fact(lines, "max turns / timeout", $"{preview.Options.MaxTurns} / {preview.Options.Timeout}");
        Fact(lines, "max budget", preview.Options.MaxBudgetUsd is { } b ? $"${b}" : "(none)");
        Fact(lines, "run log", preview.LogPaths.EventLog);

        Section(lines, "argv (one element per line, passed verbatim)");
        foreach (var arg in preview.Argv)
            lines.Add(StyledLine.Plain("  " + arg, TuiRole.Dim));

        if (preview.SettingsJson is { } settings)
        {
            Section(lines, "--settings payload");
            foreach (var settingsLine in settings.Split('\n'))
                lines.Add(StyledLine.Plain("  " + settingsLine.TrimEnd('\r'), TuiRole.Dim));
        }

        Section(lines, $"assembled prompt (sha256 {Sha12(preview.PromptSha256)})");
        foreach (var promptLine in preview.Prompt.Split('\n'))
            lines.Add(StyledLine.Plain("  " + promptLine.TrimEnd('\r'), TuiRole.Dim));

        return lines;
    }

    private static void Fact(List<StyledLine> lines, string key, string value) =>
        lines.Add(StyledLine.Of(new($"{key,-20}", TuiRole.Faint), new(value, TuiRole.Text)));

    private static void Section(List<StyledLine> lines, string title)
    {
        lines.Add(StyledLine.Empty);
        lines.Add(StyledLine.Plain(title, TuiRole.Faint));
    }

    private static string Sha12(string sha) => sha.Length >= 12 ? sha[..12] : sha;
}
