using System.Diagnostics;

namespace Agent.Common.Services;

/// <summary>
/// When to press Enter after typing text into a terminal program: once the program has
/// taken the text in, not a fixed moment later.
///
/// <para>Measured (2026-11-24, Codex 0.166 in a tab): AgentBot wrote a 1,394-character
/// first-contact message and sent CR 50 ms later. A pseudo-console hands the text over as key
/// events, and Codex — which reads a fast run of characters as a paste — draws it only when
/// the run ends: 278 characters appeared 346 ms after the write, 1,239 after 999 ms, 3,079
/// after 3,202 ms (about 1 ms a character). An Enter inside that run is a line break in the
/// composer, not a submit. The message sat in the input box — it was still there half an hour
/// later — no turn started, and the bot polled an idle terminal until it gave up. Hermes, in
/// the next tab, drew 1,231 characters after 403 ms and answered the same kind of write.</para>
///
/// <para>So the Enter waits for three things: a floor that grows with the text
/// (<see cref="FloorMs"/>), the echo — for paste-sized text something must have been drawn
/// since the write — and <see cref="QuietMs"/> with nothing more drawn. "Drawn" is what
/// reaches the screen: Codex sends an empty synchronized-output frame
/// (<c>ESC[?2026h ESC[?2026l</c>) about every 100 ms while text is in its composer, so raw
/// output length never stands still (the first version of this waited out its cap every
/// time).</para>
///
/// <para>A program that echoes nothing (a password prompt) gets its Enter after the quiet
/// period; one that never goes quiet (a spinner while the agent is busy) at the cap.</para>
/// </summary>
public static class TerminalSubmit
{
    /// <summary>How long nothing may be drawn before the text counts as taken in.</summary>
    public const int QuietMs = 250;

    /// <summary>From this length on the text is paste-sized and its echo must be seen first.</summary>
    public const int EchoRequiredFrom = 200;

    private const int PollMs = 25;

    /// <summary>Never sooner than this: 1.2 ms a character on top of a base, at most 5 s.</summary>
    public static int FloorMs(int textLength) => Math.Min(5000, 120 + Math.Max(0, textLength) * 6 / 5);

    /// <summary>The longest wait: past it the Enter goes out whatever the screen is doing.</summary>
    public static int MaxWaitMs(int textLength)
        => FloorMs(textLength) + 2000 + Math.Min(4000, Math.Max(0, textLength) * 3 / 2);

    /// <summary>What <see cref="EnterWhenSettledAsync"/> did: how long it waited and why it stopped.</summary>
    public readonly record struct Outcome(int WaitedMs, bool Settled, bool SawEcho);

    /// <summary>Waits until the text has been taken in (or the cap), then calls <paramref name="enter"/>.</summary>
    /// <param name="outputLength">The length of the terminal's output so far.</param>
    /// <param name="readOutput">(start, length) of that output; null counts every byte as drawn.</param>
    public static async Task<Outcome> EnterWhenSettledAsync(
        Func<int> outputLength, Action enter, int textLength,
        Func<int, int, string>? readOutput = null,
        Func<int, Task>? delay = null, Func<long>? clockMs = null)
    {
        delay ??= ms => Task.Delay(ms);
        var sw = Stopwatch.StartNew();
        clockMs ??= () => sw.ElapsedMilliseconds;

        var floor = FloorMs(textLength);
        var cap = MaxWaitMs(textLength);
        var needEcho = textLength >= EchoRequiredFrom;
        var last = outputLength();
        var lastDraw = clockMs();
        var sawEcho = false;
        var settled = false;
        while (true)
        {
            await delay(PollMs).ConfigureAwait(false);
            var now = clockMs();
            var length = outputLength();
            if (length != last)
            {
                var drew = readOutput is null || length < last || DrawsSomething(readOutput(last, length - last));
                last = length;
                if (drew)
                {
                    lastDraw = now;
                    sawEcho = true;
                }
            }
            if (now >= floor && now - lastDraw >= QuietMs && (sawEcho || !needEcho)) { settled = true; break; }
            if (now >= cap) break;
        }
        var waited = (int)clockMs();
        enter();
        return new Outcome(waited, settled, sawEcho);
    }

    /// <summary>
    /// Whether a piece of terminal output puts anything on the screen: a character that is not
    /// part of an escape sequence, not a control character and not blank.
    /// </summary>
    public static bool DrawsSomething(string output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];
            if (c == '\x1b')
            {
                if (i + 1 >= output.Length) return false;
                var kind = output[++i];
                if (kind == '[')
                {
                    // CSI: parameters and intermediates, then one final byte @..~
                    while (++i < output.Length && output[i] is < '@' or > '~') { }
                }
                else if (kind is ']' or 'P' or '_' or '^')
                {
                    // OSC / DCS / APC / PM: up to BEL or ESC \
                    while (++i < output.Length)
                    {
                        if (output[i] == '\a') break;
                        if (output[i] == '\x1b' && i + 1 < output.Length && output[i + 1] == '\\') { i++; break; }
                    }
                }
                // any other ESC x is a two-character sequence, already consumed
                continue;
            }
            if (c < ' ' || c == '\x7f' || char.IsWhiteSpace(c)) continue;
            return true;
        }
        return false;
    }
}
