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
            AppLogger.Log($"[Session] Write rejected: session disposed | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }

        if (_pty.ConsoleOutputLog is null)
        {
            AppLogger.Log($"[Session] Write rejected: PTY output log null (dead pipe) | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length}");
            return;
        }

        try
        {
            _pty.WriteToTerm(text);
            // PTY-FREEZE-DIAG: success line proves the write reached WriteToTerm.
            // If a freeze leaves no [Session] write ok line for a tab while the
            // sibling tab logs them, the failure is upstream of WriteToTerm
            // (route never reaches this method). If lines exist but the PTY
            // doesn't echo, the failure is inside WriteToTerm / the pipe.
            AppLogger.Log($"[Session] write ok | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length} outLen={_pty.ConsoleOutputLog?.Length ?? -1}");
            NoteInputAttempt($"write bytes={text.Length}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Session] WriteToTerm failed | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Channel health state machine ─────────────────────────────────────
    // Every input attempt (Write, SendControl, or direct keyboard via
    // NoteInputAttempt) schedules an output-growth check after EchoCheckMs.
    // No growth → bump _consecutiveNoEcho. Output growth (detected by the
    // poll thread in CheckOutputChanged) atomically resets the counter and
    // restores Alive. Thresholds 3 / 5 are conservative — slow TUI loads
    // (claude CLI startup, tailscale ssh handshake) can briefly produce
    // no echo without being truly dead, so Stale (3) is the early warning
    // and Dead (5) is the actionable signal.
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
        var beforeLen = _pty.ConsoleOutputLog?.Length ?? -1;
        if (beforeLen < 0) return;
        var snapshot = beforeLen;
        var hash = PtyRefHash;
        var id = _internalId;
        var label = _sessionId;
        _ = Task.Delay(EchoCheckMs).ContinueWith(_ =>
        {
            if (_disposed) return;
            var afterLen = _pty.ConsoleOutputLog?.Length ?? -1;
            if (afterLen != snapshot) return; // got echo
            AppLogger.Log($"[Session] INPUT-NO-ECHO | id={id} label={label} ptyHash=0x{hash:X8} source={source} outLenStable={afterLen} after={EchoCheckMs}ms");
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
            _ => _healthState, // do not regress on a single OK
        };
        if (newState == _healthState) return;
        _healthState = newState;
        AppLogger.Log($"[Session] HEALTH | id={_internalId} label={_sessionId} state={newState} consecutive={consecutive} source={source}");
        try { HealthChanged?.Invoke(newState); } catch { /* subscriber bug, not load-bearing */ }
    }

    private void OnOutputObserved()
    {
        if (Interlocked.Exchange(ref _consecutiveNoEcho, 0) == 0
            && _healthState == TerminalHealthState.Alive)
            return;
        if (_healthState != TerminalHealthState.Alive)
        {
            _healthState = TerminalHealthState.Alive;
            AppLogger.Log($"[Session] HEALTH | id={_internalId} label={_sessionId} state=Alive (output recovered)");
            try { HealthChanged?.Invoke(TerminalHealthState.Alive); } catch { }
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

    public void WriteAndEnter(string text)
    {
        Write(text.AsSpan());
        // 200ms gap + SendControl path: Codex still eats the \r at 50ms via the
        // raw Write path. Routing Enter through SendControl gives it the same
        // treatment as a user keypress and the longer gap lets the TUI commit
        // the text buffer before the submit lands.
        _ = Task.Delay(200).ContinueWith(_ => SendControl(TerminalControl.Enter),
            TaskScheduler.Default);
    }

    // --- Async write with backpressure (large text, queued) ---

    public async Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default)
    {
        // PTY-FREEZE-DIAG: WriteAsync had no rejection paths before, so a
        // disposed session or dead pipe would either throw inside WriteToTerm
        // or silently land in the Channel queue with no record. These checks
        // mirror Write() for symmetry and visibility.
        if (_disposed)
        {
            AppLogger.Log($"[Session] WriteAsync rejected: disposed | id={_internalId} label={_sessionId} bytes={text.Length}");
            return;
        }
        if (_pty.ConsoleOutputLog is null)
        {
            AppLogger.Log($"[Session] WriteAsync rejected: PTY output log null | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length}");
            return;
        }

        if (text.Length <= SmallThreshold)
        {
            // Small writes go directly — no queuing overhead
            try
            {
                _pty.WriteToTerm(text.Span);
                AppLogger.Log($"[Session] writeAsync small ok | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Session] writeAsync small failed | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length} error={ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        AppLogger.Log($"[Session] writeAsync queued | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} bytes={text.Length}");
        await _writeChannel.Writer.WriteAsync(new WriteRequest(text), ct);
    }

    public void SendControl(TerminalControl control)
    {
        // PTY-FREEZE-DIAG: SendControl had no rejection paths or success log
        // before. send_to_terminal + send_key are the two paths the AIMODE bot
        // uses, and the bot's freeze symptom was "neither lands". Logging both
        // outcomes makes it possible to distinguish "Write rejected (rare)"
        // from "WriteToTerm threw" from "WriteToTerm silently no-oped".
        if (_disposed)
        {
            AppLogger.Log($"[Session] SendControl rejected: disposed | id={_internalId} label={_sessionId} control={control}");
            return;
        }
        if (_pty.ConsoleOutputLog is null)
        {
            AppLogger.Log($"[Session] SendControl rejected: PTY output log null | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} control={control}");
            return;
        }

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
        if (seq.Length == 0) return;

        try
        {
            _pty.WriteToTerm(seq);
            AppLogger.Log($"[Session] control ok | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} control={control}");
            // ClearScreen overwrites in place — no echo expected. Other
            // controls (Enter/CR, Tab, Space, arrows in cooked mode) should
            // produce some response from a healthy shell.
            if (control != TerminalControl.ClearScreen)
                NoteInputAttempt($"control={control}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Session] control failed | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} control={control} error={ex.GetType().Name}: {ex.Message}");
        }
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
            // Output growth = the channel just produced something. Reset the
            // no-echo counter and lift Stale/Dead back to Alive. Idempotent
            // so the 50ms poll thread can call this every tick safely.
            OnOutputObserved();
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
                AppLogger.Log($"[Session] writeLoop start | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} totalBytes={text.Length}");
                var chunkOk = true;
                for (var i = 0; i < text.Length; i += ChunkSize)
                {
                    if (ct.IsCancellationRequested) return;

                    var len = Math.Min(ChunkSize, text.Length - i);
                    try
                    {
                        _pty.WriteToTerm(text.Span.Slice(i, len));
                    }
                    catch (Exception ex)
                    {
                        // PTY-FREEZE-DIAG: a chunk failure aborts the rest of
                        // the queued write — the bot's handshake (1420 bytes)
                        // would partially land then stop. Logging the exact
                        // offset narrows down whether the pipe broke mid-write
                        // or refused the very first chunk.
                        AppLogger.Log($"[Session] writeLoop chunk failed | id={_internalId} ptyHash=0x{PtyRefHash:X8} offset={i} chunkLen={len} error={ex.GetType().Name}: {ex.Message}");
                        chunkOk = false;
                        break;
                    }

                    if (i + len < text.Length)
                        await Task.Delay(ChunkDelayMs, ct);
                }

                if (!chunkOk) continue;

                // Final delay after large write before sending Enter
                await Task.Delay(FinalDelayMs, ct);
                try
                {
                    _pty.WriteToTerm("\r".AsSpan());
                    AppLogger.Log($"[Session] writeLoop end ok | id={_internalId} label={_sessionId} ptyHash=0x{PtyRefHash:X8} totalBytes={text.Length}");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Session] writeLoop final \\r failed | id={_internalId} ptyHash=0x{PtyRefHash:X8} error={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // PTY-FREEZE-DIAG: catch-all so a write-loop crash doesn't vanish
            // the entire input channel for the rest of the session lifetime.
            AppLogger.Log($"[Session] writeLoop exited unexpectedly | id={_internalId} ptyHash=0x{PtyRefHash:X8} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _writeChannel.Writer.TryComplete();
        _outputPollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _outputPollTimer.Dispose();

        try { _writeLoopTask.Wait(TimeSpan.FromSeconds(1)); }
        catch { /* shutdown */ }

        _cts.Dispose();
    }
}
