using Orca.Core.Domain;

namespace Orca.Cli.Tui;

/// <summary>Pure styled-text projections for SUG-16 proposal A's "run next now?" moment in
/// the TUI lane — the modal shown right after an Approve when the plan still has a pending
/// checkpoint, and the failure body when the fast-forward merge that chaining performs
/// isn't possible. Mirrors the CONTENT of the CLI lane's chaining prompt and
/// could-not-fast-forward message in <c>CheckpointRunCommand</c>. No Terminal.Gui types,
/// so both are unit-tested without the shell.</summary>
internal static class ChainOfferText
{
    /// <summary>Body for the "run next now?" modal. This modal doubles as the
    /// authorize-the-spend moment for the chained attempt (the CLI's confirm plays the
    /// same role), so it must say what confirming does: merge first, then launch.</summary>
    public static IReadOnlyList<StyledLine> Offer(Checkpoint next, string decidedBranch, string baseBranch) =>
    [
        StyledLine.Of(
            new("● ", TuiRole.Accent),
            new("next: ", TuiRole.Faint),
            new(next.Id, TuiRole.Text),
            new(" — ", TuiRole.Faint),
            new(next.Description, TuiRole.Text)),
        StyledLine.Empty,
        StyledLine.Of(
            new("Running it now first fast-forwards ", TuiRole.Text),
            new(baseBranch, TuiRole.Dim),
            new(" to ", TuiRole.Text),
            new(decidedBranch, TuiRole.Dim),
            new(" (the merge the next checkpoint builds on),", TuiRole.Text)),
        StyledLine.Plain("then launches its attempt in this same session.", TuiRole.Text),
        StyledLine.Empty,
        StyledLine.Plain("Chaining only automates launching, never deciding — the next checkpoint still stops at its own gate.", TuiRole.Dim),
    ];

    /// <summary>Body for the merge-failed modal: the base branch moved since the decided
    /// checkpoint branched, so chaining must stop rather than run the next checkpoint on a
    /// base its plan file doesn't describe.</summary>
    public static IReadOnlyList<StyledLine> MergeFailed(string decidedBranch, string baseBranch) =>
    [
        StyledLine.Of(
            new("● ", TuiRole.Warning),
            new("could not fast-forward", TuiRole.Warning)),
        StyledLine.Empty,
        StyledLine.Of(
            new(baseBranch, TuiRole.Dim),
            new(" moved since ", TuiRole.Text),
            new(decidedBranch, TuiRole.Dim),
            new(" branched from it.", TuiRole.Text)),
        StyledLine.Plain("Merge manually, then run the next checkpoint from the shell.", TuiRole.Text),
    ];
}
