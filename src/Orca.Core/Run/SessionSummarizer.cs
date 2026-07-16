using System.Text;
using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Vault;

namespace Orca.Core.Run;

/// <summary>How one summarizer run ended: the session note path on success, or the
/// human-actionable failure detail. Never throws for a failed distillation — this is
/// optional knowledge capture and must not sour an already-approved checkpoint.</summary>
public sealed record SessionNoteOutcome(string? NotePath, string? FailureDetail, decimal CostUsd);

/// <summary>SUG-09: the post-approve knowledge distiller. One cheap vault-scoped
/// invocation reads the attempt's filtered transcript, verification record, and commit
/// summary, and writes <c>Sessions/&lt;date&gt;-&lt;plan&gt;-&lt;checkpoint&gt;.md</c>
/// (validated fail-loud, §5.4 retries in-process). Deliberately stateless like the
/// Interviewer: a single small invocation carries nothing durable worth resume
/// plumbing. Runs only when a human says yes, only after Approve — the moment the
/// knowledge is proven worth keeping.</summary>
public sealed class SessionSummarizer(OrcaConfig config, IClaudeExecutor executor, string targetRepoRoot)
{
    private readonly VaultLayout _layout = new(config.VaultRoot, config.PlansDir);
    private readonly RunLogLayout _logLayout = new(targetRepoRoot);

    public bool IsConfigured => config.RoleSections.Summarizer is not null;

