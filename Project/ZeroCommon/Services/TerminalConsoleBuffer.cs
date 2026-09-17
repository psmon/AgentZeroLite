using System.Text;

namespace Agent.Common.Services;

/// <summary>
/// The accumulated console stream of one terminal session, plus the answer to
/// "what is on the screen right now" — the two things
/// <see cref="ITerminalSession"/> exposes about output, kept in one place so
/// both backends can mean the same thing by them.
///
/// <para><b>Why the distinction is load-bearing.</b> <c>OutputLength</c> /
/// <c>ReadOutput</c> address the <i>whole stream</i>: consumers page through it
/// to see what is new. <c>GetConsoleText</c> answers a different question — what
/// a person would see looking at the terminal — and every caller wants it for
/// that reason: the approval parser asks "is a prompt waiting", the agent-state
/// monitor asks "what is this agent doing", the bot asks "what should the model
/// see". Answering those with the full transcript re-matches prompts that were
/// dismissed ten minutes ago and grows the model's context without bound.</para>
///
/// <para><b>Who knows the screen.</b> The terminal emulator does, and for the
/// WebViewXterm backend that is xterm.js, in the renderer. So the renderer
/// pushes a viewport snapshot here (<see cref="SetScreenSnapshot"/>) and
/// <see cref="GetConsoleText"/> hands back the last one. Until the first
/// snapshot arrives there is no emulator to ask, so it falls back to the tail of
/// the stream — an approximation, deliberately bounded, never the whole log.</para>
/// </summary>
public sealed class TerminalConsoleBuffer
{
    /// <summary>
    /// How many trailing lines the fallback returns before the renderer has
    /// reported a screen. Comfortably more than a tall terminal so a prompt near
    /// the top of the viewport is still included, and far short of "everything".
    /// </summary>
    public const int FallbackLines = 200;

    private readonly object _sync = new();
    private readonly StringBuilder _log = new();
    private readonly int _fallbackLines;

    private string? _screen;

    public TerminalConsoleBuffer(int fallbackLines = FallbackLines)
        => _fallbackLines = fallbackLines > 0 ? fallbackLines : FallbackLines;

    /// <summary>Total characters of raw VT received so far.</summary>
    public int Length
    {
        get { lock (_sync) return _log.Length; }
    }

    /// <summary>True once the renderer has reported a viewport at least once.</summary>
    public bool HasScreenSnapshot
    {
        get { lock (_sync) return _screen != null; }
    }

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        lock (_sync) _log.Append(chunk);
    }

    /// <summary>A window of the raw stream, for consumers tracking what is new.</summary>
    public string Read(int start, int length)
    {
        lock (_sync)
        {
            if (length <= 0 || start < 0 || start >= _log.Length) return "";
            var safe = Math.Min(length, _log.Length - start);
            return safe > 0 ? _log.ToString(start, safe) : "";
        }
    }

    /// <summary>The whole raw stream. Only for consumers that genuinely want the
    /// transcript — <see cref="GetConsoleText"/> is what "the screen" means.</summary>
    public string FullText
    {
        get { lock (_sync) return _log.ToString(); }
    }

    /// <summary>
    /// The renderer's current viewport, newline-separated, trailing blank lines
    /// already trimmed by the sender. Null or empty clears it, so a session that
    /// loses its renderer falls back rather than serving a frozen screen.
    /// </summary>
    public void SetScreenSnapshot(string? screen)
    {
        lock (_sync) _screen = string.IsNullOrEmpty(screen) ? null : screen;
    }

    /// <summary>
    /// What a person would see on the terminal right now: the renderer's
    /// viewport when one has been reported, otherwise the last
    /// <see cref="FallbackLines"/> lines of the stream.
    /// </summary>
    public string GetConsoleText()
    {
        lock (_sync)
        {
            if (_screen != null) return _screen;
            return Tail(_log, _fallbackLines);
        }
    }

    /// <summary>Called under the lock.</summary>
    private static string Tail(StringBuilder log, int lines)
    {
        if (log.Length == 0) return "";

        // Walk back over newlines rather than materialising the whole log to
        // split it - this runs on the state monitor's poll loop, once per tab.
        var seen = 0;
        var cut = 0;
        for (var i = log.Length - 1; i >= 0; i--)
        {
            if (log[i] != '\n') continue;
            // A newline at the very end closes the last line; it does not start a new one.
            if (i == log.Length - 1) continue;
            if (++seen < lines) continue;
            cut = i + 1;
            break;
        }
        return cut == 0 ? log.ToString() : log.ToString(cut, log.Length - cut);
    }
}
