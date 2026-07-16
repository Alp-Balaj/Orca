using Orca.Cli.Tui;
using Orca.Core.Domain;

namespace Orca.Tests;

/// <summary>SUG-16 proposal A's TUI-lane text projections: the "run next now?" offer that
/// doubles as the chained attempt's authorize-the-spend moment, and the merge-failed body
/// that stops the chain. Mirrors the CONTENT of the CLI lane's chaining prompt in
/// <c>CheckpointRunCommand</c>.</summary>
public sealed class ChainOfferTextTests
{
    private static string Plain(IReadOnlyList<StyledLine> lines)
        => string.Join("\n", lines.Select(l => l.PlainText()));

    private static readonly Checkpoint Next = TuiTestData.MakeCheckpoint(
        "04-wire-ui", CheckpointStatus.Pending, "Wire the new endpoint into the UI");

    [Fact]
    public void Offer_names_the_next_checkpoint_and_its_description()
    {
        var text = Plain(ChainOfferText.Offer(Next, "orca/demo/03-endpoint", "main"));
        Assert.Contains("04-wire-ui", text);
        Assert.Contains("Wire the new endpoint into the UI", text);
    }

    [Fact]
    public void Offer_says_what_confirming_does_merge_first_then_launch()
    {
        var text = Plain(ChainOfferText.Offer(Next, "orca/demo/03-endpoint", "main"));
        Assert.Contains("fast-forwards main to orca/demo/03-endpoint", text);
        Assert.Contains("launches its attempt in this same session", text);
    }

    [Fact]
    public void Offer_states_the_gate_is_kept()
    {
        var text = Plain(ChainOfferText.Offer(Next, "orca/demo/03-endpoint", "main"));
        Assert.Contains("never deciding", text);
        Assert.Contains("stops at its own gate", text);
    }

    [Fact]
    public void Merge_failed_names_both_branches_and_the_manual_path_out()
    {
        var text = Plain(ChainOfferText.MergeFailed("orca/demo/03-endpoint", "main"));
        Assert.Contains("could not fast-forward", text);
        Assert.Contains("main moved since orca/demo/03-endpoint branched", text);
        Assert.Contains("Merge manually", text);
    }
}
