using System.Threading;
using Agent.Common;
using Agent.Common.Voice;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Test-mode helper that drives the existing voice pipeline end-to-end
/// without needing a human speaker. The flow is deliberately the long
/// way around:
///
///   typed text → ITextToSpeech.SynthesizeAsync → VoicePlaybackService
///   → speaker → microphone (loopback through OS audio) → VAD → STT
///   → AgentBot reaction
///
/// Why the long route? The intent is to <i>validate</i> the STT path —
/// VAD threshold, segmenter / pre-roll, STT provider auth + transcription
/// fidelity, AgentBot routing. Bypassing the speaker / mic step (e.g. by
/// piping PCM directly into <c>VoiceStreamActor</c>) would skip exactly
/// what needs verifying. So the audio really does play out the speaker;
/// the mic must be ON and physically coupled to the output for the test
/// to round-trip.
///
/// Constraints:
///   • TTS provider in <see cref="VoiceSettings"/> must not be Off.
///   • Mic should be ON for round-trip; if not, only the TTS leg runs.
///   • A separate <see cref="VoicePlaybackService"/> is used so the bot's
///     own reply playback (when streaming pipeline is active) doesn't
///     conflict with this test playback. Concurrent triggers on the
///     same default output device may compete — that's a real-system
///     limitation, not something this class can fix.
///
/// Lives in <c>AgentZeroWpf</c> because <see cref="VoicePlaybackService"/>
/// is WPF-side (NAudio). The injector itself is a thin orchestration
/// layer — no UI, no actor coupling.
/// </summary>
public sealed class VirtualVoiceInjector : IDisposable
{
    private readonly VoicePlaybackService _player = new();
    private CancellationTokenSource? _activeCts;
    private volatile bool _isBusy;

    /// <summary>True between <see cref="SpeakAsync"/> kickoff and playback completion.</summary>
    public bool IsBusy => _isBusy;

    public event Action? Started;
    public event Action? Stopped;
    public event Action<Exception>? Errored;

    public VirtualVoiceInjector()
    {
        _player.PlaybackStarted += () => Started?.Invoke();
        _player.PlaybackStopped += () =>
        {
            _isBusy = false;
            Stopped?.Invoke();
        };
    }

    /// <summary>
    /// Synthesize <paramref name="text"/> via the active TTS provider and play
    /// it through the default output device. Returns the WAV bytes that were
    /// produced + the format string; callers that want to replay the same
    /// audio later (history rows, A/B comparison) can keep a reference and
    /// hand it to <see cref="VoicePlaybackService.Play"/> directly.
    ///
    /// Returns the empty array on cancellation. Throws on real failures.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// TTS is set to Off, or the configured provider failed to construct.
    /// </exception>
    public async Task<(byte[] Wav, string Format)> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must be non-empty", nameof(text));

        var v = VoiceSettingsStore.Load();
        if (string.Equals(v.TtsProvider, TtsProviderNames.Off, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "TTS is Off — open Settings → Voice and pick a TTS provider before using virtual voice.");

        var tts = VoiceRuntimeFactory.BuildTts(v)
            ?? throw new InvalidOperationException(
                $"TTS provider '{v.TtsProvider}' could not be constructed.");

        // Cancel any previous in-flight synthesis. Playback that's already
        // running keeps going — caller can call Stop() if they need silence.
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _isBusy = true;
        AppLogger.Log($"[VirtualVoice] synth text=\"{Trunc(text, 60)}\" provider={v.TtsProvider} voice={v.TtsVoice}");

        try
        {
            var bytes = await tts.SynthesizeAsync(text, v.TtsVoice, _activeCts.Token);
            if (_activeCts.IsCancellationRequested) { _isBusy = false; return (Array.Empty<byte>(), tts.AudioFormat); }
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException("TTS returned empty audio.");

            _player.Play(bytes, tts.AudioFormat);
            AppLogger.Log($"[VirtualVoice] play OK | bytes={bytes.Length} format={tts.AudioFormat}");
            return (bytes, tts.AudioFormat);
        }
        catch (Exception ex)
        {
            _isBusy = false;
            AppLogger.LogError("[VirtualVoice] SpeakAsync failed", ex);
            Errored?.Invoke(ex);
            throw;
        }
        finally
        {
            (tts as IDisposable)?.Dispose();
        }
    }

    /// <summary>Stop any active playback immediately.</summary>
    public void Stop()
    {
        try { _activeCts?.Cancel(); } catch { }
        _player.Stop();
        _isBusy = false;
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
        _activeCts?.Dispose();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
