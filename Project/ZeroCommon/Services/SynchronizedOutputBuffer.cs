using System.Text;

namespace Agent.Common.Services;

/// <summary>
/// Synchronized output (DEC private mode 2026), done in the host because the
/// renderer does not do it.
///
/// <para>Ink/React TUIs — Claude Code, Codex — repaint by clearing the screen,
/// homing the cursor and drawing the whole frame again, many times a second. They
/// wrap that in <c>ESC[?2026h</c> … <c>ESC[?2026l</c> to say "do not show anyone
/// the middle of this". A terminal that honours it paints once, at the end. One
/// that ignores it paints every step, and the cursor is visibly somewhere new each
/// time — which is what a cursor skittering around the screen actually is.</para>
///
/// <para>xterm.js has no support for mode 2026 (checked against 5.5.0: the string
/// does not appear in the bundle), and the native control this backend replaced
/// did. Since this sits between the pseudo-console and the renderer, it can hold
/// the frame back itself: buffer from the start marker, hand the whole thing over
/// as one write at the end marker, and the renderer draws once.</para>
///
/// <para>Two guards, because a terminal that stops painting is worse than one that
/// flickers: a byte cap for an application that opens a frame and floods, and
/// <see cref="Flush"/> for the caller's timeout when one opens a frame and never
/// closes it.</para>
/// </summary>
public sealed class SynchronizedOutputBuffer
{
    /// <summary>ESC [ ? 2 0 2 6 h</summary>
    private const string Begin = "\u001b[?2026h";
    /// <summary>ESC [ ? 2 0 2 6 l</summary>
    private const string End = "\u001b[?2026l";

    /// <summary>
    /// A frame larger than this is forwarded anyway. Well past a full repaint of a
    /// large screen, so only a runaway writer reaches it.
    /// </summary>
    public const int DefaultMaxBufferedBytes = 4 * 1024 * 1024;

    private readonly StringBuilder _held = new();
    private readonly int _maxBytes;
    private bool _inFrame;

    /// <summary>
    /// A tail that might be the start of a marker split across two reads. The
    /// pseudo-console chunks wherever it likes, so "ESC[?20" can arrive in one read
    /// and "26h" in the next; matching per chunk would miss every such frame and
    /// this would quietly do nothing.
    /// </summary>
    private string _carry = "";

    public SynchronizedOutputBuffer(int maxBufferedBytes = DefaultMaxBufferedBytes)
        => _maxBytes = maxBufferedBytes > 0 ? maxBufferedBytes : DefaultMaxBufferedBytes;

    /// <summary>True while a frame is open and output is being held back.</summary>
    public bool IsBuffering => _inFrame;

    /// <summary>How much is currently held.</summary>
    public int PendingLength => _held.Length;

    /// <summary>How many frames have been coalesced — a cheap way to confirm the
    /// application really is using synchronized output.</summary>
    public long FramesCoalesced { get; private set; }

    /// <summary>
    /// Feed one chunk from the pseudo-console.
    /// </summary>
    /// <returns>
    /// What the renderer should be given now, or an empty string while a frame is
    /// still open. Markers are passed through: a renderer that ignores them is
    /// unaffected, and one that later learns them will behave correctly.
    /// </returns>
    public string Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return "";

        if (_carry.Length > 0)
        {
            chunk = _carry + chunk;
            _carry = "";
        }

        var output = new StringBuilder();
        var i = 0;

        while (i < chunk.Length)
        {
            if (!_inFrame)
            {
                var begin = chunk.IndexOf(Begin, i, StringComparison.Ordinal);
                if (begin < 0)
                {
                    // Hold back a tail that could be the front of a split marker.
                    var tail = PartialMarkerLength(chunk, i, Begin);
                    output.Append(chunk, i, chunk.Length - i - tail);
                    if (tail > 0) _carry = chunk[^tail..];
                    break;
                }
                // Everything before the frame starts goes out now.
                output.Append(chunk, i, begin - i);
                _inFrame = true;
                _held.Append(Begin);
                i = begin + Begin.Length;
                continue;
            }

            var end = chunk.IndexOf(End, i, StringComparison.Ordinal);
            if (end < 0)
            {
                var tail = PartialMarkerLength(chunk, i, End);
                _held.Append(chunk, i, chunk.Length - i - tail);
                if (tail > 0) _carry = chunk[^tail..];
                if (_held.Length > _maxBytes)
                {
                    // The frame is not going to close in any reasonable size. Show what
                    // there is rather than let the terminal go quiet.
                    output.Append(_held);
                    _held.Clear();
                    _inFrame = false;
                }
                break;
            }

            _held.Append(chunk, i, end - i).Append(End);
            output.Append(_held);
            _held.Clear();
            _inFrame = false;
            FramesCoalesced++;
            i = end + End.Length;
        }

        return output.ToString();
    }

    /// <summary>
    /// How many characters at the tail of <paramref name="text"/> are a proper
    /// prefix of <paramref name="marker"/> — i.e. could still become one.
    /// </summary>
    private static int PartialMarkerLength(string text, int from, string marker)
    {
        var max = Math.Min(marker.Length - 1, text.Length - from);
        for (var len = max; len > 0; len--)
        {
            if (string.CompareOrdinal(text, text.Length - len, marker, 0, len) == 0)
                return len;
        }
        return 0;
    }

    /// <summary>
    /// Release whatever is held without waiting for the end marker — for the
    /// caller's timeout. An application that opens a frame and stops writing must
    /// not leave the screen frozen.
    /// </summary>
    public string Flush()
    {
        var carried = _carry;
        _carry = "";
        if (_held.Length == 0)
        {
            _inFrame = false;
            return carried;
        }
        var text = _held.ToString() + carried;
        _held.Clear();
        _inFrame = false;
        return text;
    }
}
