using System.Globalization;
using System.Text;
using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure styled-text projections for the TUI's read-only panes — detail, activity,
/// and the bottom status line — straight from <see cref="StatusReport"/> data. No
/// Terminal.Gui types, so every line is unit-testable without the shell. The voice is
/// Claude Code's: primary facts in the default colour, labels and metadata dim, verdicts
/// as coloured words, <c>⎿</c> connectors for evidence items. This is a read-only window:
/// nothing here decides or mutates anything.</summary>
internal static class TuiText
{
    /// <summary>Right-top pane for the current selection: plan facts for a plan node, or a
    /// checkpoint's description + acceptance criteria + latest evidence.</summary>
    public static IReadOnlyList<StyledLine> Detail(TuiTreeNode node) => node switch
    {
        { Plan.LoadError: { } reason } => UnparseablePlanDetail(node.Plan.Plan.PlanId, reason),
        { Kind: TuiNodeKind.Checkpoint } => CheckpointDetail(node),
        _ => PlanDetail(node.Plan.Plan),
    };

    /// <summary>Detail pane for a plan file the parser refused (BF-13): the exact reason
    /// and the honest instruction — fix the file; nothing can run against it.</summary>
    private static IReadOnlyList<StyledLine> UnparseablePlanDetail(string planId, string reason) =>
    [
        StyledLine.Plain(planId, TuiRole.Heading),
        StyledLine.Of(new("✗ ", TuiRole.Error), new("plan file does not parse", TuiRole.Error)),
        StyledLine.Empty,
        StyledLine.Plain(reason, TuiRole.Text),
        StyledLine.Empty,
        StyledLine.Plain("Fix the file (Obsidian sync artifact? hand edit?), then press r — nothing can run against it as-is.", TuiRole.Dim),
    ];

    /// <summary>Detail pane when the vault has no plans — the first-run screen. Friendly,
    /// and points at the one command that changes the situation.</summary>
    public static IReadOnlyList<StyledLine> EmptyVault() =>
    [
        StyledLine.Plain("No plans in this vault yet.", TuiRole.Dim),
        StyledLine.Empty,
        StyledLine.Of(new("Scaffold one with ", TuiRole.Faint), new("orca plan-init <plan-id>", TuiRole.Dim)),
        StyledLine.Of(new("then draft it with ", TuiRole.Faint), new("orca plan-generate", TuiRole.Dim)),
    ];

    private static IReadOnlyList<StyledLine> PlanDetail(Plan plan)
    {
        var lines = new List<StyledLine>
        {
            StyledLine.Plain(plan.Title, TuiRole.Heading),
            StyledLine.Of(
                new(StatusWord(plan.Status), RoleFor(TuiTreeModel.MapPlanStatus(plan.Status))),
                new($" · v{plan.PlanVersion} · base {plan.BaseBranch} · repo {plan.TargetRepo}", TuiRole.Dim)),
            StyledLine.Empty,
        };

        if (plan.Checkpoints.Count > 0)
        {
            lines.Add(StyledLine.Plain("checkpoints", TuiRole.Faint));
            lines.Add(CheckpointTally(plan.Checkpoints));
        }
        else
        {
            lines.Add(StyledLine.Plain("no checkpoints in this plan", TuiRole.Faint));
        }

        lines.Add(StyledLine.Empty);
        lines.Add(StyledLine.Plain(
            $"created {plan.Created:yyyy-MM-dd} · updated {plan.Updated:yyyy-MM-dd}", TuiRole.Faint));
        return lines;
    }

