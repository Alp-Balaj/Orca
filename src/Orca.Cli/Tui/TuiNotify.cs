namespace Orca.Cli.Tui;

/// <summary>SUG-14 §2: the gate-reached ping. An Implementer runs 10–20 minutes and the
/// human WILL alt-tab away — when a decision moment opens, emit BEL plus an OSC 9
/// desktop notification (mako and friends pick it up from foot/alacritty/kitty; other
/// terminals ignore it harmlessly). Written to stderr so Terminal.Gui's stdout buffer is
/// never touched; best-effort by design — a notification must never break a gate.</summary>
internal static class TuiNotify
{
    public static string Compose(string message) => $"\a\x1b]9;orca: {message}\x1b\\";

    public static void GateReached(string message)
    {
        try
        {
            Console.Error.Write(Compose(message));
            Console.Error.Flush();
        }
        catch (IOException)
        {
            // No terminal to ping (redirected stderr) — the modal itself is the signal.
        }
    }
}
