using System.Text;
using System.Threading;
using System.Threading.Channels;
using Agent.Common.Services;

namespace AgentZeroWpf.Services;

/// <summary>
/// <see cref="ITerminalSession"/> over a <see cref="ManagedConPtyHost"/>, rendered
/// by xterm.js in a WebView2. The modern-terminal-spike counterpart to
/// <see cref="ConPtyTerminalSession"/>.
///
/// It owns the accumulated console log (the host hands us the raw VT stream), so
/// <see cref="OutputLength"/>/<see cref="ReadOutput"/>/<see cref="GetConsoleText"/>
/// reproduce the shape the approval parser + AgentEventStream already consume —
/// no changes needed in those consumers. Control-key, submit-timing, backpressure
/// and health-state semantics mirror <see cref="ConPtyTerminalSession"/> exactly so
/// both backends behave identically through the interface. VT sequences come from
/// the shared <see cref="TerminalControlSequences"/> table.
/// </summary>
public sealed class WebViewXtermTerminalSession : ITerminalSession, IDisposable
{
    private readonly ManagedConPtyHost _host;
    private readonly string _sessionId;
    private readonly string _internalId;

    // Accumulated raw VT output (mirrors TermPTY.ConsoleOutputLog).
    private readonly StringBuilder _consoleLog = new();
    private readonly object _logSync = new();

    private readonly Channel<ReadOnlyMemory<char>> _writeChannel;
    private readonly Task _writeLoopTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Adaptive chunking — identical to ConPtyTerminalSession.
    private const int SmallThreshold = 200;
    private const int ChunkSize = 200;
    private const int ChunkDelayMs = 50;
    private const int FinalDelayMs = 300;

    public WebViewXtermTerminalSession(ManagedConPtyHost host, string sessionId)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _sessionId = sessionId;
        _internalId = Guid.NewGuid().ToString("N").Substring(0, 8);

        _host.Output += OnHostOutput;

