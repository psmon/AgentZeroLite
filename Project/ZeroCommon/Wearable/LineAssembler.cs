using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Common.Wearable;

/// <summary>
/// Reassembles newline-terminated UTF-8 text out of a stream of arbitrary byte chunks — the
/// wearable link's "R {json}" / "A {json}" / "S …" / "E …" lines as they arrive from BLE.
///
/// <para><b>Why this is not a StringBuilder.</b> The obvious version appends
/// <c>Encoding.UTF8.GetString(chunk)</c> per notification and splits the result on '\n'.
/// That decodes every chunk as if it were a complete UTF-8 document, so a character split
/// across two notifications is destroyed: the trailing bytes of the first and the leading
/// continuation bytes of the second each decode to U+FFFD, and the character is gone for
/// good. One mangled character is enough to stop the line parsing as JSON.</para>
///
/// <para>Where the split falls is decided by the negotiated MTU, which is <i>not</i> stable
/// across a link's lifetime — a watch that reconnects after rebooting can come back on the
/// 23-byte default instead of the 247 it had, which moves the boundary from "rarely" to
/// "several times per line". Hence: buffer bytes, cut on the newline byte, decode only whole
/// lines.</para>
///
/// <para>Lives in ZeroCommon rather than next to the BLE central so it can be tested
/// headlessly; it has no WinRT or Win32 dependency of any kind.</para>
/// </summary>
public sealed class LineAssembler
{
    private const byte NewLine = 0x0A;
    private const byte CarriageReturn = 0x0D;

    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private readonly List<byte> _buffer = new();
    private readonly int _maxBytes;

    /// <summary>
    /// True from the moment a line overran <c>maxBytes</c> until its newline arrives. The
    /// bytes already buffered and every byte still to come belong to the same oversized line,
    /// so keeping any of them would only glue garbage onto the front of the next good one.
    /// </summary>
    private bool _discarding;

    /// <param name="maxBytes">
    /// How much unterminated text to hold before giving up on it. A device that stops sending
    /// newlines would otherwise grow this without bound, and the bytes already buffered can
    /// only corrupt the next real line, so they are dropped rather than kept.
    /// </param>
    public LineAssembler(int maxBytes = 16384) => _maxBytes = maxBytes;

    /// <summary>How many times the buffer was discarded for overrunning <c>maxBytes</c>.</summary>
    public long Discarded { get; private set; }

    /// <summary>Bytes currently held back, waiting for their newline.</summary>
    public int Pending => _buffer.Count;

    /// <summary>
    /// Feeds one chunk in.
    /// </summary>
    /// <returns>
    /// The lines this chunk completed, in order, newline and any trailing CR stripped. Empty
    /// lines are not reported — neither protocol on this link carries one. The list is empty
    /// when the chunk ended mid-line, which is the common case. A line that overran
    /// <c>maxBytes</c> is never returned, in whole or in part.
    /// </returns>
    public IReadOnlyList<string> Append(ReadOnlySpan<byte> chunk)
    {
        List<string>? lines = null;
        foreach (var b in chunk)
        {
            if (b == NewLine)
            {
                if (_discarding) _discarding = false;   // the ruined line ends here
                else Cut(ref lines);
                continue;
            }
            if (_discarding) continue;

            _buffer.Add(b);
            if (_buffer.Count <= _maxBytes) continue;
            _buffer.Clear();
            _discarding = true;
            Discarded++;
        }
        return lines ?? None;
    }

    /// <summary>Drops anything half-received. Called when the link comes up, so a line
    /// truncated by the previous drop cannot prefix the first line of the new session.</summary>
    public void Reset()
    {
        _buffer.Clear();
        _discarding = false;
    }

    private void Cut(ref List<string>? lines)
    {
        var count = _buffer.Count;
        if (count > 0 && _buffer[count - 1] == CarriageReturn) count--;
        if (count > 0)
        {
            var text = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_buffer)[..count]);
            (lines ??= new List<string>()).Add(text);
        }
        _buffer.Clear();
    }
}
