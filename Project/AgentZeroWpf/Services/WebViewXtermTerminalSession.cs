using System.Threading;
using System.Threading.Channels;
using Agent.Common.Services;

namespace AgentZeroWpf.Services;

/// <summary>
/// <see cref="ITerminalSession"/> over a <see cref="ManagedConPtyHost"/>, rendered
/// by xterm.js in a WebView2 — the terminal.
///
/// Output goes through <see cref="TerminalConsoleBuffer"/>, which draws the line
/// this class originally blurred: the raw stream answers
/// <see cref="OutputLength"/>/<see cref="ReadOutput"/>, and <see cref="GetConsoleText"/>
/// answers "what is on the screen". Only the emulator knows the latter, and here the
/// emulator is xterm.js in the renderer — so the renderer pushes a viewport snapshot
/// (see <c>Wasm/xterm/term.js</c>) and this class serves the last one. Control-key,
/// submit-timing, backpressure and health-state semantics mirror
/// the shape the rest of the app already expects. VT sequences come from the shared
/// <see cref="TerminalControlSequences"/> table.
/// </summary>
public sealed class WebViewXtermTerminalSession : ITerminalSession, IDisposable
{
    private readonly ManagedConPtyHost _host;
    private readonly string _sessionId;
    private readonly string _internalId;

    // Raw VT stream + the renderer's last reported viewport.
    private readonly TerminalConsoleBuffer _console = new();

    private readonly Channel<ReadOnlyMemory<char>> _writeChannel;
    private readonly Task _writeLoopTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Adaptive chunking.
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

    /// <summary>
    /// The renderer's viewport, pushed here by <c>XtermTerminalControl</c> whenever
    /// the screen settles. Before the first push <see cref="GetConsoleText"/> falls
    /// back to the tail of the stream.
    /// </summary>
    public void SetScreenSnapshot(string? screen) => _console.SetScreenSnapshot(screen);

    public int OutputLength
    {
        get { return _console.Length; }
    }

    public string ReadOutput(int start, int length) => _console.Read(start, length);

    /// <summary>
    /// What is on the screen — the renderer's viewport, same as the ConPTY backend's
    /// <c>TermPTY.GetConsoleText(true)</c>. Consumers (ApprovalParser,
    /// AgentStateMonitor) strip ANSI themselves.
    /// </summary>
    public string GetConsoleText() => _console.GetConsoleText();

    private void OnHostOutput(string chunk)
    {
        if (_disposed || string.IsNullOrEmpty(chunk)) return;
        _console.Append(chunk);

        NoteCursorControls(chunk);

        // Per-subscriber isolation — one bad consumer can't starve the others
        // starve the others.
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

    // ── Write paths ──

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
            // The success line is half of the freeze triage: a tab with no "write ok"
            // while its sibling logs them failed upstream of here; lines present with
            // no echo means the failure is in the pipe. Same reasoning, and the same
            // shape as every other diagnostic here.
            AppLogger.Log($"[XtermSession] write ok | id={_internalId} label={_sessionId} " +
                          $"bytes={text.Length} outLen={OutputLength}");
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
        // Rejections are logged rather than silent: a bot write that vanishes here
        // and a bot write that vanished upstream look identical otherwise.
        if (_disposed)
        {
            AppLogger.Log($"[XtermSession] WriteAsync rejected: disposed | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        if (!_host.IsRunning)
        {
            AppLogger.Log($"[XtermSession] WriteAsync rejected: host not running | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }

        if (text.Length <= SmallThreshold)
        {
            try
            {
                _host.Write(text.Span);
                AppLogger.Log($"[XtermSession] writeAsync small ok | id={_internalId} label={_sessionId} bytes={text.Length}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[XtermSession] writeAsync small failed | id={_internalId} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        AppLogger.Log($"[XtermSession] writeAsync queued | id={_internalId} label={_sessionId} bytes={text.Length}");
        await _writeChannel.Writer.WriteAsync(text, ct);
    }

    public void SendControl(TerminalControl control)
    {
        // send_to_terminal and send_key are the two paths the bot uses, and its
        // freeze symptom is "neither lands" - so both outcomes are recorded.
        if (_disposed)
        {
            AppLogger.Log($"[XtermSession] SendControl rejected: disposed | id={_internalId} label={_sessionId} control={control}");
            return;
        }
        if (!_host.IsRunning)
        {
            AppLogger.Log($"[XtermSession] SendControl rejected: host not running | id={_internalId} label={_sessionId} control={control}");
            return;
        }
        var seq = TerminalControlSequences.ToSequence(control);
        if (seq.Length == 0) return;
        try
        {
            _host.Write(seq.AsSpan());
            AppLogger.Log($"[XtermSession] control ok | id={_internalId} label={_sessionId} control={control}");
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
                AppLogger.Log($"[XtermSession] writeLoop start | id={_internalId} label={_sessionId} totalBytes={text.Length}");
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
                try
                {
                    _host.Write("\r".AsSpan());
                    AppLogger.Log($"[XtermSession] writeLoop end ok | id={_internalId} label={_sessionId} totalBytes={text.Length}");
                }
                catch (Exception ex)
                {
                    // A swallowed failure here means a large payload landed with no submit -
                    // the text is on screen and nothing happens, which is the hardest shape
                    // of this bug to diagnose from the outside.
                    AppLogger.Log($"[XtermSession] writeLoop final CR failed | id={_internalId} error={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Log($"[XtermSession] writeLoop exited | id={_internalId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Health state machine ──
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

    /// <summary>
    /// Which cursor controls the program actually asks for. A terminal that blinks
    /// when it should not has two very different causes - the renderer forcing it, or
    /// the program requesting it - and this is the only way to tell them apart. Each
    /// distinct sequence is logged once, and only a handful are kept.
    /// </summary>
    private readonly HashSet<string> _cursorControlsSeen = new(StringComparer.Ordinal);

    private void NoteCursorControls(string chunk)
    {
        if (_cursorControlsSeen.Count > 12) return;
        foreach (System.Text.RegularExpressions.Match m in CursorControl.Matches(chunk))
        {
            if (!_cursorControlsSeen.Add(m.Value)) continue;
            AppLogger.Log($"[XtermSession] cursor control | label={_sessionId} seq={Escape(m.Value)}");
        }
    }

    // DECTCEM show/hide (?25h/l), cursor-blink mode (?12h/l), DECSCUSR (CSI n SP q).
    private static readonly System.Text.RegularExpressions.Regex CursorControl =
        new("\\u001b\\[(?:\\?(?:25|12)[hl]|[0-9]* q)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Escape(string s) => s.Replace("\u001b", "<ESC>");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Output -= OnHostOutput;
        _cts.Cancel();
        _writeChannel.Writer.TryComplete();
        try { _writeLoopTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
        // The ManagedConPtyHost is owned by the XtermTerminalControl, which kills the
        // child when the control goes; the session only stops reading.
    }
}