        _writeChannel = Channel.CreateBounded<ReadOnlyMemory<char>>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _writeLoopTask = Task.Run(WriteLoopAsync);
    }

    public string SessionId => _sessionId;
    public string InternalId => _internalId;
    public bool IsRunning => !_disposed && _host.IsRunning;

    public event Action<TerminalOutputFrame>? OutputReceived;

    public int OutputLength
    {
        get { lock (_logSync) return _consoleLog.Length; }
    }

    public string ReadOutput(int start, int length)
    {
        lock (_logSync)
        {
            if (length <= 0 || start < 0 || start >= _consoleLog.Length) return "";
            var safeLength = Math.Min(length, _consoleLog.Length - start);
            return safeLength > 0 ? _consoleLog.ToString(start, safeLength) : "";
        }
    }

    public string GetConsoleText()
    {
        // The full accumulated VT transcript. Consumers (ApprovalParser,
        // AgentStateMonitor) strip ANSI themselves. NOTE (spike parity): the
        // ConPTY backend returns only the *visible screen*; here we return the
        // whole history. A SerializeAddon-based visible-screen snapshot is a
        // documented follow-up refinement.
        lock (_logSync) return _consoleLog.ToString();
    }

    private void OnHostOutput(string chunk)
    {
        if (_disposed || string.IsNullOrEmpty(chunk)) return;
        lock (_logSync) _consoleLog.Append(chunk);

        // Per-subscriber isolation — one bad consumer can't starve the others
        // (mirrors ConPtyTerminalSession.CheckOutputChanged).
        var handlers = OutputReceived;
        if (handlers is not null)
        {
            var frame = new TerminalOutputFrame(chunk, DateTimeOffset.UtcNow);
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<TerminalOutputFrame>)d).Invoke(frame); }
                catch (Exception ex)
                {
                    AppLogger.Log($"[XtermSession] OutputReceived subscriber threw | id={_internalId} err={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        OnOutputObserved();
    }

    // ── Write paths (mirror ConPtyTerminalSession) ──

    public void Write(ReadOnlySpan<char> text)
    {
        if (_disposed)
        {
            AppLogger.Log($"[XtermSession] Write rejected: disposed | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        if (!_host.IsRunning)
        {
            AppLogger.Log($"[XtermSession] Write rejected: host not running | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        try
        {
            _host.Write(text);
            NoteInputAttempt($"write bytes={text.Length}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[XtermSession] Write failed | id={_internalId} label={_sessionId} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public void WriteAndSubmit(string text)
    {
        Write(text.AsSpan());
        _ = Task.Delay(50).ContinueWith(_ => Write("\r".AsSpan()), TaskScheduler.Default);
    }

    public void WriteAndEnter(string text)
    {
        Write(text.AsSpan());
        _ = Task.Delay(200).ContinueWith(_ => SendControl(TerminalControl.Enter), TaskScheduler.Default);
    }

    public async Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        if (_disposed || !_host.IsRunning) return;

        if (text.Length <= SmallThreshold)
        {
            try { _host.Write(text.Span); }
            catch (Exception ex)
            {
                AppLogger.Log($"[XtermSession] writeAsync small failed | id={_internalId} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        await _writeChannel.Writer.WriteAsync(text, ct);
    }

    public void SendControl(TerminalControl control)
    {
        if (_disposed || !_host.IsRunning) return;
        var seq = TerminalControlSequences.ToSequence(control);
        if (seq.Length == 0) return;
        try
        {
            _host.Write(seq.AsSpan());
            if (control != TerminalControl.ClearScreen)
                NoteInputAttempt($"control={control}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[XtermSession] control failed | id={_internalId} control={control} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task WriteLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var text in _writeChannel.Reader.ReadAllAsync(ct))
            {
                var ok = true;
                for (var i = 0; i < text.Length; i += ChunkSize)
                {
                    if (ct.IsCancellationRequested) return;
                    var len = Math.Min(ChunkSize, text.Length - i);
                    try { _host.Write(text.Span.Slice(i, len)); }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[XtermSession] writeLoop chunk failed | id={_internalId} offset={i} error={ex.GetType().Name}: {ex.Message}");
                        ok = false;
                        break;
                    }
                    if (i + len < text.Length) await Task.Delay(ChunkDelayMs, ct);
                }
                if (!ok) continue;
                await Task.Delay(FinalDelayMs, ct);
                try { _host.Write("\r".AsSpan()); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Log($"[XtermSession] writeLoop exited | id={_internalId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Health state machine (identical thresholds/timing to ConPtyTerminalSession) ──
    private const int EchoCheckMs = 1000;
    private const int StaleThreshold = 3;
    private const int DeadThreshold = 5;

    private int _consecutiveNoEcho;
    private TerminalHealthState _healthState = TerminalHealthState.Alive;

    public TerminalHealthState HealthState => _healthState;
    public event Action<TerminalHealthState>? HealthChanged;

    public void NoteInputAttempt(string source)
    {
        if (_disposed) return;
        var snapshot = OutputLength;
        _ = Task.Delay(EchoCheckMs).ContinueWith(_ =>
        {
            if (_disposed) return;
            if (OutputLength != snapshot) return; // got echo
            AppLogger.Log($"[XtermSession] INPUT-NO-ECHO | id={_internalId} label={_sessionId} source={source} outLenStable={snapshot}");
            var n = Interlocked.Increment(ref _consecutiveNoEcho);
            EvaluateHealth(n, source);
        }, TaskScheduler.Default);
    }

    private void EvaluateHealth(int consecutive, string source)
    {
        var newState = consecutive switch
        {
            >= DeadThreshold => TerminalHealthState.Dead,
            >= StaleThreshold => TerminalHealthState.Stale,
            _ => _healthState,
        };
        if (newState == _healthState) return;
        _healthState = newState;
        AppLogger.Log($"[XtermSession] HEALTH | id={_internalId} label={_sessionId} state={newState} consecutive={consecutive} source={source}");
        try { HealthChanged?.Invoke(newState); } catch { }
    }

    private void OnOutputObserved()
    {
        if (Interlocked.Exchange(ref _consecutiveNoEcho, 0) == 0
            && _healthState == TerminalHealthState.Alive)
            return;
        if (_healthState != TerminalHealthState.Alive)
        {
            _healthState = TerminalHealthState.Alive;
            AppLogger.Log($"[XtermSession] HEALTH | id={_internalId} label={_sessionId} state=Alive (output recovered)");
            try { HealthChanged?.Invoke(TerminalHealthState.Alive); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Output -= OnHostOutput;
        _cts.Cancel();
        _writeChannel.Writer.TryComplete();
        try { _writeLoopTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
        // The ManagedConPtyHost is owned by the XtermTerminalControl (mirrors how
        // ConPtyTerminalSession does NOT kill the TermPTY it wraps).
    }
}
