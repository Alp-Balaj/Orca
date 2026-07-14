using Orca.Core.Run;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

// Terminal.Gui 2.4.17 ships the legacy static `Application` (Invoke/RequestStop) and
// `TextView` marked [Obsolete] — "the legacy static Application object is going away" — but
// both are present and functional, and the static Application.Invoke/AddTimeout marshaling
// primitive is exactly what decision 1 approved for this feature (§1). We implement that
// approved contract as written and suppress the deprecation here; migrating off the static
// Application (and off TextView) is a separate, owner-facing decision flagged in the step-4
// summary, not a step-4 redesign.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>The read-only TUI shell (build-order step 4): the four-region window over a
/// <see cref="StatusReport"/> — the plans/checkpoints tree (left), the detail pane
/// (right-top), the idle activity pane (right-bottom), and the status line (bottom) —
/// with navigate / refresh / quit and nothing else. It starts nothing and decides nothing.
///
/// Kept deliberately thin so Terminal.Gui minor-version churn touches presentation only:
/// every projection (the node tree, the pane text, the status line) is the pure
/// <see cref="TuiTreeModel"/> / <see cref="TuiText"/> code this merely renders.</summary>
internal sealed class MainScreen : Window
{
    private readonly Func<StatusReport> _rebuild;
    private readonly TreeView<TuiTreeNode> _tree;
    private readonly TextView _detail;
    private readonly TextView _activity;
    private readonly Label _statusLine;
    private StatusReport _report;

    public MainScreen(StatusReport report, Func<StatusReport> rebuild)
    {
        _report = report;
        _rebuild = rebuild;
        Title = "Orca";

        // Left: the tree, inside a framed region. Two rows are reserved at the very bottom
        // for the status line and the key hints, hence Dim.Fill() - 2.
        var left = new FrameView
        {
            Title = "plans (vault)",
            X = 0,
            Y = 0,
            Width = Dim.Percent(40),
            Height = Dim.Fill() - 2,
        };
        _tree = new TreeView<TuiTreeNode>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            // Children selector + can-expand predicate — generic over depth, no hardcoded
            // two levels, so a future roadmap→epic level nests without a tree redesign.
            TreeBuilder = new DelegateTreeBuilder<TuiTreeNode>(n => n.Children, n => n.Children.Count > 0),
        };
        _tree.AspectGetter = node => node.Label;
        _tree.ColorGetter = node => SchemeFor(node.Status)!;
        left.Add(_tree);

        // Right column: detail over activity.
        var detailFrame = new FrameView
        {
            Title = "detail",
            X = Pos.Right(left),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(55),
        };
        _detail = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
        };
        detailFrame.Add(_detail);

        var activityFrame = new FrameView
        {
            Title = "activity",
            X = Pos.Right(left),
            Y = Pos.Bottom(detailFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2,
        };
        _activity = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
        };
        activityFrame.Add(_activity);

        // Bottom two rows: the status line, then the key-hint line. Plain labels rather than
        // a StatusBar so no navigation key (Tab / arrows) is ever intercepted from the tree.
        _statusLine = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
        var hints = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Tab focus · ↑↓ navigate · ←/→ expand · r refresh · q quit",
        };

        Add(left, detailFrame, activityFrame, _statusLine, hints);

        // Dim the selection highlight from the default near-white to a light grey. Set on the
        // tree's own scheme so it covers every row — including pending nodes, whose per-node
        // colour getter returns null and would otherwise keep the bright default focus.
        var baseScheme = _tree.GetScheme();
        _tree.SetScheme(new Scheme(baseScheme)
        {
            Focus = new Attribute(Color.Black, Color.Gray, baseScheme.Focus.Style),
            HotFocus = new Attribute(Color.Black, Color.Gray, baseScheme.HotFocus.Style),
        });

        _tree.SelectionChanged += (_, e) => UpdateSelection(e.NewValue);
        KeyDown += OnKey;

        LoadReport(report, previousSelection: null);
        _tree.SetFocus();
    }

    /// <summary>Rebuild the tree from a (possibly fresh) report, preserving the selection by
    /// identity when the same node still exists.</summary>
    private void LoadReport(StatusReport report, NodeIdentity? previousSelection)
    {
        _report = report;
        var roots = TuiTreeModel.Build(report);

        _tree.ClearObjects();
        _tree.AddObjects(roots);
        foreach (var root in roots)
            _tree.Expand(root);

        var selection = FindByIdentity(roots, previousSelection) ?? roots.FirstOrDefault();
        if (selection is not null)
        {
            _tree.SelectedObject = selection;
            _tree.GoTo(selection);
        }

        UpdateSelection(selection);
    }

    private void Refresh()
    {
        NodeIdentity? current = _tree.SelectedObject is { } node ? NodeIdentity.Of(node) : (NodeIdentity?)null;
        LoadReport(_rebuild(), current);
    }

    private void UpdateSelection(TuiTreeNode? node)
    {
        _detail.Text = node is null ? "No plans in the vault." : TuiText.Detail(node);
        _activity.Text = node is null ? "" : TuiText.Activity(node);
        _statusLine.Text = TuiText.StatusLine(_report, node);
    }

    private void OnKey(object? sender, Key key)
    {
        if (key == Key.Q || key == Key.Esc || key == Key.Q.WithCtrl)
        {
            Application.RequestStop();
            key.Handled = true;
        }
        else if (key == Key.R || key == Key.F5)
        {
            Refresh();
            key.Handled = true;
        }
    }

    /// <summary>Per-node colour, using the same status categories the tree glyphs encode.
    /// Returns null for pending nodes, which the tree renders in its default scheme.
    ///
    /// Only the FOREGROUND is recoloured — each role's background and style are copied from
    /// the tree's resolved scheme, so a coloured row stays readable text on the normal
    /// background (and keeps the focus highlight) instead of a solid block of colour.</summary>
    private Scheme? SchemeFor(TuiNodeStatus status)
    {
        Color? foreground = status switch
        {
            TuiNodeStatus.Complete => Color.Green,
            TuiNodeStatus.Failed => Color.Red,
            TuiNodeStatus.InProgress => Color.Yellow,
            _ => null,
        };
        if (foreground is not { } fg)
            return null;

        var baseScheme = _tree.GetScheme();
        return new Scheme(baseScheme)
        {
            Normal = Recolor(baseScheme.Normal, fg),
            Focus = Recolor(baseScheme.Focus, fg),
            HotNormal = Recolor(baseScheme.HotNormal, fg),
            HotFocus = Recolor(baseScheme.HotFocus, fg),
        };
    }

    private static Attribute Recolor(Attribute attribute, Color foreground) =>
        new(foreground, attribute.Background, attribute.Style);

    private static TuiTreeNode? FindByIdentity(IReadOnlyList<TuiTreeNode> roots, NodeIdentity? identity)
    {
        if (identity is null)
            return null;

        foreach (var root in roots)
        {
            if (NodeIdentity.Of(root) == identity)
                return root;
            foreach (var child in root.Children)
                if (NodeIdentity.Of(child) == identity)
                    return child;
        }

        return null;
    }

    /// <summary>The stable identity of a node across a refresh — plan id plus optional
    /// checkpoint id — so the selection survives a rebuild when the same entity is still
    /// present.</summary>
    private readonly record struct NodeIdentity(string PlanId, string? CheckpointId)
    {
        public static NodeIdentity Of(TuiTreeNode node) =>
            new(node.Plan.Plan.PlanId, node.Checkpoint?.Id);
    }
}
