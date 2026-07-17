using Orca.Core.Planning;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of a <see cref="PlanningPreview"/> into the styled body of the
/// plan-generate preview modal — the authorize-the-spend moment (build-order step 7). It
/// mirrors the CONTENT that <c>plan-generate</c> prints today across
/// <c>PlanGenerateCommand.RenderPreview</c> (the run facts) and <c>RenderDryRun</c> (the exact
/// argv, the <c>--settings</c> payload if any, and the assembled prompt with its sha), so the
/// human authorizes against the same facts in either lane. The Planner is a gate-less run, so
/// this is the only human decision before quota is spent; the modal's generate button
/// authorizes, cancel spends nothing. No Terminal.Gui types, so it is unit-testable without
/// the shell — the same fixture pattern as <see cref="RunPreviewText"/>.</summary>
internal static class PlanPreviewText
{
    public static IReadOnlyList<StyledLine> Describe(PlanningPreview preview)
    {
        var lines = new List<StyledLine>();
        var options = preview.Options;

        Fact(lines, "plan", preview.Request.PlanId);
        Fact(lines, "strategy", "single_candidate");
        // SUG-01: the Scout stage is part of what the human authorizes — show it.
        Fact(lines, "scout", preview.Scout is { } scout
            ? $"runs first (attempt {scout.Attempt}, model {scout.Options.Model ?? "CLI default"})"
            : preview.ScoutSkipReason ?? "(skipped)");
        Fact(lines, "attempt", preview.Attempt.ToString());
        Fact(lines, "plan file", preview.PlanFilePath);
        Fact(lines, "writes (rel)", preview.RelativePlanPath);
        Fact(lines, "model", options.Model ?? "CLI default");
        Fact(lines, "allowed tools", string.Join(", ", options.AllowedTools));
        Fact(lines, "denied tools", preview.SettingsJson is null ? "(none configured)" : "via --settings permissions.deny");
        Fact(lines, "max turns / timeout", $"{options.MaxTurns} / {options.Timeout}");
        Fact(lines, "max budget", options.MaxBudgetUsd is { } b ? $"${b}" : "(none)");
        Fact(lines, "parse retries", preview.MaxParseRetries.ToString());
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
