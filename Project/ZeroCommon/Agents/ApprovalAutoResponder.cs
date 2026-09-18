using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>One keystroke of an approval answer, and how long to wait before the next one.</summary>
public readonly record struct ApprovalKeystroke(TerminalControl Control, int DelayMsAfter);

/// <summary>
/// Turns "the user picked option N" into the key sequence a TUI approval prompt expects
/// (M0041). Ported from the WPF host's <c>SendApprovalChoice</c>; kept here so both hosts
/// answer prompts identically and so the timing is testable without a terminal.
/// </summary>
/// <remarks>
/// The prompt always opens with option 0 selected, so answering it is "move down N times,
/// then Enter". The delays are the WPF values: 150 ms between arrows so the TUI redraws
/// between them, then 100 ms of settle before Enter.
/// </remarks>
public static class ApprovalAutoResponder
{
    public const int ArrowDelayMs = 150;
    public const int SettleDelayMs = 100;

    /// <summary>
    /// The keystrokes that select <paramref name="optionIndex"/> (0-based). Option 0 is
    /// already highlighted, so it is a bare Enter.
    /// </summary>
    public static IReadOnlyList<ApprovalKeystroke> Keystrokes(int optionIndex)
    {
        if (optionIndex < 0) optionIndex = 0;

        var strokes = new List<ApprovalKeystroke>(optionIndex + 1);
        for (var i = 0; i < optionIndex; i++)
            strokes.Add(new ApprovalKeystroke(TerminalControl.DownArrow, ArrowDelayMs));
        strokes.Add(new ApprovalKeystroke(TerminalControl.Enter, 0));
        return strokes;
    }

    /// <summary>
    /// Sends the answer to <paramref name="session"/>. <paramref name="delay"/> is injected so
    /// tests run without real time; <paramref name="stillWanted"/> is re-checked immediately
    /// before the first keystroke because the user may have switched auto-approve off while
    /// the delay was running (the WPF handler does the same).
    /// </summary>
    public static async Task SendAsync(
        ITerminalSession session,
        int optionIndex,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<bool>? stillWanted = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(delay);

        if (stillWanted is not null && !stillWanted()) return;

        var strokes = Keystrokes(optionIndex);
        for (var i = 0; i < strokes.Count; i++)
        {
            // The settle beat before Enter, matching the WPF sequence.
            if (i == strokes.Count - 1 && i > 0)
                await delay(TimeSpan.FromMilliseconds(SettleDelayMs), ct).ConfigureAwait(false);

            session.SendControl(strokes[i].Control);

            if (strokes[i].DelayMsAfter > 0)
                await delay(TimeSpan.FromMilliseconds(strokes[i].DelayMsAfter), ct).ConfigureAwait(false);
        }
    }
}
