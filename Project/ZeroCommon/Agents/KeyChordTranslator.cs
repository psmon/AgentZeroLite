using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>
/// KEY-mode chord rules (M0041). Ported from the WPF host so both hosts relay the same
/// bytes: Ctrl+letter becomes the matching C0 control character, and Escape is a two-step
/// sequence rather than a single byte.
/// </summary>
public static class KeyChordTranslator
{
    /// <summary>Gap between the Escape and the interrupt that follows it.</summary>
    public const int EscapeFollowUpDelayMs = 300;

    /// <summary>
    /// Ctrl+A..Ctrl+Z as control characters 0x01..0x1A. <paramref name="letter"/> is
    /// case-insensitive; anything that is not an ASCII letter returns null.
    /// </summary>
    public static char? ControlChar(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        if (upper < 'A' || upper > 'Z') return null;
        return (char)(upper - 'A' + 1);
    }

    /// <summary>
    /// Escape as the bot sends it: ESC, a beat, then Ctrl+C. A bare ESC leaves some TUIs
    /// (Claude Code among them) in a half-dismissed state, so the WPF host follows it with
    /// an interrupt and this keeps the two hosts identical.
    /// </summary>
    public static async Task SendEscapeSequenceAsync(
        ITerminalSession session,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        delay ??= (t, c) => Task.Delay(t, c);

        session.SendControl(TerminalControl.Escape);
        await delay(TimeSpan.FromMilliseconds(EscapeFollowUpDelayMs), ct).ConfigureAwait(false);
        session.SendControl(TerminalControl.Interrupt);
    }
}