    /// <summary>One line of coloured glyph+count pairs — doubles as the legend for the
    /// glyphs the tree uses (e.g. "✓ 2 done · ⏺ 1 active · · 1 pending").</summary>
    private static StyledLine CheckpointTally(IReadOnlyList<Checkpoint> checkpoints)
    {
        var done = checkpoints.Count(c => c.Status == CheckpointStatus.Complete);
        var failed = checkpoints.Count(c => c.Status is CheckpointStatus.Rejected or CheckpointStatus.Blocked);
        var active = checkpoints.Count(c => c.Status is CheckpointStatus.InProgress or CheckpointStatus.AwaitingReview);
        var pending = checkpoints.Count - done - failed - active;

        var runs = new List<StyledRun> { new("  ", TuiRole.Text) };
        void Append(string glyph, int count, string word, TuiRole role)
        {
            if (count == 0)
                return;
            if (runs.Count > 1)
                runs.Add(new StyledRun("   ", TuiRole.Text));
            runs.Add(new StyledRun($"{glyph} ", role));
            runs.Add(new StyledRun($"{count} {word}", TuiRole.Text));
        }

        Append("✓", done, "done", TuiRole.Success);
        Append("⏺", active, "active", TuiRole.Warning);
        Append("✗", failed, "blocked", TuiRole.Error);
        Append("·", pending, "pending", TuiRole.Dim);
        return new StyledLine(runs);
    }

    private static IReadOnlyList<StyledLine> CheckpointDetail(TuiTreeNode node)
    {
        var checkpoint = node.Checkpoint!;
        var evidence = node.Evidence;
        var lines = new List<StyledLine>
        {
            StyledLine.Plain(checkpoint.Id, TuiRole.Heading),
            StyledLine.Plain(checkpoint.Description),
            StatusLineFor(node, checkpoint),
            StyledLine.Empty,
            StyledLine.Plain("acceptance criteria", TuiRole.Faint),
        };

        if (checkpoint.AcceptanceCriteria.Count == 0)
            lines.Add(StyledLine.Plain("  (none)", TuiRole.Faint));
        else
            foreach (var criterion in checkpoint.AcceptanceCriteria)
                lines.Add(StyledLine.Of(new("  · ", TuiRole.Dim), new(criterion, TuiRole.Text)));

        lines.Add(StyledLine.Empty);

        if (evidence?.LatestVerification is null && evidence?.LatestReconciliation is null)
        {
            lines.Add(StyledLine.Plain("no evidence yet — records land here after a run", TuiRole.Faint));
            return lines;
        }

        AppendVerification(lines, evidence.LatestVerification);
        lines.Add(StyledLine.Empty);
        AppendReconciliation(lines, evidence.LatestReconciliation);
        return lines;
    }

    /// <summary>The checkpoint's own status word, plus the attempt count when the plan's
    /// run state is parked on this very checkpoint.</summary>
    private static StyledLine StatusLineFor(TuiTreeNode node, Checkpoint checkpoint)
    {
        var runs = new List<StyledRun>
        {
            new(StatusWord(checkpoint.Status), RoleFor(TuiTreeModel.MapCheckpointStatus(checkpoint.Status))),
        };
        if (node.IsCurrent && node.Plan.RunState is { } state)
            runs.Add(new StyledRun($" · attempt {state.Attempt}", TuiRole.Dim));

        // SUG-03 stage A: a pending checkpoint is either ready (all effective
        // dependencies complete) or shows exactly what it waits on.
        if (checkpoint.Status == CheckpointStatus.Pending)
        {
            var blockers = CheckpointGraph.IncompleteDependencies(node.Plan.Plan, checkpoint);
            runs.Add(blockers.Count == 0
                ? new StyledRun(" · ready", TuiRole.Success)
                : new StyledRun($" · waits on {string.Join(", ", blockers)}", TuiRole.Warning));
        }

        return new StyledLine(runs);
    }

