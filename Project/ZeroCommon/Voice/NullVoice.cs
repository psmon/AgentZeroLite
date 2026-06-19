namespace Agent.Common.Voice;

// ───────────────────────────────────────────────────────────
// cross-platform 음성 기본(Null) 구현 + 팩토리.
//
// 실제 제공자는 플랫폼 네이티브라 OS별로 갈린다:
//   • TTS   : Windows=System.Speech(SAPI) / macOS=AVSpeechSynthesizer
//   • STT   : Whisper.net (cross-platform 런타임 존재) / 클라우드
//   • 재생   : Windows=NAudio(WaveOutEvent) / macOS=AVAudioEngine
// 이들 네이티브 구현이 준비되기 전에도 앱이 graceful하게 동작하도록
// 모든 OS에서 동작하는 Null 기본값을 제공한다. 팩토리가 향후 플랫폼
// 구현으로 교체될 지점이다. (구현 로드맵: Docs/avalonia-port/PORTING.md)
// ───────────────────────────────────────────────────────────

public sealed class NullTextToSpeech : ITextToSpeech
{
    public string ProviderName => "none";
    public string AudioFormat => "pcm16";
    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
}

public sealed class NullSpeechToText : ISpeechToText
{
    public string ProviderName => "none";
    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

public sealed class NullAudioPlaybackQueue : IAudioPlaybackQueue
{
    public void Enqueue(byte[] audio, string format) { }
    public void Stop() { }
    public bool IsBusy => false;
#pragma warning disable CS0067 // Null 구현은 발생시키지 않음(인터페이스 충족용)
    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;
#pragma warning restore CS0067
    public void Dispose() { }
}

/// <summary>
/// OS별 음성 서비스 팩토리. 현재는 모든 OS에서 Null 기본값을 반환한다.
/// 플랫폼 네이티브 제공자가 추가되면 여기서 OperatingSystem.Is* 로 분기한다.
/// </summary>
public static class VoiceServices
{
    public static ITextToSpeech CreateTextToSpeech() => new NullTextToSpeech();
    public static ISpeechToText CreateSpeechToText() => new NullSpeechToText();
    public static IAudioPlaybackQueue CreateAudioPlaybackQueue() => new NullAudioPlaybackQueue();
}
