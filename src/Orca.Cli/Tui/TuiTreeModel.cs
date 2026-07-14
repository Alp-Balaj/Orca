using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>Pure projection of a <see cref="StatusReport"/> into the TUI tree's node model.
/// No Terminal.Gui types: the whole plan→checkpoint tree, its status colours, and its
/// glyphs are built and unit-tested here, and the shell (<c>MainScreen</c>) only renders
/// the result. The status→colour mapping and the current-checkpoint <c>→</c> marker match
/// <c>StatusCommand</c> exactly.</summary>
internal static class TuiTreeModel
{
    /// <summary>Roots of the tree — one per plan in the report, in report order. Each plan
    /// node's children are its checkpoints; the recursion is generic over depth.</summary>
    public static IReadOnlyList<TuiTreeNode> Build(StatusReport report)
    {
        var roots = new List<TuiTreeNode>(report.Plans.Count);
        foreach (var entry in report.Plans)
        {
            var currentCheckpointId = entry.RunState?.CheckpointId;
            var children = new List<TuiTreeNode>(entry.Plan.Checkpoints.Count);
            foreach (var checkpoint in entry.Plan.Checkpoints)
            {
                var isCurrent = currentCheckpointId is not null
                    && string.Equals(checkpoint.Id, currentCheckpointId, StringComparison.Ordinal);
                entry.Evidence.TryGetValue(checkpoint.Id, out var evidence);
                children.Add(new TuiTreeNode
                {
                    Label = $"{CheckpointGlyph(checkpoint.Status, isCurrent)} {checkpoint.Id}",
                    Kind = TuiNodeKind.Checkpoint,
                    Status = MapCheckpointStatus(checkpoint.Status),
                    Children = [],
                    Plan = entry,
                    Checkpoint = checkpoint,
                    Evidence = evidence,
                });
            }

            roots.Add(new TuiTreeNode
            {
                Label = entry.Plan.PlanId,
                Kind = TuiNodeKind.Plan,
                Status = MapPlanStatus(entry.Plan.Status),
                Children = children,
                Plan = entry,
            });
        }

        return roots;
    }

    /// <summary>The exact checkpoint status→colour mapping <c>StatusCommand</c> uses:
    /// Complete → green, Rejected/Blocked → red, InProgress/AwaitingReview → yellow, else
    /// pending.</summary>
    public static TuiNodeStatus MapCheckpointStatus(CheckpointStatus status) => status switch
    {
        CheckpointStatus.Complete => TuiNodeStatus.Complete,
        CheckpointStatus.Rejected or CheckpointStatus.Blocked => TuiNodeStatus.Failed,
        CheckpointStatus.InProgress or CheckpointStatus.AwaitingReview => TuiNodeStatus.InProgress,
        _ => TuiNodeStatus.Pending,
    };

    /// <summary>The analogous projection for a plan's own lifecycle, so a plan node reads
    /// at a glance the same way its checkpoints do.</summary>
    public static TuiNodeStatus MapPlanStatus(PlanStatus status) => status switch
    {
        PlanStatus.Complete => TuiNodeStatus.Complete,
        PlanStatus.Blocked or PlanStatus.Abandoned => TuiNodeStatus.Failed,
        PlanStatus.InProgress => TuiNodeStatus.InProgress,
        _ => TuiNodeStatus.Pending,
    };

    /// <summary>The leading glyph. The run state's current checkpoint is marked <c>→</c>
    /// exactly as the CLI marks it, overriding the status glyph; otherwise <c>✓</c>
    /// complete, <c>✗</c> rejected/blocked, <c>·</c> everything else.</summary>
    public static string CheckpointGlyph(CheckpointStatus status, bool isCurrent)
    {
        if (isCurrent)
            return "→";
        return status switch
        {
            CheckpointStatus.Complete => "✓",
            CheckpointStatus.Rejected or CheckpointStatus.Blocked => "✗",
            _ => "·",
        };
    }
}
