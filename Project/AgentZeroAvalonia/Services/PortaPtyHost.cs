using System.Text;
using Agent.Common.Services;
using Porta.Pty;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// The macOS / Linux PTY backend (M0035): Porta.Pty's <c>forkpty</c> shim behind
/// <see cref="IPtyHost"/>. Shape mirrors <see cref="ConPtyHost"/> — a blocking read
/// thread with a stateful UTF-8 decoder, serialised writes, one <see cref="Exited"/>
/// no matter which of the two exit signals (stream EOF, <c>ProcessExited</c>) lands
/// first — so <c>XtermTerminalSession</c> sees the same host on every OS.
///
/// Windows never uses this class: the ConPTY copy is the proven backend there, and
/// Porta's Windows path would pull in <c>conpty.dll</c> / <c>OpenConsole.exe</c>.
/// </summary>
internal sealed class PortaPtyHost : IPtyHost
{
    public event Action<string>? Output;
    public event Action? Exited;

    private readonly object _writeSync = new();
    private IPtyConnection? _conn;
    private Thread? _readThread;
    private volatile bool _running;
    private int _exitedRaised;
    private bool _disposed;

    public bool IsRunning => _running;
    public string Diagnostics { get; private set; } = "";

    public void Start(TerminalLaunchSpec spec, int cols, int rows)
    {
        if (_conn is not null) throw new InvalidOperationException("PTY host already started.");
        if (_disposed) throw new ObjectDisposedException(nameof(PortaPtyHost));

        var options = new PtyOptions
        {
            Name = spec.DisplayName,
            Cols = Math.Max(1, cols),
            Rows = Math.Max(1, rows),
            Cwd = spec.Cwd,
            App = spec.App,
            CommandLine = spec.Args.ToArray(),
            Environment = new Dictionary<string, string>(spec.Env),
        };

        // SpawnAsync is fork/exec behind an async signature; nothing here needs the UI
        // thread, and the callers (the control, the self-test) are synchronous.
        _conn = PtyProvider.SpawnAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        Diagnostics = $"Porta.Pty spawn pid={_conn.Pid} app={spec.App} args={spec.Args.Count} size={options.Cols}x{options.Rows}";

        _conn.ProcessExited += (_, e) =>
        {
            Diagnostics += $" | exited code={e.ExitCode}";
            _running = false;
            RaiseExited();
        };

        _running = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "PortaPtyReadLoop" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var conn = _conn!;
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        try
        {
            while (true)
            {
                int n;
                try { n = conn.ReaderStream.Read(buffer, 0, buffer.Length); }
                catch { break; }
                if (n <= 0) break;

                int charCount = decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
                if (charCount > 0)
                {
                    var text = new string(chars, 0, charCount);
                    try { Output?.Invoke(text); } catch { }
                }
            }
        }
        finally
        {
            _running = false;
            RaiseExited();
        }
    }

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0) return;
        try { Exited?.Invoke(); } catch { }
    }

    public void Write(ReadOnlySpan<char> text)
    {
        if (!_running || text.Length == 0) return;
        var conn = _conn;
        if (conn is null) return;
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        lock (_writeSync)
        {
            try
            {
                conn.WriterStream.Write(bytes, 0, bytes.Length);
                conn.WriterStream.Flush();
            }
            catch
            {
                // The health tracker reports the wedge; nothing to do here.
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        var conn = _conn;
        if (conn is null || !_running) return;
        try { conn.Resize(Math.Max(1, cols), Math.Max(1, rows)); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        var conn = _conn;
        _conn = null;
        if (conn is not null)
        {
            try { conn.Kill(); } catch { }
            try { conn.Dispose(); } catch { }
        }
        try { _readThread?.Join(TimeSpan.FromMilliseconds(500)); } catch { }
    }
}
