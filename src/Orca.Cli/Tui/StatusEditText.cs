using Orca.Core.Domain;
using Orca.Core.Vault;

namespace Orca.Cli.Tui;

/// <summary>Pure styled-text projections for SUG-17's status-edit confirm modals: plan
/// approve/un-approve on <c>a</c>, checkpoint mark-complete/re-open on <c>m</c>. Every
/// body names the exact transition and the file being edited — the TUI is the pen, the
/// human is the decider, and Obsidian stays a fallback, never a requirement. No
/// Terminal.Gui types, so all four are unit-tested without the shell.</summary>
internal static class StatusEditText
{
    public static IReadOnlyList<StyledLine> PlanFlip(Plan plan, string path, PlanStatus target) =>
    [
        StyledLine.Of(
            new("● ", TuiRole.Accent),
            new(target == PlanStatus.Approved ? "Approve plan " : "Un-approve plan ", TuiRole.Text),
            new(plan.PlanId, TuiRole.Heading),
            new("?", TuiRole.Text)),
        StyledLine.Empty,
        StyledLine.Of(
            new("Flips ", TuiRole.Text),
            new($"status: {SchemaEnums.ToYaml(plan.Status)} → {SchemaEnums.ToYaml(target)}", TuiRole.Dim),
            new(" (plus updated) in", TuiRole.Text)),
        StyledLine.Plain(path, TuiRole.Dim),
        StyledLine.Empty,
        StyledLine.Plain("Only those frontmatter lines change — body prose and formatting survive byte-for-byte.", TuiRole.Faint),
    ];

    /// <summary>The mark-complete confirm. With <paramref name="evidenceDescription"/> the
    /// body cites it; without, it warns loudly — the same distinction the CLI draws with
    /// its --anyway flag.</summary>
    public static IReadOnlyList<StyledLine> MarkComplete(
        Checkpoint checkpoint, string path, string? evidenceDescription) =>
    [
        StyledLine.Of(
            new("● ", TuiRole.Accent),
            new("Mark checkpoint ", TuiRole.Text),
            new(checkpoint.Id, TuiRole.Heading),
            new(" complete?", TuiRole.Text)),
        StyledLine.Empty,
        evidenceDescription is { } reason
            ? StyledLine.Of(new("evidence  ", TuiRole.Faint), new(reason, TuiRole.Text))
            : StyledLine.Of(
                new("● ", TuiRole.Warning),
                new("no completion evidence found", TuiRole.Warning),
                new(" — no approval commit on its branch, no passing verification record. Mark complete anyway?", TuiRole.Text)),
        StyledLine.Empty,
        StyledLine.Of(
            new("Flips ", TuiRole.Text),
            new($"status: {SchemaEnums.ToYaml(checkpoint.Status)} → complete", TuiRole.Dim),
            new(" (plus updated) in", TuiRole.Text)),
        StyledLine.Plain(path, TuiRole.Dim),
    ];

    public static IReadOnlyList<StyledLine> Reopen(Checkpoint checkpoint, string path) =>
    [
        StyledLine.Of(
            new("● ", TuiRole.Warning),
            new("Re-open checkpoint ", TuiRole.Text),
            new(checkpoint.Id, TuiRole.Heading),
            new("?", TuiRole.Text)),
        StyledLine.Empty,
        StyledLine.Of(
            new("Flips ", TuiRole.Text),
            new("status: complete → pending", TuiRole.Dim),
            new(" (plus updated) in", TuiRole.Text)),
        StyledLine.Plain(path, TuiRole.Dim),
        StyledLine.Empty,
        StyledLine.Plain("The vault's verification/reconciliation records stay — the next attempt number continues from that evidence (never reused, never renumbered).", TuiRole.Faint),
    ];
}
