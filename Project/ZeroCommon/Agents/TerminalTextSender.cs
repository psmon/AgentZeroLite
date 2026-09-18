using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>
/// How the AgentBot puts a chat message into a terminal (M0041). Ported from the WPF host so
/// both hosts chunk the same way: short text goes through the synchronous write, long text
/// through the async write queue that applies backpressure, and multi-line text gets one
/// extra Enter because some shells treat pasted newlines as separators and leave the last
/// line sitting unexecuted.
/// </summary>
public static class TerminalTextSender
{
    /// <summary>Above this length the async write queue is used instead of a direct write.</summary>
    public const int ChunkThreshold = 200;

    /// <summary>Settle time before the extra Enter that executes a multi-line paste.</summary>
    public const int MultiLineEnterDelayMs = 100;

    /// <summary>True when <paramref name="text"/> is large enough to need the async queue.</summary>
    public static bool NeedsChunking(string text) => text.Length > ChunkThreshold;

    /// <summary>True when the text will need the trailing Enter.</summary>
    public static bool IsMultiLine(string text) => text.Contains('\n');

    /// <summary>
    /// Writes <paramref name="text"/> and executes it. <paramref name="delay"/> is injected so
    /// tests do not wait on real time.
    /// </summary>
    public static async Task SendAsync(
        ITerminalSession session,
        string text,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(text)) return;

        delay ??= (t, c) => Task.Delay(t, c);
        var multiLine = IsMultiLine(text);

        if (NeedsChunking(text))
        {
            // WriteAsync already appends \r through the write loop.
            await session.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
            if (multiLine)
            {
                await delay(TimeSpan.FromMilliseconds(MultiLineEnterDelayMs), ct).ConfigureAwait(false);
                session.SendControl(TerminalControl.Enter);
            }
            return;
        }

        session.WriteAndEnter(text);
        if (multiLine) session.SendControl(TerminalControl.Enter);
    }
}
