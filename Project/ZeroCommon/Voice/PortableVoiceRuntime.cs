namespace Agent.Common.Voice;

/// <summary>
/// Builds the TTS and STT that <see cref="VoiceSettings"/> names, out of the providers that
/// run on every OS: Supertonic (ONNX), KittenTTS 2 (Windows: llama.cpp + ONNX), OpenAI TTS, Whisper.net and OpenAI Whisper. The WPF
/// host has its own factory because it also offers Windows SAPI and the LocalGemma stub;
/// a provider this one cannot build comes back null with the reason, so a screen can say
/// why instead of failing on first use.
/// </summary>
public static class PortableVoiceRuntime
{
    /// <summary>The TTS providers this runtime can build, in the order a picker shows them.</summary>
    public static readonly IReadOnlyList<string> TtsProviders =
        [TtsProviderNames.Off, TtsProviderNames.Supertonic, TtsProviderNames.KittenTts2, TtsProviderNames.OpenAITts];

    /// <summary>The STT providers this runtime can build.</summary>
    public static readonly IReadOnlyList<string> SttProviders =
        [SttProviderNames.WhisperLocal, SttProviderNames.OpenAIWhisper];

    public static ITextToSpeech? CreateTts(VoiceSettings s, out string reason)
    {
        switch (s.TtsProvider)
        {
            case TtsProviderNames.Supertonic:
            case TtsProviderNames.KittenTts2:
                // The same engines the wearable host speaks with (IVoiceEngine), so a voice behaves the same on the
                // desktop and on the watch - and the model they load stays loaded for the next call (the Voice
                // tab's test used to reload it on every click).
                var engine = VoiceEngines.Create(s)!;
                if (!engine.Available)
                {
                    reason = engine.Status;
                    engine.Dispose();
                    return null;
                }
                reason = "";
                return new VoiceEngineTts(engine);
            case TtsProviderNames.OpenAITts:
                if (string.IsNullOrWhiteSpace(s.TtsOpenAIApiKey))
                {
                    reason = "OpenAI API key is empty";
                    return null;
                }
                reason = "";
                return new OpenAiTts(s.TtsOpenAIApiKey);
            case TtsProviderNames.Off:
                reason = "voice output is off";
                return null;
            default:
                reason = $"{s.TtsProvider} is not available in this host";
                return null;
        }
    }

    /// <summary>
    /// The voice to pass to <see cref="ITextToSpeech.SynthesizeAsync"/>. Supertonic reads its
    /// own <see cref="VoiceSettings.SupertonicVoice"/> — the generic <see cref="VoiceSettings.TtsVoice"/>
    /// would otherwise override it, and the wearable host reads only the Supertonic field.
    /// </summary>
    public static string VoiceFor(VoiceSettings s) => s.TtsProvider switch
    {
        TtsProviderNames.Supertonic => s.SupertonicVoice,
        TtsProviderNames.KittenTts2 => s.Kitten2Voice,
        _ => s.TtsVoice,
    };


    public static ISpeechToText? CreateStt(VoiceSettings s, out string reason)
    {
        switch (s.SttProvider)
        {
            case SttProviderNames.WhisperLocal:
                reason = "";
                return new WhisperNetStt(s.SttWhisperModel) { UseGpu = s.SttUseGpu, GpuDeviceIndex = s.SttGpuDeviceIndex };
            case SttProviderNames.OpenAIWhisper:
                if (string.IsNullOrWhiteSpace(s.SttOpenAIApiKey))
                {
                    reason = "OpenAI API key is empty";
                    return null;
                }
                reason = "";
                return new OpenAiWhisperStt(s.SttOpenAIApiKey);
            default:
                reason = $"{s.SttProvider} is not available in this host";
                return null;
        }
    }
}
