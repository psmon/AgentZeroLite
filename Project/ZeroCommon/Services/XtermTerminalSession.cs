using System.Threading.Channels;

namespace Agent.Common.Services;

/// <summary>
/// <see cref="ITerminalSession"/> over an <see cref="IPtyHost"/>, rendered by xterm.js in
/// whatever web view the host provides (M0033). This is the WPF host's
/// <c>WebViewXtermTerminalSession</c> with the ConPTY dependency replaced by the
/// <see cref="IPtyHost"/> seam and the health machine moved into
/// <see cref="TerminalHealthTracker"/>; the WPF copy stays untouched so that app's
/// behaviour cannot change under it.
///
/// <para>Output goes through <see cref="TerminalConsoleBuffer"/>: the raw stream answers
/// <see cref="OutputLength"/>/<see cref="ReadOutput"/>, and <see cref="GetConsoleText"/>
/// answers "what is on the screen" — which only the emulator knows, so the renderer
/// pushes a viewport snapshot (<see cref="SetScreenSnapshot"/>) and this class serves
/// the last one.</para>
/// </summary>
public sealed class XtermTerminalSession : ITerminalSession, IDisposable
{
    private readonly IPtyHost _host;
    private readonly string _sessionId;
    private readonly string _internalId;
    private readonly Action<string> _log;

    private readonly TerminalConsoleBuffer _console = new();
    private readonly TerminalHealthTracker _health;

    private readonly Channel<ReadOnlyMemory<char>> _writeChannel;
    private readonly Task _writeLoopTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Adaptive chunking — a bot's long paste lands as 200-char pieces with a breath
    // between them, then a CR after a longer one. Same constants as the WPF session.
    public const int SmallThreshold = 200;
    public const int ChunkSize = 200;
    public const int ChunkDelayMs = 50;
    public const int FinalDelayMs = 300;

    public XtermTerminalSession(IPtyHost host, string sessionId, Action<string>? log = null,
        TerminalHealthTracker? health = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _sessionId = sessionId;
        _internalId = Guid.NewGuid().ToString("N")[..8];
        _log = log ?? (line => AppLogger.Log($"[XtermSession] {line}"));
        _health = health ?? new TerminalHealthTracker(() => OutputLength,
            line => _log($"{line} | id={_internalId} label={_sessionId}"));
        _health.Changed += state => { try { HealthChanged?.Invoke(state); } catch { } };

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

    /// <summary>The renderer's viewport, pushed whenever the screen settles.</summary>
    public void SetScreenSnapshot(string? screen) => _console.SetScreenSnapshot(screen);

    public int OutputLength => _console.Length;

    public string ReadOutput(int start, int length) => _console.Read(start, length);

    public string GetConsoleText() => _console.GetConsoleText();

    private void OnHostOutput(string chunk)
    {
        if (_disposed || string.IsNullOrEmpty(chunk)) return;
        _console.Append(chunk);

        var handlers = OutputReceived;
        if (handlers is not null)
        {
            var frame = new TerminalOutputFrame(chunk, DateTimeOffset.UtcNow);
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<TerminalOutputFrame>)d).Invoke(frame); }
                catch (Exception ex)
                {
                    _log($"OutputReceived subscriber threw | id={_internalId} err={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        _health.NoteOutput();
    }

    // ── Write paths ──

    public void Write(ReadOnlySpan<char> text)
    {
        if (_disposed)
        {
            _log($"Write rejected: disposed | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        if (!_host.IsRunning)
        {
            _log($"Write rejected: host not running | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        try
        {
            _host.Write(text);
            _log($"write ok | id={_internalId} label={_sessionId} bytes={text.Length} outLen={OutputLength}");
            NoteInputAttempt($"write bytes={text.Length}");
        }
        catch (Exception ex)
        {
            _log($"Write failed | id={_internalId} label={_sessionId} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
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
        if (_disposed)
        {
            _log($"WriteAsync rejected: disposed | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        if (!_host.IsRunning)
        {
            _log($"WriteAsync rejected: host not running | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }

        if (text.Length <= SmallThreshold)
        {
            try
            {
                _host.Write(text.Span);
                _log($"writeAsync small ok | id={_internalId} label={_sessionId} bytes={text.Length}");
            }
            catch (Exception ex)
            {
                _log($"writeAsync small failed | id={_internalId} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        _log($"writeAsync queued | id={_internalId} label={_sessionId} bytes={text.Length}");
        await _writeChannel.Writer.WriteAsync(text, ct);
    }

    public void SendControl(TerminalControl control)
    {
        if (_disposed)
        {
            _log($"SendControl rejected: disposed | id={_internalId} label={_sessionId} control={control}");
            return;
        }
        if (!_host.IsRunning)
        {
            _log($"SendControl rejected: host not running | id={_internalId} label={_sessionId} control={control}");
            return;
        }
        var seq = TerminalControlSequences.ToSequence(control);
        if (seq.Length == 0) return;
        try
        {
            _host.Write(seq.AsSpan());
            _log($"control ok | id={_internalId} label={_sessionId} control={control}");
            if (control != TerminalControl.ClearScreen)
                NoteInputAttempt($"control={control}");
        }
        catch (Exception ex)
        {
            _log($"control failed | id={_internalId} control={control} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task WriteLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var text in _writeChannel.Reader.ReadAllAsync(ct))
            {
                _log($"writeLoop start | id={_internalId} label={_sessionId} totalBytes={text.Length}");
                var ok = true;
                for (var i = 0; i < text.Length; i += ChunkSize)
                {
                    if (ct.IsCancellationRequested) return;
                    var len = Math.Min(ChunkSize, text.Length - i);
                    try { _host.Write(text.Span.Slice(i, len)); }
                    catch (Exception ex)
                    {
                        _log($"writeLoop chunk failed | id={_internalId} offset={i} error={ex.GetType().Name}: {ex.Message}");
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
                    _log($"writeLoop end ok | id={_internalId} label={_sessionId} totalBytes={text.Length}");
                }
                catch (Exception ex)
                {
                    _log($"writeLoop final CR failed | id={_internalId} error={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"writeLoop exited | id={_internalId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Health ──

    public TerminalHealthState HealthState => _health.State;
    public event Action<TerminalHealthState>? HealthChanged;

    public void NoteInputAttempt(string source)
    {
        if (_disposed) return;
        _health.NoteInputAttempt(source);
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
        _health.Dispose();
        // The IPtyHost is owned by the terminal control, which kills the child when the
        // control goes; the session only stops reading.
    }
}
