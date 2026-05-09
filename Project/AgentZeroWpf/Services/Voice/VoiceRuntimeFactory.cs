using Agent.Common.Voice;
using Agent.Common.Voice.Streams;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Shared factory + small math helpers used by both the Voice Test panel
/// (<c>SettingsPanel.Voice</c>) and the AgentBot voice-input runtime
/// (<c>AgentBotWindow.Voice</c>). Extracted so AgentBot can pick the same
/// STT provider / sensitivity curve / device parsing the Voice Test uses
/// without duplicating the switch.
/// </summary>
internal static class VoiceRuntimeFactory
{
    /// <summary>Build the active <see cref="ISpeechToText"/> from saved voice settings.</summary>
    public static ISpeechToText? BuildStt(VoiceSettings v)
    {
        return v.SttProvider switch
        {
            SttProviderNames.WhisperLocal => new WhisperLocalStt(v.SttWhisperModel)
            {
                UseGpu = v.SttUseGpu,
                GpuDeviceIndex = v.SttGpuDeviceIndex,
            },
            SttProviderNames.OpenAIWhisper => new OpenAiWhisperStt(v.SttOpenAIApiKey),
            SttProviderNames.WebnoriGemma => new WebnoriGemmaStt(v.SttWebnoriModel),
            SttProviderNames.LocalGemma => new LocalGemmaStt(v.SttLocalGemmaModelId),
            _ => null,
        };
    }

    /// <summary>Build the active <see cref="ITextToSpeech"/> from saved voice settings; null when Off.</summary>
    public static ITextToSpeech? BuildTts(VoiceSettings v)
    {
        return v.TtsProvider switch
        {
            TtsProviderNames.WindowsTts => new WindowsTts(),
            TtsProviderNames.OpenAITts => new OpenAiTts(v.TtsOpenAIApiKey),
            _ => null,
        };
    }

    public static int ParseDeviceNumber(string id) => int.TryParse(id, out var n) ? n : 0;

    /// <summary>
    /// Map UI sensitivity (0–100, higher = more sensitive) to a raw RMS
    /// amplitude threshold using <c>(100 - sens) / 400</c> with a 0.005 floor.
    /// Origin-proven curve — the linear mapping fails at sens≈75 and the VAD
    /// never triggers on normal speech.
    /// </summary>
    public static float SensitivityToThreshold(double sensitivityPercent)
    {
        var inv = Math.Max(0, 100 - sensitivityPercent) / 400.0;
        return Math.Max(0.005f, (float)inv);
    }

    /// <summary>
    /// Map a UI sensitivity (0–100) into the threshold for the active
    /// <see cref="VoiceSensitivityLevel"/> in saved settings. Strict
    /// reproduces <see cref="SensitivityToThreshold"/>; Loose returns the
    /// scaled-down threshold from <see cref="VoiceSensitivityProfile"/>.
    /// Capture-side callers (<c>VoiceCaptureService</c>) only need the
    /// scalar — pre-roll and hangover are graph-stage knobs and stay on
    /// <see cref="BuildVadConfig"/>.
    /// </summary>
    public static float SensitivityToThreshold(double sensitivityPercent, VoiceSettings v)
    {
        var level = VoiceSensitivityProfile.Parse(v.SensitivityProfile);
        return VoiceSensitivityProfile.BuildVadConfig(level, sensitivityPercent).VadThreshold;
    }

    /// <summary>
    /// Build the streaming-pipeline <see cref="VadConfig"/> from saved
    /// settings. Slider value comes from <c>100 - VadThreshold</c> (the
    /// stored field is the inverse), profile picks the curve.
    /// </summary>
    public static VadConfig BuildVadConfig(VoiceSettings v)
        => BuildVadConfig(v, isAgentMode: false);

    /// <summary>
    /// Build a <see cref="VadConfig"/> with mode-aware Auto resolution.
    /// When the saved profile is <c>"Auto"</c>, agent-mode callsites
    /// (AgentBot AiMode) get Loose; ChatMode / Key keep Strict. Explicit
    /// Strict / Loose tokens always win over the mode hint.
    /// </summary>
    public static VadConfig BuildVadConfig(VoiceSettings v, bool isAgentMode)
    {
        var level = VoiceSensitivityProfile.ResolveAuto(v.SensitivityProfile, isAgentMode);
        var sensitivityPercent = Math.Clamp(100.0 - v.VadThreshold, 0.0, 100.0);
        return VoiceSensitivityProfile.BuildVadConfig(level, sensitivityPercent);
    }

    /// <summary>
    /// Mode-aware threshold scalar. Mirrors
    /// <see cref="SensitivityToThreshold(double, VoiceSettings)"/> but
    /// honours the <c>"Auto"</c> token by routing through
    /// <see cref="VoiceSensitivityProfile.ResolveAuto"/>.
    /// </summary>
    public static float SensitivityToThreshold(
        double sensitivityPercent, VoiceSettings v, bool isAgentMode)
    {
        var level = VoiceSensitivityProfile.ResolveAuto(v.SensitivityProfile, isAgentMode);
        return VoiceSensitivityProfile.BuildVadConfig(level, sensitivityPercent).VadThreshold;
    }
}