    public async Task<SessionNoteOutcome> WriteSessionNoteAsync(
        CheckpointRunPreview preview,
        int decidedAttempt,
        string? commitSummary,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var role = config.GetRole(RoleName.Summarizer);
        var maxParseRetries = config.MaxParseRetries ?? 0;

        var notePath = _layout.SessionFile(now, preview.Plan.PlanId, preview.Checkpoint.Id);
        var relativeNotePath = Path.GetRelativePath(config.VaultRoot, notePath).Replace('\\', '/');
        var relativeDecisions = Path.GetRelativePath(config.VaultRoot, _layout.DecisionsDirectory()).Replace('\\', '/');
        Directory.CreateDirectory(_layout.SessionsDirectory());

        var timestamp = $"{now.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z";
        var prompt = AssemblePrompt(preview, decidedAttempt, commitSummary,
            relativeNotePath, relativeDecisions, timestamp, previousError: null);

        var settingsJson = config.DeniedTools.Count > 0 ? PermissionSettings.ToJson(config.DeniedTools) : null;
        var cost = 0m;
        var parseFailures = 0;
        var invocation = 1;
        while (true)
        {
            var logPaths = SummaryLogPaths(preview, decidedAttempt, invocation);
            var options = new ClaudeInvocationOptions
            {
                Prompt = prompt,
                WorkingDirectory = config.VaultRoot,
                AllowedTools = role.AllowedTools,
                MaxTurns = role.MaxTurns,
                Timeout = role.Timeout,
                Model = role.Model,
                MaxBudgetUsd = role.MaxBudgetUsd,
                SettingsFilePath = settingsJson is null ? null : logPaths.EventLog + ".settings.json",
            };
            if (options.SettingsFilePath is { } settingsPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, settingsJson!);
            }

            var execution = await executor.ExecuteAsync(options, logPaths, null, cancellationToken);
            cost += execution.Result?.TotalCostUsd ?? 0m;

            if (execution.StopReason != InvocationStopReason.Succeeded)
            {
                return new SessionNoteOutcome(null,
                    $"the summarizer invocation did not succeed (stop reason: {InvocationStopReasons.ToWire(execution.StopReason)}) — the checkpoint itself is unaffected",
                    cost);
            }

            string? parseFailure = null;
            if (!File.Exists(notePath))
            {
                parseFailure = $"the Summarizer wrote no file at {relativeNotePath}";
            }
            else
            {
                try
                {
                    SessionNoteParser.Parse(File.ReadAllText(notePath), notePath,
                        preview.Plan.PlanId, preview.Checkpoint.Id, decidedAttempt);
                }
                catch (VaultException e)
                {
                    parseFailure = e.Message;
                }
            }

            if (parseFailure is null)
                return new SessionNoteOutcome(notePath, null, cost);

            parseFailures++;
            if (parseFailures > maxParseRetries)
            {
                return new SessionNoteOutcome(null,
                    $"the Summarizer's output failed validation {parseFailures} time(s), exhausting maxParseRetries={maxParseRetries}; last error: {parseFailure}",
                    cost);
            }

            invocation++;
            prompt = AssemblePrompt(preview, decidedAttempt, commitSummary,
                relativeNotePath, relativeDecisions, timestamp, parseFailure);
        }
    }

    private RunLogPaths SummaryLogPaths(CheckpointRunPreview preview, int attempt, int invocation) => new()
    {
        EventLog = Path.Combine(_logLayout.RunsRoot, preview.Plan.PlanId,
            $"{preview.Checkpoint.Id}.attempt-{attempt}.summarizer-{invocation}.ndjson"),
        Stderr = Path.Combine(_logLayout.RunsRoot, preview.Plan.PlanId,
            $"{preview.Checkpoint.Id}.attempt-{attempt}.summarizer-{invocation}.stderr.txt"),
    };

    private string AssemblePrompt(
        CheckpointRunPreview preview, int attempt, string? commitSummary,
        string relativeNotePath, string relativeDecisions, string timestamp, string? previousError)
    {
        var excerpt = TranscriptExcerpt.Extract(
            _logLayout.AttemptPaths(preview.Plan.PlanId, preview.Checkpoint.Id, attempt).EventLog);

        string verification;
        try
        {
            var records = new VaultReader(_layout)
                .ReadVerificationRecords(preview.Plan.PlanId, preview.Checkpoint.Id);
            var record = records.LastOrDefault(r => r.Attempt == attempt) ?? records.LastOrDefault();
            verification = record is null
                ? "(no verification record found)"
                : $"verdict: {SchemaEnums.ToYaml(record.Verdict)}\n{record.Body.ReplaceLineEndings("\n").TrimEnd()}";
        }
        catch (VaultException e)
        {
            verification = $"(verification record unreadable: {e.Message})";
        }

        var sb = new StringBuilder();
        sb.Append(SummarizerPromptText.RoleText.ReplaceLineEndings("\n").TrimEnd());
        sb.Append("\n\n## Request\n\n");
        sb.Append($"- plan_id: {preview.Plan.PlanId}\n");
        sb.Append($"- checkpoint_id: {preview.Checkpoint.Id} — {preview.Checkpoint.Description}\n");
        sb.Append($"- attempt: {attempt}\n");
        sb.Append($"- created timestamp to use verbatim: {timestamp}\n");
        sb.Append($"- write the session note to this path, relative to your working directory: {relativeNotePath}\n");
        sb.Append($"- decision-note drafts (at most one, only if truly warranted) go under: {relativeDecisions}/\n");
        if (commitSummary is not null)
            sb.Append($"- approved commit: {commitSummary}\n");

        sb.Append("\n## Transcript excerpt (filtered: assistant prose and tool errors only)\n\n");
        AppendFenced(sb, excerpt);
        sb.Append("\n## Verification record\n\n");
        AppendFenced(sb, verification);

        if (previousError is not null)
        {
            sb.Append("\n## Retry — your previous output failed validation\n\n");
            sb.Append("Orca parsed the file you wrote and rejected it. The exact error:\n\n");
            sb.Append("```\n").Append(previousError.ReplaceLineEndings("\n").TrimEnd()).Append("\n```\n\n");
            sb.Append($"The rejected file is still at {relativeNotePath} (unless you never wrote it). Fix it in place.\n");
        }

        return sb.ToString();
    }

    private static void AppendFenced(StringBuilder sb, string content)
    {
        var text = content.ReplaceLineEndings("\n").TrimEnd();
        var fence = new string('`', Math.Max(4, LongestBacktickRun(text) + 1));
        sb.Append(fence).Append('\n').Append(text).Append('\n').Append(fence).Append('\n');
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, current = 0;
        foreach (var ch in text)
        {
            current = ch == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}

/// <summary>The embedded role prompt, hash-attributed like every other role.</summary>
public static class SummarizerPromptText
{
    public static string RoleText { get; } = RolePromptResource.Read("Orca.Core.Prompts.summarizer.md");

    public static string RoleSha256 { get; } = CheckpointPrompt.Sha256(RoleText);
}
