using Orca.Core.Domain;
using Orca.Core.Run;

namespace Orca.Cli.Tui;

/// <summary>What kind of vault entity a tree node represents. Extensible on purpose: the
/// pending roadmap→epic→plan hierarchy (separate proposal, out of scope here) adds members
/// without changing the tree, which renders node DEPTH via
/// <see cref="TuiTreeNode.Children"/>, not a fixed two levels.</summary>
internal enum TuiNodeKind
{
    Plan,
    Checkpoint,
}

/// <summary>The status category a node renders in — the plan/checkpoint lifecycles
/// collapsed onto the four colours <c>StatusCommand</c> uses today
/// (green / red / yellow / default).</summary>
internal enum TuiNodeStatus
{
    Pending,
    InProgress,
    Complete,
    Failed,
}

/// <summary>One node in the TUI tree. A typed, recursive record built as a pure projection
/// of <see cref="StatusReport"/> (see <see cref="TuiTreeModel"/>), so the whole tree — its
/// shape, colours, and glyphs — is unit-testable without the Terminal.Gui shell. Depth is
/// expressed only by <see cref="Children"/>; nothing here hardcodes "plans then
/// checkpoints".</summary>
internal sealed record TuiTreeNode
{
    /// <summary>Display text, glyph included (e.g. "⏺ 03-verifier-loop").</summary>
    public required string Label { get; init; }

    public required TuiNodeKind Kind { get; init; }

    /// <summary>Drives the node's colour via the tree's colour getter.</summary>
    public required TuiNodeStatus Status { get; init; }

    /// <summary>True for the checkpoint the plan's run state points at — the accent-coloured
    /// "work happens here" row, overriding the status colour.</summary>
    public bool IsCurrent { get; init; }

    public required IReadOnlyList<TuiTreeNode> Children { get; init; }

    /// <summary>The owning plan entry — set on EVERY node (a checkpoint node carries its
    /// parent plan's entry), so the status bar can show the selected plan's cost and the
    /// detail/activity panes can reach run state regardless of which node is selected.</summary>
    public required PlanStatusEntry Plan { get; init; }

    /// <summary>Set on checkpoint nodes only.</summary>
    public Checkpoint? Checkpoint { get; init; }

    /// <summary>Set on checkpoint nodes only: the latest vault evidence for this
    /// checkpoint (either record may be absent).</summary>
    public CheckpointEvidence? Evidence { get; init; }
}
