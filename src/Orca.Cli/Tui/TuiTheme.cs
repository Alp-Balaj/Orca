using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Orca.Cli.Tui;

/// <summary>One theme's colour set (SUG-18). Status-colour SEMANTICS are fixed across
/// themes — pending/running/complete/failed stay distinguishable in every built-in;
/// only the palette changes.</summary>
internal sealed record TuiPalette(
    string Name,
    Color Accent,
    Color Background,
    Color Text,
    Color Dim,
    Color Faint,
    Color Border,
    Color SelectionBg,
    Color Success,
    Color Error,
    Color Warning);

/// <summary>The built-in themes (SUG-18): <c>dark</c> (the original Claude Code-flavoured
/// palette), <c>light</c>, <c>high-contrast</c>, and <c>terminal</c> — only the
/// terminal's own 16 ANSI colours, so Orca inherits whatever theme the terminal (and the
/// OS theme switcher) is running. Built-ins only, by owner decision — no theming engine,
/// no custom hex maps until someone actually asks.</summary>
internal static class TuiPalettes
{
    public static readonly TuiPalette Dark = new(
        "dark",
        Accent: new Color(0xD9, 0x77, 0x57),
        Background: new Color(0x1A, 0x18, 0x16),
        Text: new Color(0xE8, 0xE6, 0xE3),
        Dim: new Color(0x9A, 0x94, 0x8D),
        Faint: new Color(0x6E, 0x68, 0x62),
        Border: new Color(0x4A, 0x45, 0x40),
        SelectionBg: new Color(0x3E, 0x2E, 0x26),
        Success: new Color(0x5F, 0xAF, 0x6E),
        Error: new Color(0xDE, 0x66, 0x5E),
        Warning: new Color(0xD0, 0xA5, 0x4A));

    public static readonly TuiPalette Light = new(
        "light",
        Accent: new Color(0xB4, 0x4E, 0x2B),
        Background: new Color(0xF5, 0xF2, 0xEE),
        Text: new Color(0x2B, 0x28, 0x24),
        Dim: new Color(0x6B, 0x64, 0x5C),
        Faint: new Color(0x9A, 0x93, 0x8A),
        Border: new Color(0xC4, 0xBD, 0xB3),
        SelectionBg: new Color(0xE5, 0xD8, 0xCC),
        Success: new Color(0x2E, 0x7D, 0x43),
        Error: new Color(0xB3, 0x2D, 0x26),
        Warning: new Color(0x92, 0x6C, 0x0E));

    public static readonly TuiPalette HighContrast = new(
        "high-contrast",
        Accent: new Color(0xFF, 0x8A, 0x50),
        Background: new Color(0x00, 0x00, 0x00),
        Text: new Color(0xFF, 0xFF, 0xFF),
        Dim: new Color(0xC0, 0xC0, 0xC0),
        Faint: new Color(0x90, 0x90, 0x90),
        Border: new Color(0xFF, 0xFF, 0xFF),
        SelectionBg: new Color(0x2A, 0x2A, 0x2A),
        Success: new Color(0x00, 0xFF, 0x66),
        Error: new Color(0xFF, 0x33, 0x33),
        Warning: new Color(0xFF, 0xD7, 0x00));

    public static readonly TuiPalette Terminal = new(
        "terminal",
        Accent: new Color(ColorName16.BrightYellow),
        Background: new Color(ColorName16.Black),
        Text: new Color(ColorName16.White),
        Dim: new Color(ColorName16.Gray),
        Faint: new Color(ColorName16.DarkGray),
        Border: new Color(ColorName16.DarkGray),
        SelectionBg: new Color(ColorName16.Blue),
        Success: new Color(ColorName16.BrightGreen),
        Error: new Color(ColorName16.BrightRed),
        Warning: new Color(ColorName16.Yellow));

    public static readonly IReadOnlyList<string> Names =
        [Dark.Name, Light.Name, HighContrast.Name, Terminal.Name];

    /// <summary>Null or empty means the default (dark); an unknown name throws with the
    /// built-ins listed — the caller surfaces it as a normal config refusal.</summary>
    public static TuiPalette Resolve(string? name) => name switch
    {
        null or "" or "dark" => Dark,
        "light" => Light,
        "high-contrast" => HighContrast,
        "terminal" => Terminal,
        _ => throw new ArgumentException(
            $"unknown tui.theme '{name}' — built-ins: {string.Join(", ", Names)}"),
    };
}

