using Orca.Core.Config;
using Orca.Core.Domain;
using Orca.Core.Execution;
using Orca.Core.Git;
using Orca.Core.Planning;
using Orca.Core.Run;
using Orca.Core.Vault;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

// Terminal.Gui 2.4.17 ships the legacy static `Application` (Invoke/RequestStop) marked
// [Obsolete] — "the legacy static Application object is going away" — but it is present and
// functional, and the static Application.Invoke/AddTimeout marshaling primitive is exactly
// what decision 1 approved for this feature (§1). We implement that approved contract as
// written and suppress the deprecation here; migrating off the static Application is a
// separate, owner-facing decision flagged in the step-4 summary, not a redesign.
#pragma warning disable CS0618

namespace Orca.Cli.Tui;

/// <summary>The read-only TUI shell (build-order step 4): a Claude Code-styled window over
/// a <see cref="StatusReport"/> — brand header, the plans/checkpoints tree (left), the
/// detail pane (right-top), the idle activity pane (right-bottom), and the status + hint
/// lines (bottom) — with navigate / refresh / quit and nothing else. It starts nothing and
/// decides nothing.
///
/// Kept deliberately thin so Terminal.Gui minor-version churn touches presentation only:
/// every projection (the node tree, the pane text, the status line) is the pure
/// <see cref="TuiTreeModel"/> / <see cref="TuiText"/> code this merely renders, and every
/// colour comes from <see cref="TuiTheme"/>.</summary>
internal sealed class MainScreen : Window
{
    private readonly Func<StatusReport> _rebuild;
    private readonly OrcaConfig _config;
    private readonly GitWorkspace _workspace;
    private readonly TreeView<TuiTreeNode> _tree;
    private readonly StyledView _detail;
    private readonly StyledView _activity;
    private readonly StyledView _statusLine;
    private StatusReport _report;

    public MainScreen(StatusReport report, Func<StatusReport> rebuild, OrcaConfig config, GitWorkspace workspace)
    {
        _report = report;
        _rebuild = rebuild;
        _config = config;
        _workspace = workspace;

        // The window is a full-bleed canvas, not a box: no outer border, no title bar,
        // one column of breathing room at each side — the Claude Code page, not a dialog.
        Title = string.Empty;
        BorderStyle = LineStyle.None;
        SetScheme(TuiTheme.BaseScheme);
        Padding!.Thickness = new Thickness(1, 0, 1, 0);

        // Brand header, Claude Code's opening line: coral asterisk, bold name, dim purpose.
        var header = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        header.SetLines(
        [
            StyledLine.Of(
                new("✻ ", TuiRole.Accent),
                new("Orca", TuiRole.Heading),
                new("  plans · checkpoints · evidence", TuiRole.Faint)),
        ]);

        // Left: the plans/checkpoints tree.
        var plansPane = MakePane("plans");
        plansPane.X = 0;
        plansPane.Y = 2;
        plansPane.Width = Dim.Percent(32);
        plansPane.Height = Dim.Fill(2);

        _tree = new TreeView<TuiTreeNode>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            // Children selector + can-expand predicate — generic over depth, no hardcoded
            // two levels, so a future roadmap→epic level nests without a tree redesign.
            TreeBuilder = new DelegateTreeBuilder<TuiTreeNode>(n => n.Children, n => n.Children.Count > 0),
            AllowLetterBasedNavigation = false,
        };
        _tree.Style.ShowBranchLines = false;
        _tree.Style.ExpandableSymbol = new System.Text.Rune('▸');
        _tree.Style.CollapseableSymbol = new System.Text.Rune('▾');
        // One space between the ▸/▾ fold symbol and the plan id; checkpoints (no fold
        // symbol) carry their own glyph in the label.
        _tree.AspectGetter = node => node.Kind == TuiNodeKind.Plan ? " " + node.Label : node.Label;
        _tree.ColorGetter = SchemeFor;
        _tree.SetScheme(NodeScheme(TuiTheme.Text));
        plansPane.Add(_tree);

