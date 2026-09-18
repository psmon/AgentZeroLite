namespace Agent.Common.Services;

/// <summary>
/// The wedged-terminal detector, on its own (M0033). Every keystroke or bot write is
/// followed one second later by a look at the output length; input that produced no
/// echo counts against the session, three in a row is <see cref="TerminalHealthState.Stale"/>,
/// five is <see cref="TerminalHealthState.Dead"/>, and any output at all resets to
/// <see cref="TerminalHealthState.Alive"/>. Extracted from the WPF session class so the
/// Avalonia session shares it and it can be tested without a PTY (the delay is
/// injectable).
/// </summary>
public sealed class TerminalHealthTracker : IDisposable
{
    public const int DefaultEchoCheckMs = 1000;
    public const int DefaultStaleThreshold = 3;
    public const int DefaultDeadThreshold = 5;

    private readonly Func<int> _outputLength;
    private readonly Action<string>? _log;
    private readonly int _echoCheckMs;
    private readonly int _staleThreshold;
    private readonly int _deadThreshold;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();

    private int _consecutiveNoEcho;
    private TerminalHealthState _state = TerminalHealthState.Alive;
    private bool _disposed;

    public TerminalHealthTracker(Func<int> outputLength, Action<string>? log = null,
        int echoCheckMs = DefaultEchoCheckMs, int staleThreshold = DefaultStaleThreshold,
        int deadThreshold = DefaultDeadThreshold, Func<int, CancellationToken, Task>? delay = null)
    {
        _outputLength = outputLength;
        _log = log;
        _echoCheckMs = echoCheckMs;
        _staleThreshold = staleThreshold;
        _deadThreshold = deadThreshold;
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public TerminalHealthState State => _state;

    public event Action<TerminalHealthState>? Changed;

    /// <summary>Input went to the child; check for an echo after the grace period.</summary>
    public void NoteInputAttempt(string source)
    {
        if (_disposed) return;
        var snapshot = _outputLength();
        _ = CheckLaterAsync(snapshot, source);
    }

    private async Task CheckLaterAsync(int snapshot, string source)
    {
        try { await _delay(_echoCheckMs, _cts.Token); }
        catch (OperationCanceledException) { return; }
        if (_disposed) return;
        if (_outputLength() != snapshot) return;   // got echo

        _log?.Invoke($"INPUT-NO-ECHO source={source} outLenStable={snapshot}");
        var n = Interlocked.Increment(ref _consecutiveNoEcho);
        var next = n >= _deadThreshold ? TerminalHealthState.Dead
            : n >= _staleThreshold ? TerminalHealthState.Stale
            : _state;
        Transition(next, $"consecutive={n} source={source}");
    }

    /// <summary>Output arrived; the session is alive by definition.</summary>
    public void NoteOutput()
    {
        if (Interlocked.Exchange(ref _consecutiveNoEcho, 0) == 0 && _state == TerminalHealthState.Alive)
            return;
        Transition(TerminalHealthState.Alive, "output recovered");
    }

    private void Transition(TerminalHealthState next, string why)
    {
        if (next == _state) return;
        _state = next;
        _log?.Invoke($"HEALTH state={next} ({why})");
        try { Changed?.Invoke(next); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
