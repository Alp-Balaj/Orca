using Orca.Cli.Tui;

namespace Orca.Tests;

/// <summary>SUG-18: the built-in palettes and their resolution — semantics stay fixed
/// (status colours distinguishable per theme), names validate loudly.</summary>
public sealed class TuiThemeTests
{
    [Fact]
    public void All_four_built_ins_resolve_and_null_means_dark()
    {
        Assert.Same(TuiPalettes.Dark, TuiPalettes.Resolve(null));
        Assert.Same(TuiPalettes.Dark, TuiPalettes.Resolve("dark"));
        Assert.Same(TuiPalettes.Light, TuiPalettes.Resolve("light"));
        Assert.Same(TuiPalettes.HighContrast, TuiPalettes.Resolve("high-contrast"));
        Assert.Same(TuiPalettes.Terminal, TuiPalettes.Resolve("terminal"));
    }

    [Fact]
    public void An_unknown_theme_name_lists_the_built_ins()
    {
        var ex = Assert.Throws<ArgumentException>(() => TuiPalettes.Resolve("solarized"));
        Assert.Contains("dark, light, high-contrast, terminal", ex.Message);
    }

    [Fact]
    public void Status_colours_stay_distinguishable_in_every_theme()
    {
        foreach (var palette in new[] { TuiPalettes.Dark, TuiPalettes.Light, TuiPalettes.HighContrast, TuiPalettes.Terminal })
        {
            var statuses = new[] { palette.Success, palette.Error, palette.Warning, palette.Dim };
            Assert.Equal(statuses.Length, statuses.Distinct().Count());
            Assert.DoesNotContain(palette.Background, statuses);
        }
    }
}