        // Right column: detail over activity, one gap column from the tree.
        var detailPane = MakePane("detail");
        detailPane.X = Pos.Right(plansPane) + 1;
        detailPane.Y = 2;
        detailPane.Width = Dim.Fill();
        detailPane.Height = Dim.Percent(55);

        _detail = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        detailPane.Add(_detail);

        var activityPane = MakePane("activity");
        activityPane.X = Pos.Right(plansPane) + 1;
        activityPane.Y = Pos.Bottom(detailPane);
        activityPane.Width = Dim.Fill();
        activityPane.Height = Dim.Fill(2);

        _activity = new StyledView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        activityPane.Add(_activity);

        // Bottom two rows, Claude Code's footer: the context line, then the faint key
        // hints. Plain views rather than a StatusBar so no navigation key (Tab / arrows)
        // is ever intercepted from the tree.
        _statusLine = new StyledView { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, CanFocus = false };
        var hints = new StyledView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };
        hints.SetLines([TuiText.Hints()]);

        Add(header, plansPane, detailPane, activityPane, _statusLine, hints);

        _tree.SelectionChanged += (_, e) => UpdateSelection(e.NewValue);
        KeyDown += OnKey;

        LoadReport(report, previousSelection: null);
        _tree.SetFocus();
    }

    /// <summary>One bordered region: rounded, quiet grey chrome that turns coral while the
    /// pane owns focus — the "active thing gets the accent border" cue Claude Code's
    /// dialogs use, and the visible answer to "where am I" for a first-time user. The
    /// title is our own label over the top border line (<c>╭─ plans ──</c>) rather than
    /// the stock <c>┤plans├</c> tab decoration.</summary>
    private static View MakePane(string title)
    {
        var pane = new View
        {
            BorderStyle = LineStyle.Rounded,
            CanFocus = true,
        };
        pane.SetScheme(TuiTheme.BaseScheme);
        pane.Padding!.Thickness = new Thickness(1, 0, 1, 0);

        var borderView = pane.Border!.GetOrCreateView();
        borderView.SetScheme(TuiTheme.BorderIdle);
        var titleLabel = new Label { X = 2, Y = 0, Text = $" {title} " };
        titleLabel.SetScheme(TuiTheme.BorderIdle);
        borderView.Add(titleLabel);

        pane.HasFocusChanged += (_, _) =>
        {
            var scheme = pane.HasFocus ? TuiTheme.BorderFocused : TuiTheme.BorderIdle;
            borderView.SetScheme(scheme);
            titleLabel.SetScheme(scheme);
            pane.SetNeedsDraw();
        };
        return pane;
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
        _tree.ScrollOffsetVertical = 0;
        if (selection is not null)
        {
            _tree.SelectedObject = selection;
            if (!ReferenceEquals(selection, roots[0]))
                _tree.EnsureVisible(selection);
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
        _detail.SetLines(node is null ? TuiText.EmptyVault() : TuiText.Detail(node));
        _activity.SetLines(node is null ? [] : TuiText.Activity(node));
        _statusLine.SetLines([TuiText.StatusLine(_report, node)]);
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
        else if (key == Key.X)
        {
            RunSelectedCheckpoint();
            key.Handled = true;
        }
        else if (key == Key.G)
        {
            GeneratePlan();
            key.Handled = true;
        }
        else if (key == Key.A)
        {
            EditSelectedPlanStatus();
            key.Handled = true;
        }
        else if (key == Key.M)
        {
            EditSelectedCheckpointStatus();
            key.Handled = true;
        }
    }

    /// <summary>SUG-17: <c>a</c> on a plan node flips draft → approved (or approved back
    /// to draft) through the surgical frontmatter editor, after an explicit confirm modal
    /// naming the transition and the file. A human pressing <c>a</c> in a confirm modal is
    /// exactly as deliberate as typing the status in source mode — the authority is
    /// identical, only the pen changes; the Planner still can never self-approve. Any
    /// other plan status is a silent no-op (like <c>x</c> on a plan node).</summary>
    private void EditSelectedPlanStatus()
    {
        if (_tree.SelectedObject is not { Kind: TuiNodeKind.Plan } node)
            return;

        var plan = node.Plan.Plan;
        if (plan.Status is not (PlanStatus.Draft or PlanStatus.Approved))
            return;

        var target = plan.Status == PlanStatus.Draft ? PlanStatus.Approved : PlanStatus.Draft;
        var path = new VaultLayout(_config.VaultRoot, _config.PlansDir).PlanFile(plan.PlanId);

        var confirmed = TuiModal.Ask(
            target == PlanStatus.Approved ? "approve plan" : "un-approve plan",
            StatusEditText.PlanFlip(plan, path, target),
            new TuiModal.Choice<bool>(Key.Enter,
                target == PlanStatus.Approved ? "enter approve" : "enter un-approve", true),
            new TuiModal.Choice<bool>(Key.Esc, "esc cancel", false));
        if (!confirmed)
            return;

        ApplyStatusEdit(() => CheckpointStatusEditor.SetPlanStatus(
            path, plan.Status, target, DateTimeOffset.UtcNow));
    }

    /// <summary>SUG-17: <c>m</c> on a checkpoint node marks it complete — with the confirm
    /// modal citing the completion evidence (approval commit / passing verification), or
    /// warning loudly when there is none — and on an already-complete checkpoint offers
    /// re-opening it (complete → pending), with the attempt-numbering caveat spelled out.</summary>
    private void EditSelectedCheckpointStatus()
    {
        if (_tree.SelectedObject is not { Kind: TuiNodeKind.Checkpoint } node)
            return;

        var plan = node.Plan.Plan;
        var checkpoint = node.Checkpoint!;
        var layout = new VaultLayout(_config.VaultRoot, _config.PlansDir);
        var path = layout.PlanFile(plan.PlanId);

        if (checkpoint.Status == CheckpointStatus.Complete)
        {
            var reopen = TuiModal.Ask("re-open checkpoint",
                StatusEditText.Reopen(checkpoint, path),
                new TuiModal.Choice<bool>(Key.Enter, "enter re-open", true),
                new TuiModal.Choice<bool>(Key.Esc, "esc cancel", false));
            if (!reopen)
                return;

            ApplyStatusEdit(() => CheckpointStatusEditor.SetStatus(
                path, checkpoint.Id, CheckpointStatus.Complete, CheckpointStatus.Pending,
                DateTimeOffset.UtcNow));
            return;
        }

        var evidence = CompletionEvidence.Probe(
            _workspace, new VaultReader(layout), plan.PlanId, checkpoint.Id);
        var confirmed = TuiModal.Ask("mark checkpoint complete",
            StatusEditText.MarkComplete(checkpoint, path, evidence.Description),
            new TuiModal.Choice<bool>(Key.Enter,
                evidence.Any ? "enter mark complete" : "enter mark complete anyway", true),
            new TuiModal.Choice<bool>(Key.Esc, "esc cancel", false));
        if (!confirmed)
            return;

        ApplyStatusEdit(() => CheckpointStatusEditor.SetStatus(
            path, checkpoint.Id, checkpoint.Status, CheckpointStatus.Complete,
            DateTimeOffset.UtcNow));
    }

    /// <summary>Run one surgical status edit and refresh. The editor's own guards do the
    /// heavy lifting — expected-status mismatch (the file changed since this report was
    /// built, e.g. Obsidian sync) and parse failures both throw, and land here as an error
    /// modal telling the human to refresh rather than a crash or a blind overwrite.</summary>
    private void ApplyStatusEdit(Action edit)
    {
        try
        {
            edit();
        }
        catch (Exception e)
        {
            TuiModal.Ask("cannot edit this status",
                [
                    StyledLine.Of(new("● ", TuiRole.Error), new(e.Message, TuiRole.Text)),
                    StyledLine.Empty,
                    StyledLine.Plain("If the file changed outside this session, press r to refresh and retry.", TuiRole.Dim),
                ],
                new TuiModal.Choice<bool>(Key.Enter, "enter back", false),
                new TuiModal.Choice<bool>(Key.Esc, "esc back", false));
            return;
        }

        Refresh();
    }

    /// <summary>The plan-generate action (build-order step 7): open the form (item 4a), and on
    /// a valid submit assemble a <see cref="PlanningRequest"/> exactly as
    /// <c>PlanGenerateCommand</c> does, render the preview modal (the authorize-the-spend
    /// moment — cancel spends nothing), run the same <c>OrcaIgnore.EnsureIgnored</c> guard the
    /// CLI runs (added-the-entry → stop, commit it, re-run; Orca never commits on its own),
    /// and on authorize drive one gate-less <see cref="SingleCandidatePlanningStrategy"/> run
    /// through the full-screen <see cref="PlanScreen"/>. On return the report rebuilds and the
    /// shell refreshes — the freshly drafted plan now appears in the tree. This is a thin front
    /// end (Prepare → gitignore guard → GenerateAsync); it creates a NEW plan and so is not
    /// tied to the tree selection, though a selected plan's id pre-fills the form.</summary>
    private void GeneratePlan()
    {
        var prefillPlanId = _tree.SelectedObject?.Plan.Plan.PlanId;
        if (PlanForm.Show(prefillPlanId) is not { } resolved)
            return; // cancelled — spends nothing, touches nothing.

        var request = new PlanningRequest
        {
            PlanId = resolved.PlanId,
            Goal = resolved.Goal,
            Title = resolved.Title,
            TargetRepoName = Path.GetFileName(_workspace.RepoRoot),
            BaseBranch = resolved.BaseBranch,
        };

        var strategy = new SingleCandidatePlanningStrategy(_config, new ClaudeCodeExecutor(), _workspace.RepoRoot);

        PlanningPreview preview;
        try
        {
            // Read-only: resolves attempt, plan path, prompt, argv. Throws (never guesses) when
            // the state won't allow a run — the "already exists / already succeeded" refusals —
            // surfaced in a modal rather than crashing the shell.
            preview = strategy.Prepare(request);
        }
        catch (Exception e)
        {
            TuiModal.Ask("cannot generate this plan",
                [StyledLine.Of(new("● ", TuiRole.Error), new(e.Message, TuiRole.Text))],
                new TuiModal.Choice<bool>(Key.Enter, "enter back", false),
                new TuiModal.Choice<bool>(Key.Esc, "esc back", false));
            return;
        }

        var authorized = TuiModal.Ask("preview — authorize plan generation", PlanPreviewText.Describe(preview),
            new TuiModal.Choice<bool>(Key.Enter, "enter generate (spends quota)", true),
            new TuiModal.Choice<bool>(Key.Esc, "esc cancel (spends nothing)", false));
        if (!authorized)
            return;

        // The thin front end's gitignore guard (mirrors PlanGenerateCommand): if it just added
        // the entry, stop before spending — commit that, then re-run.
        if (OrcaIgnore.EnsureIgnored(_workspace.RepoRoot))
        {
            TuiModal.Ask("gitignore updated", PlanOutcomeText.GitignoreAdded(),
                new TuiModal.Choice<bool>(Key.Enter, "enter back", false),
                new TuiModal.Choice<bool>(Key.Esc, "esc back", false));
            Refresh();
            return;
        }

        PlanScreen.Run(strategy, preview);

        // Run over — the workflow stops (no auto-advance): rebuild and refresh so the freshly
        // drafted plan appears in the tree (selection preserved by identity when it survives).
        Refresh();
    }

    /// <summary>The run/approve action (build-order step 5): on a selected checkpoint,
    /// prepare the run (read-only). A fresh attempt renders the preview modal — the
    /// authorize-the-spend moment — and only proceeds if the human authorizes; a preview
    /// Cancel spends nothing. A checkpoint already awaiting a decision
    /// (<see cref="CheckpointRunPreview.PendingDecision"/>) has nothing new to authorize —
    /// resuming it launches no invocation, so it skips straight to the decision, exactly as
    /// <c>checkpoint-run</c> already does on the CLI (<c>CheckpointRunCommand</c> has no
    /// confirm step either; only the pending-decision notice). Either way, the session then
    /// runs to its decision in the full-screen <see cref="RunScreen"/>. On return the report
    /// is rebuilt and the shell refreshes with the selection preserved.</summary>
    private void RunSelectedCheckpoint()
    {
        if (_tree.SelectedObject is not { Kind: TuiNodeKind.Checkpoint } node)
            return; // x on a plan node (or empty vault) is a no-op this step.

        var session = new CheckpointRunSession(_config, _workspace, new ClaudeCodeExecutor());

        CheckpointRunPreview preview;
        try
        {
            // Read-only: resolves plan/checkpoint, attempt, branch, prompt, argv. Throws
            // (never guesses) when the state won't allow a run — surfaced in a modal rather
            // than crashing the shell. PendingDecision = true is legal here: the same
            // preview → ExecuteAsync path resumes it (the session branches internally).
            preview = session.Prepare(node.Plan.Plan.PlanId, node.Checkpoint!.Id);
        }
        catch (Exception e)
        {
            TuiModal.Ask("cannot run this checkpoint",
                [StyledLine.Of(new("● ", TuiRole.Error), new(e.Message, TuiRole.Text))],
                new TuiModal.Choice<bool>(Key.Enter, "enter back", false),
                new TuiModal.Choice<bool>(Key.Esc, "esc back", false));
            return;
        }

        if (NeedsAuthorizePreview(preview.PendingDecision))
        {
            var authorized = TuiModal.Ask("preview — authorize the run", RunPreviewText.Describe(preview),
                new TuiModal.Choice<bool>(Key.Enter, "enter run (spends quota)", true),
                new TuiModal.Choice<bool>(Key.Esc, "esc cancel (spends nothing)", false));
            if (!authorized)
                return;
        }

        RunScreen.Run(session, preview);

        // Session over — the workflow stops (no auto-advance): rebuild and refresh, keeping
        // the selection so the just-run checkpoint stays put with its updated evidence.
        Refresh();
    }

    /// <summary>False exactly when resuming a checkpoint that's already awaiting a
    /// decision — nothing new would be launched, so there's no spend to authorize.
    /// Pulled out of <see cref="RunSelectedCheckpoint"/> (SUG-15) so this decision has a
    /// test independent of Terminal.Gui/<see cref="TuiModal"/> and of
    /// <see cref="CheckpointRunPreview"/>'s much larger shape.</summary>
    internal static bool NeedsAuthorizePreview(bool pendingDecision) => !pendingDecision;

    /// <summary>Per-node colour in the tree, from the same status categories the glyphs
    /// encode: green done, red failed, yellow active, dim pending (plans stay in the
    /// default colour while pending — they are the primary items). The run state's current
    /// checkpoint gets the coral accent, outranking its status. Selection keeps the node's
    /// colour over a warm dark highlight.</summary>
    private Scheme SchemeFor(TuiTreeNode node)
    {
        var foreground = node switch
        {
            { IsCurrent: true } => TuiTheme.Accent,
            { Status: TuiNodeStatus.Complete } => TuiTheme.Success,
            { Status: TuiNodeStatus.Failed } => TuiTheme.Error,
            { Status: TuiNodeStatus.InProgress } => TuiTheme.Warning,
            { Kind: TuiNodeKind.Plan } => TuiTheme.Text,
            _ => TuiTheme.Dim,
        };
        return NodeScheme(foreground);
    }

    private static Scheme NodeScheme(Color foreground)
    {
        var normal = new Attribute(foreground, TuiTheme.Background);
        var selected = new Attribute(foreground, TuiTheme.SelectionBg, TextStyle.Bold);
        return new Scheme(TuiTheme.BaseScheme)
        {
            Normal = normal,
            HotNormal = normal,
            Focus = selected,
            HotFocus = selected,
        };
    }

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
