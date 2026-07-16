using System.ComponentModel;
using Orca.Core.Config;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Run;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orca.Cli.Commands;

/// <summary>Runs exactly one checkpoint — one Implementer invocation — then stops for the
/// human's approve/reject decision. Never loops over checkpoints (spec §6). Since the
/// §3.3 extraction this is a thin caller: CheckpointRunSession owns the sequence,
/// SpectreCheckpointGate owns the prompts, and this command renders the preview up front
/// and the outcome afterwards — byte-for-byte today's output.</summary>
public sealed class CheckpointRunCommand : AsyncCommand<CheckpointRunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--plan <PLAN-ID>")]
        [Description("Plan to run. Optional when the vault holds exactly one plan.")]
        public string? Plan { get; init; }

        [CommandOption("--checkpoint <CHECKPOINT-ID>")]
        [Description("Checkpoint to run. Defaults to the plan's first pending checkpoint.")]
        public string? Checkpoint { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print the branch, prompt, and exact claude argv without mutating anything.")]
        public bool DryRun { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Target repo (any directory inside it). Defaults to the current directory.")]
        public string? Repo { get; init; }

        [CommandOption("--override-complete-by-evidence")]
        [Description("Run a checkpoint anyway even though its branch head or verification record already shows it complete (the plan file just wasn't flipped).")]
        public bool OverrideCompleteByEvidence { get; init; }

        [CommandOption("--override-unmerged-base")]
        [Description("Run anyway even though the previous checkpoint's approved branch is not yet merged into base_branch.")]
        public bool OverrideUnmergedBase { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var workspace = GitWorkspace.Discover(settings.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(workspace.RepoRoot);
        var session = new CheckpointRunSession(config, workspace, new ClaudeCodeExecutor());
        var interactive = ConsoleInteractivity.Detect();

        // SUG-16 proposal A: after an Approve, offer to launch the next checkpoint in the
        // same session — the only thing this loop automates is *launching*, never
        // *deciding*; every checkpoint still stops at ExecuteAsync for its own gate.
        string? planId = settings.Plan;
        string? checkpointId = settings.Checkpoint;
        while (true)
        {
            var preview = session.Prepare(
                planId, checkpointId, settings.OverrideCompleteByEvidence, settings.OverrideUnmergedBase);
            CheckpointRenderer.RenderPreview(preview);

            if (settings.DryRun)
            {
                CheckpointRenderer.RenderDryRun(preview);
                return 0;
            }

            if (preview.PendingDecision)
                AnsiConsole.MarkupLine(
                    $"[yellow]attempt {preview.Attempt - 1} is still awaiting your decision[/] — reviewing it now instead of launching a new invocation.");

            var gate = new SpectreCheckpointGate(interactive);
            var result = await session.ExecuteAsync(preview, gate);
            var exitCode = Render(result, gate);

            if (result.Outcome != CheckpointSessionOutcome.Approved || !interactive)
                return exitCode;

            // SUG-09: the knowledge-distillation offer, at the moment the knowledge is
            // proven worth keeping. Optional, cheap, and a failure never sours the
            // approval.
            await OfferSessionNoteAsync(config, workspace, result);

            var next = session.ChainCandidate(result.Preview);
            if (next is null || !AnsiConsole.Confirm(
                $"next: '{next.Id.EscapeMarkup()}' — {next.Description.EscapeMarkup()}. Run it now?"))
                return exitCode;

            if (!session.MergeApprovedBranch(result.Preview))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]could not fast-forward[/] '{result.Preview.Plan.BaseBranch.EscapeMarkup()}' to " +
                    $"'{result.Preview.Branch.EscapeMarkup()}' — it moved since the checkpoint branched. " +
                    "Merge manually, then re-run checkpoint-run.");
                return exitCode;
            }

            AnsiConsole.MarkupLine(
                $"[green]merged[/] '{result.Preview.Branch.EscapeMarkup()}' into '{result.Preview.Plan.BaseBranch.EscapeMarkup()}'");
            planId = result.Preview.Plan.PlanId;
            checkpointId = next.Id;
        }
    }

    private static async Task OfferSessionNoteAsync(
        Orca.Core.Config.OrcaConfig config, GitWorkspace workspace, CheckpointSessionResult result)
    {
        var summarizer = new SessionSummarizer(config, new ClaudeCodeExecutor(), workspace.RepoRoot);
        if (!summarizer.IsConfigured)
            return;

        if (!AnsiConsole.Prompt(new ConfirmationPrompt(
                "Write a session note from this run's evidence (one haiku-tier invocation, cents)?")))
            return;

        var decidedAttempt = result.Verification?.Record?.Attempt ?? result.Preview.Attempt;
        var commitSummary = result.CommitSha is { } sha
            ? $"{sha[..12]} — {workspace.CommitSubject(sha) ?? "(subject unavailable)"}"
            : null;

        var outcome = await summarizer.WriteSessionNoteAsync(
            result.Preview, decidedAttempt, commitSummary, DateTimeOffset.UtcNow);

        if (outcome.NotePath is { } path)
            AnsiConsole.MarkupLine($"[green]session note written:[/] {path.EscapeMarkup()}" +
                (outcome.CostUsd > 0 ? $" (${outcome.CostUsd:0.00})" : ""));
        else
            AnsiConsole.MarkupLine($"[yellow]{outcome.FailureDetail!.EscapeMarkup()}[/]");
    }

    internal static int Render(CheckpointSessionResult result, SpectreCheckpointGate gate)
    {
        switch (result.Outcome)
        {
            case CheckpointSessionOutcome.GitignoreAdded:
                AnsiConsole.MarkupLine(
                    $"[yellow]added '{OrcaIgnore.Entry}' to .gitignore[/] — commit that change, then re-run (Orca never commits to your branch on its own).");
                return 1;

            case CheckpointSessionOutcome.InvocationFailed:
                // A failure that left partial changes was already rendered by the gate
                // (evidence before its discard prompt); a clean failure is rendered here.
                if (!gate.RenderedInvocationFailure)
                {
                    CheckpointRenderer.RenderExecution(result.Run!);
                    CheckpointRenderer.RenderInvocationFailedLine();
                }
                if (result.DiscardedChanges)
                    AnsiConsole.MarkupLine("[yellow]discarded[/]");
                return 1;

            case CheckpointSessionOutcome.NoChanges:
                CheckpointRenderer.RenderExecution(result.Run!);
                AnsiConsole.MarkupLine(
                    "[red]the invocation succeeded but produced no changes[/] — nothing to review; treat as a failed attempt.");
                return 1;

            case CheckpointSessionOutcome.VerificationIncomplete:
                if (result.Run is { } run)
                    CheckpointRenderer.RenderExecution(run);
                CheckpointRenderer.RenderVerification(result.Verification!);
                CheckpointRenderer.RenderVerificationFailure(result.Verification!);
                return 1;

            case CheckpointSessionOutcome.Approved:
                AnsiConsole.MarkupLine(
                    $"[green]committed[/] {result.CommitSha![..12].EscapeMarkup()} on {result.Preview.Branch.EscapeMarkup()}");
                AnsiConsole.MarkupLine(result.NextStep!.EscapeMarkup());
                return 0;

            case CheckpointSessionOutcome.Rejected:
                AnsiConsole.MarkupLine(result.DiscardedChanges
                    ? "[yellow]changes discarded[/] — the branch is back at its base state."
                    : "[yellow]changes left in place[/] — re-run checkpoint-run to decide later.");
                AnsiConsole.MarkupLine(result.NextStep!.EscapeMarkup());
                return 0;

            case CheckpointSessionOutcome.Deferred:
                // Auto-defer on a non-interactive terminal exits 2 ("ran, needs a human" —
                // §3.6); a human's deliberate defer stays 0. Everything is already in the
                // resumable state; an interactive re-run picks the decision back up free.
                if (result.AutoDeferred)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]auto-deferred[/] — this terminal is non-interactive, so the checkpoint ran and its decision is waiting. Re-run checkpoint-run from a real terminal to approve/reject/defer (nothing is re-paid).");
                    AnsiConsole.MarkupLine(result.NextStep!.EscapeMarkup());
                    return 2;
                }

                AnsiConsole.MarkupLine("[yellow]deferred[/] — re-run checkpoint-run to pick this decision back up.");
                AnsiConsole.MarkupLine(result.NextStep!.EscapeMarkup());
                return 0;

            default:
                // Interrupted — unreachable from this front end (it passes no
                // cancellation source); the TUI's Interrupt affordance renders it later.
                AnsiConsole.MarkupLine(
                    "[yellow]interrupted[/] — re-run checkpoint-run to resume; completed sub-steps are not re-paid.");
                return 1;
        }
    }
}
