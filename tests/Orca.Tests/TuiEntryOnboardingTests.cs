using Orca.Cli.Tui;
using Orca.Core.Config;

namespace Orca.Tests;

/// <summary>Build-order for plan orca-init, checkpoint 4: the bare-<c>orca</c> / <c>orca ui</c>
/// onboarding offer. The offer's confirm is injected (same style as the vault pick in
/// <see cref="InitCommandTests"/>), so the decline branch is exercised without a real console:
/// declining is a clean choice — exit 0, and nothing is scaffolded. The accept branch delegates
/// to <see cref="Orca.Cli.Commands.InitCommand.Scaffold"/>, whose bytes are round-tripped
/// through <see cref="ConfigLoader.Load"/> by <see cref="OrcaConfigScaffoldTests"/> and driven
/// end-to-end in a throwaway repo at review.</summary>
public sealed class TuiEntryOnboardingTests
{
    [Fact]
    public void Declining_the_offer_scaffolds_nothing_and_exits_zero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orca-offer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var offered = false;
            var code = TuiEntry.OfferInit(dir, () =>
            {
                offered = true;
                return false;
            });

            Assert.Equal(0, code); // decline is a clean choice, not a failure
            Assert.True(offered); // the offer was actually put to the user
            Assert.False(File.Exists(Path.Combine(dir, ConfigLoader.RepoFileName))); // no file written on decline
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
