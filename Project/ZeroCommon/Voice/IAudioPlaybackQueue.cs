namespace Agent.Common.Voice;

/// <summary>
/// Sequential audio playback abstraction. Each <see cref="Enqueue"/>d clip
/// plays in arrival order through whatever output device the implementation
/// owns. The TTS pipeline produces audio chunks faster than they play
/// (synthesize is parallel; playback is serial), so the queue lets the
/// graph keep TTS workers busy ahead of the speaker without extra
/// coordination plumbing.
///
/// Implementation lives in <c>AgentZeroWpf</c> wrapping NAudio's
/// <c>WaveOutEvent</c> — kept out of <c>ZeroCommon</c> so the actor
/// layer stays headless-testable.
/// </summary>
public interface IAudioPlaybackQueue : IDisposable
{
    /// <summary>
    /// Append a clip. <paramref name="format"/> follows the existing
    /// convention used by <c>VoicePlaybackService</c>: <c>"wav"</c>,
    /// <c>"mp3"</c>, or <c>"pcm16"</c> (raw 24 kHz mono fallback).
    /// </summary>
    void Enqueue(byte[] audio, string format);

    /// <summary>Stop immediately and clear any queued clips.</summary>
    void Stop();

    /// <summary>True while a clip is playing or the queue is non-empty.</summary>
    bool IsBusy { get; }

    /// <summary>Fires once when playback transitions from idle to busy.</summary>
    event Action? PlaybackStarted;

    /// <summary>Fires once when the queue drains and playback returns to idle.</summary>
    event Action? PlaybackStopped;
}
