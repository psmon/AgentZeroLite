using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Agent.Common;

namespace Agent.Common.Services;

/// <summary>
/// One hyperlink found in (ANSI-stripped) terminal text.
/// <para><see cref="Start"/>/<see cref="End"/> are offsets into the scanned
/// text (End is exclusive and may span joined wrap lines). <see cref="MayContinue"/>
/// is true when the URL reaches the end of the scanned text — or ends a
/// full-width line whose continuation has not arrived yet — so a streaming
/// consumer should wait for more output before trusting it.</para>
/// </summary>
public readonly record struct TerminalLink(string Url, int Start, int End, bool MayContinue);

/// <summary>
/// Pure URL extraction for terminal output. The single hard problem it solves
/// is <b>soft-wrapped URLs</b>: OAuth / device-login links (claude, gh, az,
/// gcloud …) are far wider than a terminal row, and both ConPTY and TUI
/// frameworks (Ink) hard-break them at the column boundary, so a naive regex
/// only ever sees the first row. When a URL runs exactly to the end of a
/// full-width row and the next row starts with URL characters, the rows are
/// joined back into one link.
/// <para>Headless + stateless — <see cref="TerminalLinkDetector"/> adds the
/// streaming/dedupe layer on top, and <see cref="AgentEventStream"/> reuses it
/// for the bot window's URL bubbles.</para>
/// </summary>
public static class TerminalLinkScanner
{
    /// <summary>Row width assumed when the caller does not know the terminal's
    /// column count. A URL that ends a line at least this long is treated as
    /// "possibly wrapped".</summary>
    public const int DefaultWrapWidth = 60;

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>\]\)]+|(?<![\w.])localhost:\d{1,5}[^\s""'<>\]\)]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', ')', '…'];

    /// <summary>
    /// Extract every URL from <paramref name="text"/> (ANSI already stripped;
    /// CR is ignored). <paramref name="columns"/> is the terminal width when
    /// known — it sharpens the wrap heuristic. With <paramref name="final"/> =
    /// false, links that may still be growing are flagged
    /// <see cref="TerminalLink.MayContinue"/> instead of being cut short.
    /// </summary>
    public static IReadOnlyList<TerminalLink> Scan(string text, int? columns = null, bool final = true)
    {
        var links = new List<TerminalLink>();
        if (string.IsNullOrEmpty(text)) return links;

        var wrapWidth = columns is > 0 ? Math.Max(20, columns.Value - 1) : DefaultWrapWidth;
        var pos = 0;
        while (pos < text.Length)
        {
            var m = UrlRegex.Match(text, pos);
            if (!m.Success) break;

            var sb = new StringBuilder(m.Value);
            var end = m.Index + m.Length;
            var mayContinue = false;

            // Wrap-join loop: absorb continuation rows while the URL keeps
            // ending exactly at a full-width row boundary.
            while (true)
            {
                if (end >= text.Length) { mayContinue = true; break; }
                if (text[end] == '\r') { end++; continue; }
                if (text[end] != '\n') break;                       // ended by a delimiter → complete

                var lineStart = text.LastIndexOf('\n', end - 1) + 1;
                var lineLen = end - lineStart;
                if (lineLen < wrapWidth) break;                     // short row → URL really ended here

                var next = end + 1;
                if (next >= text.Length) { mayContinue = true; break; } // continuation row not here yet
                if (!IsUrlChar(text[next])) break;
                if (StartsWithScheme(text, next)) break;            // a *new* URL, not a continuation

                var runEnd = next;
                while (runEnd < text.Length && IsUrlChar(text[runEnd])) runEnd++;
                sb.Append(text, next, runEnd - next);
                end = runEnd;
            }

            // A TUI that elides long URLs ("https://…/path...") prints something
            // that isn't a real link — decide that BEFORE trimming punctuation.
            var raw = sb.ToString();
            var truncated = raw.EndsWith("...", StringComparison.Ordinal) || raw.EndsWith('…');

            var url = raw.TrimEnd(TrailingPunctuation);
            if (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            if (!truncated && url.Length > 8)
                links.Add(new TerminalLink(url, m.Index, end, mayContinue && !final));

            pos = Math.Max(end, m.Index + 1);
        }
        return links;
    }

    /// <summary>True for http/https absolute URLs only — the only kind we hand
    /// to the OS shell. Rejects file:, javascript:, custom schemes.</summary>
    public static bool IsOpenableWebUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(u.Host);

    private static bool IsUrlChar(char c)
        => c > ' ' && c < (char)0x7F && "\"'<>])".IndexOf(c) < 0;

    private static bool StartsWithScheme(string text, int at)
        => string.Compare(text, at, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0
           || string.Compare(text, at, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0;
}

/// <summary>
/// Streaming link watcher for one <see cref="ITerminalSession"/>: strips ANSI,
/// keeps a bounded tail buffer, runs <see cref="TerminalLinkScanner"/> on each
/// output frame, defers links that may still be wrapping, flushes them after a
/// short idle gap, and dedupes repeats (TUIs redraw the same screen many
/// times). Raises <see cref="LinkDetected"/> on the session's output thread —
/// UI subscribers must marshal.
/// </summary>
public sealed class TerminalLinkDetector : IDisposable
{
    private const int BufferMax = 8000;
    private const int IdleFlushMs = 600;

    private readonly ITerminalSession _session;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _idle;
    private string _buffer = "";
    private int _offset;
    private bool _disposed;

    /// <summary>Terminal width, when the UI knows it. Improves wrap joining.</summary>
    public int? Columns { get; set; }

    /// <summary>The same URL is reported at most once per this window.</summary>
    public TimeSpan RepeatCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A complete http(s) URL was seen in the terminal output.</summary>
    public event Action<string>? LinkDetected;

    public TerminalLinkDetector(ITerminalSession session, int? columns = null)
    {
        _session = session;
        Columns = columns;
        _idle = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _session.OutputReceived += OnOutput;
    }

    /// <summary>Forget buffered text and the dedupe history (session swap).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _buffer = "";
            _offset = 0;
            _seen.Clear();
        }
    }

    /// <summary>Emit links that were held back waiting for a continuation row.
    /// Called automatically after <see cref="IdleFlushMs"/> of output silence.</summary>
    public void Flush()
    {
        if (_disposed) return;
        List<string> found;
        lock (_sync) found = ScanLocked(final: true);
        Emit(found);
    }

    private void OnOutput(TerminalOutputFrame frame)
    {
        if (_disposed || string.IsNullOrEmpty(frame.Text)) return;

        var clean = ApprovalParser.StripAnsiCodes(frame.Text).Replace("\r", "");
        if (clean.Length == 0) return;

        List<string> found;
        lock (_sync)
        {
            _buffer += clean;
            if (_buffer.Length > BufferMax)
            {
                var cut = _buffer.Length - BufferMax;
                _buffer = _buffer[cut..];
                _offset = Math.Max(0, _offset - cut);
            }
            found = ScanLocked(final: false);
        }
        Emit(found);

        try { _idle.Change(IdleFlushMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    // Scans the unconsumed region. Advances _offset past complete links but
    // never past (a) a link that may still be wrapping or (b) the trailing
    // partial token, so the next frame can complete them.
    private List<string> ScanLocked(bool final)
    {
        var result = new List<string>();
        if (_offset >= _buffer.Length) return result;

        var region = _buffer[_offset..];
        var links = TerminalLinkScanner.Scan(region, Columns, final);

        var lastWs = region.LastIndexOfAny([' ', '\n', '\t']);
        var newOffset = lastWs >= 0 ? _offset + lastWs + 1 : _offset;

        foreach (var link in links)
        {
            if (link.MayContinue)
            {
                newOffset = Math.Min(newOffset, _offset + link.Start);
                break;
            }
            result.Add(link.Url);
            newOffset = Math.Max(newOffset, _offset + link.End);
        }

        // On a final flush a URL sitting at the very end is emitted but NOT
        // consumed — if the next frame extends it, the longer form is reported
        // (dedupe is exact-string, so the UI simply replaces it).
        if (final && links.Count > 0 && links[^1].End >= region.Length)
            newOffset = Math.Min(newOffset, _offset + links[^1].Start);

        _offset = Math.Clamp(newOffset, 0, _buffer.Length);
        return result;
    }

    private void Emit(List<string> urls)
    {
        if (urls.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var url in urls)
        {
            if (!TerminalLinkScanner.IsOpenableWebUrl(url)) continue;

            lock (_sync)
            {
                if (_seen.TryGetValue(url, out var last) && now - last < RepeatCooldown) continue;
                _seen[url] = now;
                if (_seen.Count > 200)
                {
                    var cutoff = now - RepeatCooldown;
                    foreach (var k in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                        _seen.Remove(k);
                }
            }

            try { LinkDetected?.Invoke(url); }
            catch (Exception ex)
            {
                AppLogger.Log($"[LinkDetector] subscriber threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.OutputReceived -= OnOutput;
        _idle.Dispose();
    }
}
