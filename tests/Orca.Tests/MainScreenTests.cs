using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>The one decision pulled out of <see cref="MainScreen"/> for direct testing
/// (SUG-15) — everything else in that class is Terminal.Gui-coupled presentation.</summary>
public class MainScreenTests
{
    [Fact]
    public void A_fresh_attempt_needs_the_authorize_preview()
    {
        Assert.True(MainScreen.NeedsAuthorizePreview(pendingDecision: false));
    }

    [Fact]
    public void Resuming_a_pending_decision_skips_the_preview()
    {
        Assert.False(MainScreen.NeedsAuthorizePreview(pendingDecision: true));
    }
}
