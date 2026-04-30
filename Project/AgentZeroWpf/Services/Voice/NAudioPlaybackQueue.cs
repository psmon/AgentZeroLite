using Agent.Common;
using Agent.Common.Voice;
using NAudio.Wave;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Sequential WaveOutEvent-based playback queue for the streaming TTS
/// pipeline. Each <see cref="Enqueue"/>'d clip plays in arrival order;
/// when one clip finishes, <c>PlaybackStopped</c> drains the next from the
/// queue. <see cref="Stop"/> aborts the current clip and clears the queue.
///
/// Format detection + WAV header patching is delegated to the static
/// helpers on <see cref="VoicePlaybackService"/> so the OpenAI streaming
/// sentinel ((<c>0xFFFFFFFF</c>) is handled identically here.
///
/// Thread-safe: the public surface (Enqueue/Stop) takes a private lock; the
/// PlaybackStopped callback runs on a worker thread and also takes the
/// lock. Single producer (the actor's Sink.ForEach) and single consumer
/// (NAudio's playback thread) — contention is negligible.
/// </summary>
public sealed class NAudioPlaybackQueue : IAudioPlaybackQueue
{
    private readonly object _lock = new();
    private readonly Queue<(byte[] Audio, string Format)> _pending = new();
    private WaveOutEvent? _output;
    private WaveStream? _current;
    private bool _disposed;

    // Tracks whether we have already raised <see cref="PlaybackStarted"/> for
    // the current burst. The contract on <see cref="IAudioPlaybackQueue"/>
    // says PlaybackStarted fires *once* on idle→busy. Without this flag every
    // clip start (including the next-clip drain inside <see cref="OnPlaybackStopped"/>)
    // would re-fire — and downstream consumers that pair start/stop into a
    // mute envelope would latch into a permanent state.
    private bool _started;

    public bool IsBusy
    {
        get
        {
            lock (_lock) return _output is not null || _pending.Count > 0;
        }
    }

    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;

    public void Enqueue(byte[] audio, string format)
    {
        if (audio is null || audio.Length == 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            _pending.Enqueue((audio, format));
            if (_output is null) StartNextLocked();
        }
    }

    public void Stop()
    {
        bool wasStarted;
        lock (_lock)
        {
            _pending.Clear();
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _current?.Dispose(); } catch { }
            _output = null;
            _current = null;
            wasStarted = _started;
            _started = false;
        }
        AppLogger.Log("[NAudioPlayback] Stop — queue cleared");
        // Only signal Stopped if we actually were in a started state — saves
        // downstream consumers from a phantom stop event when Stop() is
        // called on an already-idle queue (happens during CancelOutputGraph
        // for the very first SpeakResponse of a session).
        if (wasStarted) PlaybackStopped?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }

    private void StartNextLocked()
    {
        if (_pending.Count == 0) return;
        var (bytes, format) = _pending.Dequeue();
        try
        {
            var effectiveFormat = VoicePlaybackService.DetectFormat(bytes, format);
            var src = VoicePlaybackService.CreateSource(bytes, effectiveFormat);
            var output = new WaveOutEvent();
            output.Init(src);
            output.PlaybackStopped += OnPlaybackStopped;
            _current = src;
            _output = output;
            output.Play();
            // PlaybackStarted is contractually a *single* idle→busy edge per
            // burst, not per clip. Only raise on the first clip; subsequent
            // clips in the same burst (drained via OnPlaybackStopped → here)
            // must stay quiet so consumers using the event for state envelopes
            // (e.g. mic auto-mute) don't double-fire.
            if (!_started)
            {
                _started = true;
                PlaybackStarted?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[NAudioPlayback] Start failed: {ex.GetType().Name}: {ex.Message}");
            // Drop and try next; otherwise a single bad chunk would stall
            // the whole TTS playback for the rest of the response.
            StartNextLocked();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool wentIdle = false;
        lock (_lock)
        {
            try { _output?.Dispose(); } catch { }
            try { _current?.Dispose(); } catch { }
            _output = null;
            _current = null;
            if (_pending.Count > 0 && !_disposed)
                StartNextLocked();
            else
            {
                wentIdle = _started;
                _started = false;
            }
        }
        if (wentIdle) PlaybackStopped?.Invoke();
    }
}
