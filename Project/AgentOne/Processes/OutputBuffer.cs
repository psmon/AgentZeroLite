using System.Text;

namespace AgentOne.Processes;

/// <summary>
/// A process's output, bounded: the first <c>headCap</c> characters are kept
/// (a build's first error is usually what matters), and past that only the
/// last <c>tailCap</c> (a server's newest lines are what matter). A dev server
/// left running for an hour cannot grow it without bound.
/// </summary>
internal sealed class OutputBuffer(int headCap, int tailCap)
{
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private long _total;

    public long Total => _total;

    public void Append(string text)
    {
        if (text.Length == 0) return;
        _total += text.Length;

        if (_head.Length < headCap)
        {
            var take = Math.Min(headCap - _head.Length, text.Length);
            _head.Append(text, 0, take);
            text = text[take..];
        }

        if (text.Length == 0) return;
        _tail.Append(text);
        if (_tail.Length > tailCap * 2) _tail.Remove(0, _tail.Length - tailCap);
    }

    public string Text()
    {
        if (_tail.Length == 0) return _head.ToString();
        var tail = _tail.Length > tailCap ? _tail.ToString(_tail.Length - tailCap, tailCap) : _tail.ToString();
        var skipped = _total - _head.Length - tail.Length;
        return _head + (skipped > 0 ? $"\n… {skipped} chars not kept …\n" : "") + tail;
    }

    /// <summary>The last <paramref name="chars"/> characters seen.</summary>
    public string Tail(int chars)
    {
        var all = _tail.Length >= chars ? _tail.ToString() : _head.ToString() + _tail;
        return all.Length <= chars ? all : all[^chars..];
    }
}