    private static void AppendVerification(List<StyledLine> lines, VerificationRecord? verification)
    {
        if (verification is null)
        {
            lines.Add(StyledLine.Of(
                new("verification ", TuiRole.Faint), new("none yet", TuiRole.Dim)));
            return;
        }

        lines.Add(StyledLine.Of(
            new("verification ", TuiRole.Faint),
            new(VerdictWord(verification.Verdict), verification.Verdict == VerificationVerdict.Pass ? TuiRole.Success : TuiRole.Error),
            new($" · attempt {verification.Attempt}", TuiRole.Dim)));

        lines.Add(StyledLine.Of(
            new("  ⎿ ", TuiRole.Faint),
            new("gates  ", TuiRole.Dim),
            new($"build {GateWord(verification.Gates.Build)}", RoleFor(verification.Gates.Build)),
            new(" · ", TuiRole.Dim),
            new($"tests {GateWord(verification.Gates.Tests)}", RoleFor(verification.Gates.Tests))));

        foreach (var result in verification.Semantic ?? [])
        {
            var pass = result.Verdict == SemanticVerdict.Pass;
            lines.Add(StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint),
                new(pass ? "✓ " : "✗ ", pass ? TuiRole.Success : TuiRole.Error),
                new(result.Criterion, TuiRole.Text)));
            if (result.Note is { } note)
                lines.Add(StyledLine.Plain($"      {note}", TuiRole.Dim));
        }
    }

    private static void AppendReconciliation(List<StyledLine> lines, ReconciliationRecord? reconciliation)
    {
        if (reconciliation is null)
        {
            lines.Add(StyledLine.Of(
                new("reconciliation ", TuiRole.Faint), new("none yet", TuiRole.Dim)));
            return;
        }

        lines.Add(StyledLine.Of(
            new("reconciliation ", TuiRole.Faint),
            new(VerdictWord(reconciliation.ImplementsPlan), reconciliation.ImplementsPlan switch
            {
                ReconciliationVerdict.Full => TuiRole.Success,
                ReconciliationVerdict.Partial => TuiRole.Warning,
                _ => TuiRole.Error,
            }),
            new($" · attempt {reconciliation.Attempt}", TuiRole.Dim)));

        foreach (var missing in reconciliation.Missing)
            lines.Add(StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint), new("missing  ", TuiRole.Error), new(missing, TuiRole.Text)));
        foreach (var extra in reconciliation.Extra)
            lines.Add(StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint), new("extra    ", TuiRole.Warning), new(extra, TuiRole.Text)));
    }

    /// <summary>Right-bottom pane. Step 4 is idle only: the selected plan's last-run
    /// summary from run state, or a clearly-idle placeholder pointing at the command that
    /// starts a run. The live role-tagged stream is a later build.</summary>
    public static IReadOnlyList<StyledLine> Activity(TuiTreeNode node)
    {
        var state = node.Plan.RunState;
        if (state is null)
            return
            [
                StyledLine.Plain("idle — no runs yet for this plan", TuiRole.Dim),
                StyledLine.Empty,
                StyledLine.Of(
                    new("run a checkpoint with ", TuiRole.Faint),
                    new("orca checkpoint-run", TuiRole.Dim)),
            ];

        return
        [
            StyledLine.Of(
                new("⏺ ", TuiRole.Accent),
                new("last run — ", TuiRole.Dim),
                new(state.CheckpointId, TuiRole.Text),
                new($" · attempt {state.Attempt}", TuiRole.Dim)),
            StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint),
                new("stop reason  ", TuiRole.Dim),
                state.LastStopReason is { } reason
                    ? new(StatusWord(reason), RoleFor(reason))
                    : new("interrupted mid-run", TuiRole.Warning)),
            StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint),
                new("branch       ", TuiRole.Dim),
                new(state.Branch, TuiRole.Text)),
            StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint),
                new("spent        ", TuiRole.Dim),
                new(Money(state.AccumulatedCostUsd), TuiRole.Text)),
            StyledLine.Of(
                new("  ⎿ ", TuiRole.Faint),
                new("updated      ", TuiRole.Dim),
                new($"{state.UpdatedAt:yyyy-MM-dd HH:mm} UTC", TuiRole.Dim)),
        ];
    }

    /// <summary>Bottom status line: repo (home-shortened), current branch + dirty marker,
    /// vault name, and the SELECTED plan's accumulated cost from its run state (2026-07-09
    /// review: the selected plan's cost, not a vault-wide total).</summary>
    public static StyledLine StatusLine(StatusReport report, TuiTreeNode? selected)
    {
        var runs = new List<StyledRun>
        {
            new(CompactPath(report.RepoRoot), TuiRole.Dim),
            new("  ", TuiRole.Text),
            new(report.CurrentBranch, TuiRole.Text),
        };
        if (!report.WorkingTreeClean)
            runs.Add(new StyledRun("*", TuiRole.Warning));

        runs.Add(new StyledRun("  vault ", TuiRole.Faint));
        runs.Add(new StyledRun(VaultDisplayName(report.VaultRoot), TuiRole.Dim));

        var cost = selected?.Plan.RunState?.AccumulatedCostUsd ?? 0m;
        runs.Add(new StyledRun("  spent ", TuiRole.Faint));
        runs.Add(new StyledRun(Money(cost), cost > 0m ? TuiRole.Text : TuiRole.Dim));

        return new StyledLine(runs);
    }

    /// <summary>The key hints, Claude Code style — one faint line, always visible.</summary>
    public static StyledLine Hints() => StyledLine.Plain(
        "↑↓ navigate · ←→ collapse/expand · x run / approve checkpoint · a approve plan · m mark complete · g generate plan · r refresh · q quit", TuiRole.Faint);

    public static string ShortenHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal))
            return "~" + path[home.Length..];
        return path;
    }

    /// <summary>Home-shorten, then keep a long path to its last two segments
    /// (<c>…/parent/name</c>) so the status line's branch/vault/spend never get pushed
    /// off-screen by a deep repo path.</summary>
    public static string CompactPath(string path)
    {
        var shortened = ShortenHome(path);
        if (shortened.Length <= 40)
            return shortened;

        var trimmed = Path.TrimEndingDirectorySeparator(shortened);
        var name = Path.GetFileName(trimmed);
        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
        return parent.Length > 0 ? $"…/{parent}/{name}" : $"…/{name}";
    }

    private static string VaultDisplayName(string vaultRoot)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(vaultRoot));
        return string.IsNullOrEmpty(name) ? ShortenHome(vaultRoot) : name;
    }

    // BF-17: sub-cent runs used to render as $0.00 — real spend hidden as zero.
    private static string Money(decimal usd) => "$" + (usd > 0m && usd < 0.01m
        ? usd.ToString("0.000####", CultureInfo.InvariantCulture)
        : usd.ToString("0.00", CultureInfo.InvariantCulture));

    private static TuiRole RoleFor(TuiNodeStatus status) => status switch
    {
        TuiNodeStatus.Complete => TuiRole.Success,
        TuiNodeStatus.Failed => TuiRole.Error,
        TuiNodeStatus.InProgress => TuiRole.Warning,
        _ => TuiRole.Dim,
    };

    private static TuiRole RoleFor(GateOutcome outcome) =>
        outcome == GateOutcome.Pass ? TuiRole.Success : TuiRole.Error;

    private static TuiRole RoleFor(InvocationStopReason reason) => reason switch
    {
        InvocationStopReason.Succeeded => TuiRole.Success,
        InvocationStopReason.AuthFailed or InvocationStopReason.Failed => TuiRole.Error,
        _ => TuiRole.Warning,
    };

    private static string GateWord(GateOutcome outcome) => outcome == GateOutcome.Pass ? "✓" : "✗";

    private static string VerdictWord<T>(T value) where T : struct => Words(value);

    /// <summary>Enum name as lowercase words: AwaitingReview → "awaiting review".</summary>
    public static string StatusWord<T>(T value) where T : struct => Words(value);

    private static string Words<T>(T value) where T : struct
    {
        var name = value.ToString()!;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch) && i > 0)
                sb.Append(' ');
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}
