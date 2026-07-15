using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Orca.Cli.Tui;

/// <summary>The TUI's single source of colour truth — a Claude Code-flavoured palette
/// (warm coral accent, warm greys for chrome, muted verdict colours) applied over the
/// terminal-default dark background. Every widget takes its <see cref="Attribute"/>s from
/// here so the look can be tuned in one place; nothing else in the TUI names a colour.</summary>
internal static class TuiTheme
{
    // The Claude brand coral, and a warm neutral ramp to match it. Truecolor; terminals
    // without 24-bit support get Terminal.Gui's nearest-colour mapping.
    public static readonly Color Accent = new(0xD9, 0x77, 0x57);
    public static readonly Color Background = new(0x1A, 0x18, 0x16);
    public static readonly Color Text = new(0xE8, 0xE6, 0xE3);
    public static readonly Color Dim = new(0x9A, 0x94, 0x8D);
    public static readonly Color Faint = new(0x6E, 0x68, 0x62);
    public static readonly Color Border = new(0x4A, 0x45, 0x40);
    public static readonly Color SelectionBg = new(0x3E, 0x2E, 0x26);
    public static readonly Color Success = new(0x5F, 0xAF, 0x6E);
    public static readonly Color Error = new(0xDE, 0x66, 0x5E);
    public static readonly Color Warning = new(0xD0, 0xA5, 0x4A);

    public static Attribute Normal { get; } = new(Text, Background);

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

    /// <summary>Scheme every plain view starts from: soft warm white on the app background,
    /// with no surprise role colours (the stock v2 scheme renders read-only text
    /// grey-on-grey — the bug this theme replaces).</summary>
    public static Scheme BaseScheme { get; } = new()
    {
        Normal = Normal,
        Focus = Normal,
        HotNormal = Normal,
        HotFocus = Normal,
        Active = Normal,
        HotActive = Normal,
        Highlight = Normal,
        Editable = Normal,
        ReadOnly = Normal,
        Disabled = new Attribute(Faint, Background),
    };

    /// <summary>Editable field fill (step 7's plan-generate form): text over a warm dark
    /// box so a field reads as fillable rather than blending into the page, lifting to the
    /// grey border colour while it owns focus (the same "active thing lifts" cue the panes
    /// use). Set explicitly because the stock v2 field scheme renders grey-on-grey — the
    /// bug <see cref="StyledView"/> exists to dodge.</summary>
    public static Scheme Input { get; } = new()
    {
        Normal = new Attribute(Text, SelectionBg),
        Focus = new Attribute(Text, Border),
        HotNormal = new Attribute(Text, SelectionBg),
        HotFocus = new Attribute(Text, Border),
        Active = new Attribute(Text, Border),
        HotActive = new Attribute(Text, Border),
        Highlight = new Attribute(Text, Border),
        Editable = new Attribute(Text, SelectionBg),
        ReadOnly = new Attribute(Dim, SelectionBg),
        Disabled = new Attribute(Faint, Background),
    };

    /// <summary>Pane border + title when the pane does not own focus: quiet warm grey.</summary>
    public static Scheme BorderIdle { get; } = DimmedScheme(new Attribute(Border, Background));

    /// <summary>Pane border + title when the pane owns focus: the coral accent — the same
    /// "the active thing gets the accent border" cue Claude Code's dialogs use.</summary>
    public static Scheme BorderFocused { get; } = DimmedScheme(new Attribute(Accent, Background));

    private static Scheme DimmedScheme(Attribute all) => new()
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
