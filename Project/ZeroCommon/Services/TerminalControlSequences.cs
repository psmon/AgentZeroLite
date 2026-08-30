namespace Agent.Common.Services;

/// <summary>
/// Canonical VT/ANSI byte sequences for each <see cref="TerminalControl"/>.
/// Both terminal backends (ConPTY and WebView/xterm.js) send exactly these,
/// so control-key behavior stays byte-identical regardless of renderer.
/// Kept as a pure function so it is trivially unit-testable and cannot drift
/// between backends.
/// </summary>
public static class TerminalControlSequences
{
    /// <summary>The VT sequence for a control, or "" if unmapped.</summary>
    public static string ToSequence(TerminalControl control) => control switch
    {
        TerminalControl.Interrupt => "\x03",
        TerminalControl.Escape => "\x1b",
        TerminalControl.Enter => "\r",
        TerminalControl.Tab => "\t",
        // ESC[Z is the standard xterm-style reverse-tab sequence — what VT220+
        // terminals emit for Shift+Tab. Claude Code uses it to cycle modes;
        // readline binds it to menu-complete-backward.
        TerminalControl.BackTab => "\x1b[Z",
        TerminalControl.Backspace => "\x7f",
        TerminalControl.Space => " ",
        TerminalControl.Delete => "\x1b[3~",
        TerminalControl.Home => "\x1b[H",
        TerminalControl.End => "\x1b[F",
        TerminalControl.PageUp => "\x1b[5~",
        TerminalControl.PageDown => "\x1b[6~",
        TerminalControl.DownArrow => "\x1b[B",
        TerminalControl.UpArrow => "\x1b[A",
        TerminalControl.LeftArrow => "\x1b[D",
        TerminalControl.RightArrow => "\x1b[C",
        TerminalControl.ClearScreen => "\x1b[2J\x1b[H",
        _ => "",
    };
}
