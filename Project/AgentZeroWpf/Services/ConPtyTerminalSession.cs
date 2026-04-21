using System.Threading;
using System.Threading.Channels;
using AgentZeroWpf;

namespace AgentZeroWpf.Services;

/// <summary>
/// Wraps EasyWindowsTerminalControl.TermPTY behind the ITerminalSession interface.
/// Bridges the existing ConPTY implementation to the new abstraction layer.
/// </summary>
public sealed class ConPtyTerminalSession : ITerminalSession, IDisposable
{
    private readonly EasyWindowsTerminalControl.TermPTY _pty;
    private readonly string _sessionId;
    private readonly string _internalId;

    // Output change detection (event-driven bridge)
    private readonly System.Threading.Timer _outputPollTimer;
    private int _lastKnownOutputLen;
    private bool _disposed;

    // Input write queue with backpressure
    private readonly Channel<WriteRequest> _writeChannel;
    private readonly Task _writeLoopTask;
    private readonly CancellationTokenSource _cts = new();

    private readonly record struct WriteRequest(ReadOnlyMemory<char> Text);

    // Adaptive chunking parameters
    private const int SmallThreshold = 200;
    private const int ChunkSize = 200;
    private const int ChunkDelayMs = 50;
    private const int FinalDelayMs = 300;

    public ConPtyTerminalSession(EasyWindowsTerminalControl.TermPTY pty, string sessionId)
    {
        _pty = pty ?? throw new ArgumentNullException(nameof(pty));
        _sessionId = sessionId;
        _internalId = Guid.NewGuid().ToString("N").Substring(0, 8);
        _lastKnownOutputLen = pty.ConsoleOutputLog?.Length ?? 0;

        // Channel with bounded capacity for backpressure
        _writeChannel = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _writeLoopTask = Task.Run(WriteLoopAsync);

        // Poll PTY output at high frequency (~50ms) to bridge to events.
        // This replaces the 1500ms DispatcherTimer — much more responsive,
        // and runs on a ThreadPool thread (no UI thread load).
        _outputPollTimer = new System.Threading.Timer(CheckOutputChanged, null, 50, 50);
    }

    public string SessionId => _sessionId;

    public string InternalId => _internalId;

    /// <summary>Reference hash of the underlying TermPTY — used to detect when two
    /// sessions accidentally share the same PTY instance.</summary>
    internal int PtyRefHash => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_pty);

    public bool IsRunning => !_disposed && _pty.ConsoleOutputLog != null;

    public event Action<TerminalOutputFrame>? OutputReceived;

    public int OutputLength => _pty.ConsoleOutputLog?.Length ?? 0;

    public string ReadOutput(int start, int length)
    {
        var outputLog = _pty.ConsoleOutputLog;
        if (outputLog is null || length <= 0 || start < 0 || start >= outputLog.Length)
            return "";

        var safeLength = Math.Min(length, outputLog.Length - start);
        return safeLength > 0 ? outputLog.ToString(start, safeLength) : "";
    }

    public string GetConsoleText()
    {
        try
        {
            return _pty.GetConsoleText(true);
        }
        catch
        {
            return "";
        }
    }

    // --- Synchronous write (small text, immediate) ---

    public void Write(ReadOnlySpan<char> text)
    {
        if (_disposed)
        {
            AppLogger.Log($"[Session] Write rejected: session disposed | id={_internalId} label={_sessionId}");
            return;
        }

        if (_pty.ConsoleOutputLog is null)
        {
            AppLogger.Log($"[Session] Write rejected: PTY output log null (dead pipe) | id={_internalId} label={_sessionId}");
            return;
        }

        try
        {
            _pty.WriteToTerm(text);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Session] WriteToTerm failed | id={_internalId} label={_sessionId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public void WriteAndSubmit(string text)
    {
        Write(text.AsSpan());
        // Delay Enter so PTY flushes the text as a separate event from the \r.
        // Without this, TUIs like Codex interpret text+\r as a pasted block with
        // an embedded newline instead of "input text → submit".
        _ = Task.Delay(50).ContinueWith(_ => Write("\r".AsSpan()),
            TaskScheduler.Default);
    }

    // --- Async write with backpressure (large text, queued) ---

    public async Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        if (text.Length <= SmallThreshold)
        {
            // Small writes go directly — no queuing overhead
            _pty.WriteToTerm(text.Span);
            return;
        }

        await _writeChannel.Writer.WriteAsync(new WriteRequest(text), ct);
    }

    public void SendControl(TerminalControl control)
    {
        ReadOnlySpan<char> seq = control switch
        {
            TerminalControl.Interrupt => "\x03",
            TerminalControl.Escape => "\x1b",
            TerminalControl.Enter => "\r",
            TerminalControl.Tab => "\t",
            TerminalControl.Backspace => "\x7f",
            TerminalControl.Space => " ",
            TerminalControl.Delete => "\x1b[3~",
            TerminalControl.Home => "\x1b[H",
            TerminalControl.End => "\x1b[F",
            TerminalControl.PageUp => "\x1b[5~",
            TerminalControl.PageDown => "\x1b[6~",
            TerminalControl.DownArrow => "\x1b[B",
            TerminalControl.UpArrow => "\x1b[A",
            TerminalControl.LeftArrow => "\x1b[D",
            TerminalControl.RightArrow => "\x1b[C",
            TerminalControl.ClearScreen => "\x1b[2J\x1b[H",
            _ => "",
        };
        if (seq.Length > 0)
            _pty.WriteToTerm(seq);
    }

    // --- Output change detection (bridges PTY log to events) ---

    private void CheckOutputChanged(object? state)
    {
        if (_disposed) return;

        try
        {
            var outputLog = _pty.ConsoleOutputLog;
            if (outputLog is null)
            {
                _lastKnownOutputLen = 0;
                return;
            }

            var currentLen = outputLog.Length;
            var lastLen = _lastKnownOutputLen;

            if (currentLen == lastLen) return;

            // Handle PTY reset (log got shorter)
            if (currentLen < lastLen)
            {
                _lastKnownOutputLen = currentLen;
                return;
            }

            _lastKnownOutputLen = currentLen;

            var newText = outputLog.ToString(lastLen, currentLen - lastLen);
            OutputReceived?.Invoke(new TerminalOutputFrame(newText, DateTimeOffset.UtcNow));
        }
        catch
        {
            // PTY may be disposed during shutdown — swallow
        }
    }

    // --- Write loop: processes queued large writes with adaptive chunking ---

    private async Task WriteLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var req in _writeChannel.Reader.ReadAllAsync(ct))
            {
                var text = req.Text;
                for (var i = 0; i < text.Length; i += ChunkSize)
                {
                    if (ct.IsCancellationRequested) return;

                    var len = Math.Min(ChunkSize, text.Length - i);
                    _pty.WriteToTerm(text.Span.Slice(i, len));

                    if (i + len < text.Length)
                        await Task.Delay(ChunkDelayMs, ct);
                }

                // Final delay after large write before sending Enter
                await Task.Delay(FinalDelayMs, ct);
                _pty.WriteToTerm("\r".AsSpan());
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _outputPollTimer.Dispose();
        _cts.Cancel();
        _writeChannel.Writer.TryComplete();

        try { _writeLoopTask.Wait(TimeSpan.FromSeconds(1)); }
        catch { /* shutdown */ }

        _cts.Dispose();
    }
}
