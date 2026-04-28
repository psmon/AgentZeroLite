using Agent.Common.Voice;

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
            SttProviderNames.WhisperLocal => new WhisperLocalStt(v.SttWhisperModel) { UseGpu = v.SttUseGpu },
            SttProviderNames.OpenAIWhisper => new OpenAiWhisperStt(v.SttOpenAIApiKey),
            SttProviderNames.WebnoriGemma => new WebnoriGemmaStt(v.SttWebnoriModel),
            SttProviderNames.LocalGemma => new LocalGemmaStt(v.SttLocalGemmaModelId),
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
}
