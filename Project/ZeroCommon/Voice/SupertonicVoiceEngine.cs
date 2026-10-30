namespace Agent.Common.Voice;

/// <summary>
/// Supertone's Supertonic-3 (ONNX, 44.1 kHz, 31 languages incl. Korean) as an <see cref="IVoiceEngine"/> — the
/// bundle from Settings → Voice, the voice / steps / speed / language picked there, and the voices that are actually
/// in <c>voice_styles/</c>. The four ONNX sessions are held once per process (like KittenTTS 2's model), so engine
/// objects are cheap and the Voice tab's test no longer reloads the model per click; <see cref="Release"/> frees it.
/// </summary>
public sealed class SupertonicVoiceEngine : IVoiceEngine
{
    private static readonly object SharedLock = new();
    private static readonly object LoadLock = new();
    private static (string Dir, SuperTonicSynthesizer Synth, Dictionary<string, SuperTonicStyle> Styles)? _shared;

    private readonly VoiceSettings _settings;
    private readonly Action<string, string> _log;

    public SupertonicVoiceEngine(VoiceSettings settings, Action<string, string>? log = null)
    {
        _settings = settings;
        _log = log ?? ((_, _) => { });
        ModelDirectory = SuperTonicModelStore.ResolveModelDir(settings);
    }

    public string Name => TtsProviderNames.Supertonic;
    public string ModelDirectory { get; }
    private string Key => Path.GetFullPath(ModelDirectory);
    public bool Available => SuperTonicModelStore.IsModelPresent(ModelDirectory);
    private int Steps => Math.Clamp(_settings.SupertonicSteps, 5, 12);
    private float Speed => Math.Clamp(_settings.SupertonicSpeed, 0.7f, 2.0f);

    public bool IsLoaded
    {
        get { lock (SharedLock) return _shared?.Dir == Key; }
    }

    public string Status => Available
        ? $"Supertonic {DefaultVoice}, {Steps} steps, {DefaultLanguage} ({ModelDirectory})"
        : $"model not installed at {ModelDirectory} — Settings → Voice → Download Model";

    public string DefaultVoice => string.IsNullOrWhiteSpace(_settings.SupertonicVoice) ? "F1" : _settings.SupertonicVoice;

    public string DefaultLanguage => string.IsNullOrWhiteSpace(_settings.SupertonicLanguage) ? "ko" : _settings.SupertonicLanguage;

    /// <summary>
    /// The styles on disk. Supertonic's styles are speaker embeddings with no language binding, so every voice
    /// speaks every supported language.
    /// </summary>
    public IReadOnlyList<string> Voices
    {
        get
        {
            var dir = Path.Combine(ModelDirectory, SuperTonicModelStore.VoiceStylesSubdir);
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Warm()
    {
        if (Available) EnsureLoaded();
    }

    public VoiceSamples Synthesize(string text, string? voice = null, string? language = null, CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException($"voice unavailable: {Status}");
        var (synth, styles) = EnsureLoaded();
        var voiceId = !string.IsNullOrWhiteSpace(voice) && File.Exists(SuperTonicModelStore.VoiceStylePath(ModelDirectory, voice!))
            ? voice!
            : DefaultVoice;
        var lang = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language!;
        var style = Style(styles, voiceId);
        ct.ThrowIfCancellationRequested();
        return new VoiceSamples(synth.Synthesize(text, lang, style, Steps, Speed), synth.SampleRate);
    }

    private (SuperTonicSynthesizer, Dictionary<string, SuperTonicStyle>) EnsureLoaded()
    {
        lock (SharedLock)
            if (_shared?.Dir == Key) return (_shared.Value.Synth, _shared.Value.Styles);
        // One load at a time; SharedLock is held only for the swap, so status queries never wait for a load.
        lock (LoadLock)
        {
            lock (SharedLock)
            {
                if (_shared?.Dir == Key) return (_shared.Value.Synth, _shared.Value.Styles);
                _shared?.Synth.Dispose();
                _shared = null;
            }
            _log("info", $"loading Supertonic from {ModelDirectory}");
            var started = DateTime.UtcNow;
            var synth = SuperTonicSynthesizer.Load(ModelDirectory);
            _log("info", $"Supertonic ready in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms, {synth.SampleRate} Hz");
            var styles = new Dictionary<string, SuperTonicStyle>(StringComparer.OrdinalIgnoreCase);
            lock (SharedLock) _shared = (Key, synth, styles);
            return (synth, styles);
        }
    }

    private SuperTonicStyle Style(Dictionary<string, SuperTonicStyle> styles, string voice)
    {
        lock (SharedLock)
        {
            if (styles.TryGetValue(voice, out var cached)) return cached;
            var path = SuperTonicModelStore.VoiceStylePath(ModelDirectory, voice);
            if (!File.Exists(path)) throw new InvalidOperationException($"voice style '{voice}' not found at {path}");
            var style = SuperTonicStyle.Load(path);
            styles[voice] = style;
            return style;
        }
    }

    /// <summary>Free the loaded sessions (memory back).</summary>
    public static void Release()
    {
        lock (SharedLock)
        {
            _shared?.Synth.Dispose();
            _shared = null;
        }
    }

    /// <summary>The engine object is cheap; the model stays loaded for the next one (see <see cref="Release"/>).</summary>
    public void Dispose() { }
}
