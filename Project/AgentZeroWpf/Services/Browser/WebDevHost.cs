using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Default <see cref="IZeroBrowser"/> implementation. Reuses the existing
/// VoiceRuntimeFactory + VoicePlaybackService so the WebDev sandbox shares
/// the same TTS/STT pipeline the Settings/Voice tab already drives.
/// </summary>
public sealed class WebDevHost : IZeroBrowser, IDisposable
{
    private readonly VoicePlaybackService _playback = new();

    public string GetAppVersion()
    {
        try { return Agent.Common.Module.AppVersionProvider.GetDisplayVersion(); }
        catch { return "v?"; }
    }

    public VoiceProvidersInfo GetVoiceProviders()
    {
        var v = VoiceSettingsStore.Load();
        var llm = TryReadLlmBackendName();
        return new VoiceProvidersInfo(
            Stt: string.IsNullOrWhiteSpace(v.SttProvider) ? "off" : v.SttProvider,
            Tts: string.IsNullOrWhiteSpace(v.TtsProvider) ? "off" : v.TtsProvider,
            LlmBackend: llm);
    }

    public async Task<TtsResult> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult(false, null, 0, null, "empty text");

        var v = VoiceSettingsStore.Load();
        var tts = VoiceRuntimeFactory.BuildTts(v);
        if (tts is null)
            return new TtsResult(false, v.TtsProvider, 0, null, "TTS provider is Off — pick one in Settings/Voice first");

        try
        {
            var voiceId = ResolveVoiceId(v);
            var bytes = await tts.SynthesizeAsync(text, voiceId, ct);
            if (bytes is null || bytes.Length == 0)
                return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, "synthesis returned 0 bytes");

            _playback.Play(bytes, tts.AudioFormat);
            AppLogger.Log($"[WebDev] TTS OK | provider={tts.ProviderName} bytes={bytes.Length} fmt={tts.AudioFormat}");
            return new TtsResult(true, tts.ProviderName, bytes.Length, tts.AudioFormat, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] TTS failed: {ex.GetType().Name}: {ex.Message}");
            return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, ex.Message);
        }
    }

    public void StopSpeaking() => _playback.Stop();

    public void Dispose() => _playback.Dispose();

    private static string ResolveVoiceId(VoiceSettings v)
    {
        if (!string.IsNullOrWhiteSpace(v.TtsVoice)) return v.TtsVoice;
        return v.TtsProvider == TtsProviderNames.OpenAITts ? "alloy" : string.Empty;
    }

    private static string TryReadLlmBackendName()
    {
        try
        {
            var s = Agent.Common.Llm.LlmSettingsStore.Load();
            return s.ActiveBackend.ToString();
        }
        catch { return "unknown"; }
    }
}
