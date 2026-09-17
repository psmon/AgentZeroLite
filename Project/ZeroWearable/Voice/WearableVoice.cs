using Agent.Common.Voice;

namespace ZeroWearable.Voice;

/// <summary>One synthesized answer, already framed for the device.</summary>
public sealed record Speech(List<byte[]> Frames, int DurationMs, int Rate, byte[] Pcm16);

/// <summary>
/// The watch's voice — <b>AgentZero's</b> voice.
///
/// The reference host (<c>samples/akka/host/AkkaHost</c>) carried its own 466-line copy of
/// the SuperTonic-3 pipeline. That copy is gone: this drives
/// <see cref="SuperTonicSynthesizer"/> out of <c>ZeroCommon</c> — the same four ONNX graphs
/// the Voice tab loads, from the same
/// <c>%LOCALAPPDATA%\AgentZeroLite\models\supertonic</c> bundle, with the voice / steps /
/// speed / language the user already picked in Settings → Voice. Change the voice there and
/// the watch changes voice.
///
/// What is left here is the part AgentZero has no use for: the watch cannot play 44.1 kHz
/// float, so the samples are resampled to 16 kHz, IMA-ADPCM encoded and cut into the frames
/// the firmware's decoder expects (<see cref="DeviceAudio"/>).
///
/// It never downloads anything. With the bundle absent <see cref="Available"/> is false, the
/// device is told <c>tts:false</c> in hostinfo, hides its voice toggle and answers stay text.
/// </summary>
public sealed class WearableVoice : IDisposable
{
    private readonly VoiceSettings _settings;
    private readonly string _modelDir;
    private readonly Action<string, string> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, SuperTonicStyle> _styles = new(StringComparer.OrdinalIgnoreCase);
    private SuperTonicSynthesizer? _synth;

    public WearableVoice(VoiceSettings settings, Action<string, string> log)
    {
        _settings = settings;
        _log = log;
        _modelDir = SuperTonicModelStore.ResolveModelDir(settings);
    }

    public string ModelDirectory => _modelDir;

    /// <summary>Settings → Voice → Supertonic voice. A request may ask for another.</summary>
    public string VoiceId => string.IsNullOrWhiteSpace(_settings.SupertonicVoice) ? "F1" : _settings.SupertonicVoice;

    /// <summary>Settings → Voice → Supertonic language, the default output language.</summary>
    public string LanguageId => string.IsNullOrWhiteSpace(_settings.SupertonicLanguage) ? "ko" : _settings.SupertonicLanguage;

    /// <summary>
    /// The voices actually on disk, read from <c>voice_styles/</c>. SuperTonic's styles are
    /// speaker embeddings with no language binding, so every voice can speak every supported
    /// language; the watch lists exactly these in its Settings screen rather than a hardcoded
    /// set.
    /// </summary>
    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            var dir = Path.Combine(_modelDir, SuperTonicModelStore.VoiceStylesSubdir);
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Voice output is on when Settings → Voice has Supertonic selected as the TTS provider
    /// <i>and</i> the bundle is installed. Any other provider (Windows SAPI, OpenAI) is a
    /// desktop-only path — neither produces the raw samples this has to resample and frame.
    /// </summary>
    public bool Available =>
        string.Equals(_settings.TtsProvider, TtsProviderNames.Supertonic, StringComparison.OrdinalIgnoreCase) &&
        SuperTonicModelStore.IsModelPresent(_modelDir);

    /// <summary>Why voice is off, for the startup line the panel shows.</summary>
    public string Status =>
        !string.Equals(_settings.TtsProvider, TtsProviderNames.Supertonic, StringComparison.OrdinalIgnoreCase)
            ? $"Settings → Voice → TTS is '{_settings.TtsProvider}'; the watch needs Supertonic"
            : Available
                ? $"Supertonic {VoiceId}, {Steps} steps, {LanguageId} ({_modelDir})"
                : $"model not installed at {_modelDir} — Settings → Voice → Download Model";

    private int Steps => Math.Clamp(_settings.SupertonicSteps, 5, 12);
    private float Speed => Math.Clamp(_settings.SupertonicSpeed, 0.7f, 2.0f);

    /// <summary>
    /// Synthesize and frame. Blocking and CPU-heavy (flow matching over N steps), so callers
    /// run it off the actor thread.
    /// </summary>
    public Speech Synthesize(string text, int requestId, string? voice = null, string? language = null,
        CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException($"voice unavailable: {Status}");

        var synth = EnsureLoaded();
        // Per-request overrides come from the watch's Settings screen; the app's own Voice
        // settings are the fallback.
        var voiceId = string.IsNullOrWhiteSpace(voice) ? VoiceId : voice!;
        var lang = string.IsNullOrWhiteSpace(language) ? LanguageId : language!;
        var style = Style(voiceId);
        ct.ThrowIfCancellationRequested();

        var samples = synth.Synthesize(text, lang, style, Steps, Speed);

        var pcm = DeviceAudio.ToPcm16k(samples, synth.SampleRate);
        var blocks = ImaAdpcm.EncodeBlocks(pcm, DeviceAudio.SamplesPerBlock);

        var frames = new List<byte[]>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++) frames.Add(DeviceAudio.SpeechFrame(requestId, i, blocks[i]));

        return new Speech(frames, DeviceAudio.DurationMs(pcm), DeviceAudio.TargetRate, pcm);
    }

    private SuperTonicSynthesizer EnsureLoaded()
    {
        if (_synth is not null) return _synth;
        lock (_lock)
        {
            if (_synth is not null) return _synth;
            _log("info", $"loading Supertonic from {_modelDir}");
            var started = DateTime.UtcNow;
            _synth = SuperTonicSynthesizer.Load(_modelDir);
            _log("info", $"Supertonic ready in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms, " +
                         $"{_synth.SampleRate} Hz");
            return _synth;
        }
    }

    private SuperTonicStyle Style(string voice)
    {
        lock (_lock)
        {
            if (_styles.TryGetValue(voice, out var cached)) return cached;

            var path = SuperTonicModelStore.VoiceStylePath(_modelDir, voice);
            if (!File.Exists(path))
                throw new InvalidOperationException($"voice style '{voice}' not found at {path}");

            var style = SuperTonicStyle.Load(path);
            _styles[voice] = style;
            return style;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _synth?.Dispose();
            _synth = null;
            _styles.Clear();
        }
    }
}
