using Agent.Common.Voice;

namespace ZeroWearable.Voice;

/// <summary>One synthesized answer, already framed for the device.</summary>
public sealed record Speech(List<byte[]> Frames, int DurationMs, int Rate, byte[] Pcm16);

/// <summary>
/// The watch's voice — <b>AgentZero's</b> voice.
///
/// The reference host (<c>samples/akka/host/AkkaHost</c>) carried its own 466-line copy of the SuperTonic-3
/// pipeline, and this class later grew a copy of each engine's model handling. Both are gone: the engine is
/// ZeroCommon's <see cref="IVoiceEngine"/> that Settings → Voice names (<see cref="VoiceEngines.Create"/> —
/// Supertonic, KittenTTS 0.8 or KittenTTS 2), the same object the desktop's Voice tab speaks with, from the same
/// <c>%LOCALAPPDATA%\AgentZeroLite\models</c> bundles and the voice / language picked there. Change the voice there
/// and the watch changes voice.
///
/// What is left here is the part AgentZero has no use for: the watch cannot play 24/44.1 kHz float, so the samples
/// are resampled to 16 kHz, IMA-ADPCM encoded and cut into the frames the firmware's decoder expects
/// (<see cref="DeviceAudio"/>).
///
/// It never downloads anything. With the engine's model absent <see cref="Available"/> is false, the device is told
/// <c>tts:false</c> in hostinfo, hides its voice toggle and answers stay text.
/// </summary>
public sealed class WearableVoice : IDisposable
{
    private readonly VoiceSettings _settings;
    private readonly IVoiceEngine? _engine;

    public WearableVoice(VoiceSettings settings, Action<string, string> log)
    {
        _settings = settings;
        _engine = VoiceEngines.Create(settings, log);
    }

    /// <summary>The engine Settings → Voice picked, or null for a provider with no on-device engine.</summary>
    public IVoiceEngine? Engine => _engine;

    /// <summary>Settings → Voice voice of the chosen engine. A request may ask for another.</summary>
    public string VoiceId => _engine?.DefaultVoice ?? "F1";

    /// <summary>The default output language — the brain answers in it, so it must be one the engine speaks.</summary>
    public string LanguageId => _engine?.DefaultLanguage ?? "ko";

    /// <summary>The voices the watch lists in its Settings screen — exactly the engine's, not a hardcoded set.</summary>
    public IReadOnlyList<string> AvailableVoices => _engine?.Available == true ? _engine.Voices : [];

    /// <summary>
    /// Voice output is on when Settings → Voice names an on-device engine <i>and</i> its model is installed. Windows
    /// SAPI and OpenAI are desktop-only paths — neither produces the raw samples this has to resample and frame.
    /// </summary>
    public bool Available => _engine?.Available == true;

    /// <summary>Why voice is off (or what speaks), for the startup line the panel shows.</summary>
    public string Status => _engine?.Status
        ?? $"Settings → Voice → TTS is '{_settings.TtsProvider}'; the watch needs " +
           string.Join(", ", VoiceEngines.OnDevice);

    /// <summary>
    /// Synthesize and frame. Blocking and CPU/GPU-heavy, so callers run it off the actor thread.
    /// </summary>
    public Speech Synthesize(string text, int requestId, string? voice = null, string? language = null,
        CancellationToken ct = default)
    {
        var v = Render(text, voice, language, ct);
        var pcm = DeviceAudio.ToPcm16k(v.Samples, v.SampleRate);
        var blocks = ImaAdpcm.EncodeBlocks(pcm, DeviceAudio.SamplesPerBlock);

        var frames = new List<byte[]>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++) frames.Add(DeviceAudio.SpeechFrame(requestId, i, blocks[i]));

        return new Speech(frames, DeviceAudio.DurationMs(pcm), DeviceAudio.TargetRate, pcm);
    }

    /// <summary>
    /// The same synthesis at the model's own rate, as a WAV for the PC's speakers — no 16 kHz resample and no
    /// ADPCM, because nothing has to cross the BLE link.
    /// </summary>
    public (byte[] Wav, TimeSpan Length) SynthesizeWav(string text, string? voice = null, string? language = null,
        CancellationToken ct = default)
    {
        var v = Render(text, voice, language, ct);
        var decoded = new WavPcm.Decoded(v.Samples, v.SampleRate);
        return (WavPcm.ToWav(decoded), TimeSpan.FromSeconds(decoded.DurationSeconds));
    }

    private VoiceSamples Render(string text, string? voice, string? language, CancellationToken ct)
    {
        if (_engine is not { Available: true }) throw new InvalidOperationException($"voice unavailable: {Status}");
        ct.ThrowIfCancellationRequested();
        return _engine.Synthesize(text, voice, language, ct);
    }

    public void Dispose() => _engine?.Dispose();
}
