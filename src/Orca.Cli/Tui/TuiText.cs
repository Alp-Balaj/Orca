using System.Globalization;
using System.Text;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure text projections for the TUI's read-only panes — detail, activity, and the
/// bottom status line — straight from <see cref="StatusReport"/> data. No Terminal.Gui
/// types, so every line is unit-testable without the shell. This is a read-only window:
/// nothing here decides or mutates anything.</summary>
internal static class TuiText
{
    /// <summary>Right-top pane for the current selection: plan frontmatter facts for a plan
    /// node, or a checkpoint's description + acceptance criteria + latest evidence.</summary>
    public static string Detail(TuiTreeNode node) => node.Kind switch
    {
        TuiNodeKind.Checkpoint => CheckpointDetail(node.Checkpoint!, node.Evidence),
        _ => PlanDetail(node.Plan.Plan),
    };

    private static string PlanDetail(Plan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(plan.Title);
        sb.AppendLine();
        sb.AppendLine($"id            {plan.PlanId}");
        sb.AppendLine($"version       {plan.PlanVersion}");
        sb.AppendLine($"status        {Lower(plan.Status)}");
        sb.AppendLine($"base branch   {plan.BaseBranch}");
        sb.AppendLine($"target repo   {plan.TargetRepo}");
        return sb.ToString();
    }

    private static string CheckpointDetail(Checkpoint checkpoint, CheckpointEvidence? evidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine(checkpoint.Description);
        sb.AppendLine();
        sb.AppendLine($"status  {Lower(checkpoint.Status)}");
        sb.AppendLine();
        sb.AppendLine("acceptance criteria:");
        if (checkpoint.AcceptanceCriteria.Count == 0)
            sb.AppendLine("  (none)");
        else
            foreach (var criterion in checkpoint.AcceptanceCriteria)
                sb.AppendLine($"  - {criterion}");

        sb.AppendLine();
        sb.AppendLine("latest evidence:");
        if (evidence?.LatestVerification is { } verification)
        {
            sb.AppendLine(
                $"  verification    {Lower(verification.Verdict)} " +
                $"(attempt {verification.Attempt}, gates: build {Lower(verification.Gates.Build)}, tests {Lower(verification.Gates.Tests)})");
            if (verification.Semantic is { } semantic)
                foreach (var result in semantic)
                    sb.AppendLine($"    - {result.Criterion}: {Lower(result.Verdict)}{(result.Note is null ? "" : $" — {result.Note}")}");
        }
        else
        {
            sb.AppendLine("  verification    (no record yet)");
        }

        if (evidence?.LatestReconciliation is { } reconciliation)
        {
            sb.AppendLine($"  reconciliation  {Lower(reconciliation.ImplementsPlan)} (attempt {reconciliation.Attempt})");
            foreach (var missing in reconciliation.Missing)
                sb.AppendLine($"    missing: {missing}");
            foreach (var extra in reconciliation.Extra)
                sb.AppendLine($"    extra: {extra}");
        }
        else
        {
            sb.AppendLine("  reconciliation  (no record yet)");
        }

        return sb.ToString();
    }

    /// <summary>Right-bottom pane. Step 4 is idle only: the selected plan's last-run
    /// summary from run state (checkpoint, attempt, branch, accumulated cost), or a
    /// clearly-idle placeholder. The live role-tagged stream is a later build.</summary>
    public static string Activity(TuiTreeNode node)
    {
        var state = node.Plan.RunState;
        if (state is null)
            return "idle — no runs yet for this plan.\n\n"
                + "Start one from the CLI (orca checkpoint-run); the live run stream lands here in a later build.";

        var sb = new StringBuilder();
        sb.AppendLine("idle — last run:");
        sb.AppendLine();
        sb.AppendLine($"checkpoint   {state.CheckpointId} (attempt {state.Attempt})");
        sb.AppendLine($"branch       {state.Branch}");
        sb.AppendLine($"cost         {Money(state.AccumulatedCostUsd)}");
        if (state.LastStopReason is { } reason)
            sb.AppendLine($"stop reason  {Lower(reason)}");
        sb.AppendLine($"updated      {state.UpdatedAt:u}");
        return sb.ToString();
    }

    /// <summary>Bottom status line: repo root, current branch + dirty flag, vault, and the
    /// SELECTED plan's accumulated cost from its run state (2026-07-09 review: the selected
    /// plan's cost, not a vault-wide total). A null selection shows $0.00.</summary>
    public static string StatusLine(StatusReport report, TuiTreeNode? selected)
    {
        var cost = selected?.Plan.RunState?.AccumulatedCostUsd ?? 0m;
        var dirty = report.WorkingTreeClean ? "" : " (dirty)";
        return $"repo {report.RepoRoot} · branch {report.CurrentBranch}{dirty} · vault {report.VaultRoot} · spent {Money(cost)}";
    }

    private static string Money(decimal usd) => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Lower<T>(T value) where T : struct => value.ToString()!.ToLowerInvariant();
}