/// <summary>The TUI's single source of colour truth — a pure lookup over the selected
/// <see cref="TuiPalette"/> (SUG-18). <see cref="Select"/> runs once at TUI startup from
/// the GLOBAL config's <c>tui.theme</c> (a machine/user preference, never a repo
/// property); every widget keeps asking this class, nothing else names a colour.</summary>
internal static class TuiTheme
{
    private static TuiPalette _palette = TuiPalettes.Dark;
    private static Attribute _normal;
    private static Scheme _baseScheme = null!;
    private static Scheme _input = null!;
    private static Scheme _borderIdle = null!;
    private static Scheme _borderFocused = null!;

    static TuiTheme() => Select(null);

    public static void Select(string? themeName)
    {
        _palette = TuiPalettes.Resolve(themeName);
        _normal = new Attribute(_palette.Text, _palette.Background);
        _baseScheme = UniformScheme(_normal) with
        {
            Disabled = new Attribute(_palette.Faint, _palette.Background),
        };
        _input = new Scheme
        {
            Normal = new Attribute(_palette.Text, _palette.SelectionBg),
            Focus = new Attribute(_palette.Text, _palette.Border),
            HotNormal = new Attribute(_palette.Text, _palette.SelectionBg),
            HotFocus = new Attribute(_palette.Text, _palette.Border),
            Active = new Attribute(_palette.Text, _palette.Border),
            HotActive = new Attribute(_palette.Text, _palette.Border),
            Highlight = new Attribute(_palette.Text, _palette.Border),
            Editable = new Attribute(_palette.Text, _palette.SelectionBg),
            ReadOnly = new Attribute(_palette.Dim, _palette.SelectionBg),
            Disabled = new Attribute(_palette.Faint, _palette.Background),
        };
        _borderIdle = UniformScheme(new Attribute(_palette.Border, _palette.Background));
        _borderFocused = UniformScheme(new Attribute(_palette.Accent, _palette.Background));
    }

    public static Color Accent => _palette.Accent;
    public static Color Background => _palette.Background;
    public static Color Text => _palette.Text;
    public static Color Dim => _palette.Dim;
    public static Color Faint => _palette.Faint;
    public static Color Border => _palette.Border;
    public static Color SelectionBg => _palette.SelectionBg;
    public static Color Success => _palette.Success;
    public static Color Error => _palette.Error;
    public static Color Warning => _palette.Warning;

    public static Attribute Normal => _normal;

    /// <summary>Attribute for one styled-text role, over the standard background.</summary>
    public static Attribute For(TuiRole role) => role switch
    {
        TuiRole.Dim => new Attribute(Dim, Background),
        TuiRole.Faint => new Attribute(Faint, Background),
        TuiRole.Accent => new Attribute(Accent, Background),
        TuiRole.Success => new Attribute(Success, Background),
        TuiRole.Error => new Attribute(Error, Background),
        TuiRole.Warning => new Attribute(Warning, Background),
        TuiRole.Heading => new Attribute(Text, Background, TextStyle.Bold),
        _ => Normal,
    };

    /// <summary>Scheme every plain view starts from: theme text on the app background,
    /// with no surprise role colours (the stock v2 scheme renders read-only text
    /// grey-on-grey — the bug this theme replaces).</summary>
    public static Scheme BaseScheme => _baseScheme;

    /// <summary>Editable field fill (step 7's plan-generate form): text over the theme's
    /// selection box so a field reads as fillable, lifting to the border colour while it
    /// owns focus (the same "active thing lifts" cue the panes use). Set explicitly
    /// because the stock v2 field scheme renders grey-on-grey — the bug
    /// <see cref="StyledView"/> exists to dodge.</summary>
    public static Scheme Input => _input;

    /// <summary>Pane border + title when the pane does not own focus: quiet chrome.</summary>
    public static Scheme BorderIdle => _borderIdle;

    /// <summary>Pane border + title when the pane owns focus: the accent — the same
    /// "the active thing gets the accent border" cue Claude Code's dialogs use.</summary>
    public static Scheme BorderFocused => _borderFocused;

    private static Scheme UniformScheme(Attribute all) => new()
    {
        Normal = all,
        Focus = all,
        HotNormal = all,
        HotFocus = all,
        Active = all,
        HotActive = all,
        Highlight = all,
        Editable = all,
        ReadOnly = all,
        Disabled = all,
    };
}
