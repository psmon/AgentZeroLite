namespace Agent.Common.Voice.KittenTts2;

/// <summary>
/// KittenTTS 2 as an <see cref="IVoiceEngine"/>: multilingual (Korean included) on-device speech from the bundle in
/// <see cref="KittenTts2ModelStore"/>. Windows only for now — the speech LM runs on the app's self-built llama.cpp
/// natives, which ship for win-x64.
///
/// <para>The model (~3.3 GB, ~13 s to load) is held <b>once per process</b>, not per engine: the Voice tab's test,
/// a plugin's speak call and the wearable host each build a cheap engine object, and every one of them reuses the
/// loaded synthesizer while the bundle folder and device stay the same. Measured before this: the Voice tab built
/// and disposed an engine per click, so every test paid the full load. <see cref="Release"/> frees it.</para>
/// </summary>
public sealed class KittenTts2VoiceEngine : IVoiceEngine
{
    private static readonly object SharedLock = new();
    private static readonly object LoadLock = new();
    private static (string Key, KittenTts2Synthesizer Synth)? _shared;

    private readonly VoiceSettings _settings;
    private readonly Action<string, string> _log;

    public KittenTts2VoiceEngine(VoiceSettings settings, Action<string, string>? log = null)
    {
        _settings = settings;
        _log = log ?? ((_, _) => { });
        ModelDirectory = KittenTts2ModelStore.ResolveDir(settings);
    }

    public string Name => TtsProviderNames.KittenTts2;
    public string ModelDirectory { get; }
    private string Device => Kitten2Devices.Normalize(_settings.Kitten2Device);
    private string Key => $"{Path.GetFullPath(ModelDirectory)}|{Device}";
    public bool Available => OperatingSystem.IsWindows() && KittenTts2ModelStore.IsPresent(ModelDirectory);

    public bool IsLoaded
    {
        get { lock (SharedLock) return _shared?.Key == Key; }
    }

    public string Status =>
        !OperatingSystem.IsWindows() ? "KittenTTS 2 runs on Windows only in this build"
        : Available ? $"KittenTTS 2 {DefaultVoice}, {DefaultLanguage}, " +
                      $"{(Loaded() is { } s ? s.Device : $"device {Device}, not loaded yet")} ({ModelDirectory})"
        : $"KittenTTS 2 not installed at {ModelDirectory} — Settings → Voice → KittenTTS 2 → Download";

    public IReadOnlyList<string> Voices => KittenTts2ModelStore.Voices(ModelDirectory);

    public string DefaultVoice => VoiceEngines.Match(Voices, _settings.Kitten2Voice)
                                  ?? Voices.FirstOrDefault() ?? KittenTts2ModelStore.DefaultVoice;

    public string DefaultLanguage => string.IsNullOrWhiteSpace(_settings.Kitten2Language) ? "ko" : _settings.Kitten2Language;

    public void Warm()
    {
        if (Available) EnsureLoaded();
    }

    public VoiceSamples Synthesize(string text, string? voice = null, string? language = null, CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException($"voice unavailable: {Status}");
        var synth = EnsureLoaded();
        var samples = synth.Synthesize(text, VoiceEngines.Match(Voices, voice) ?? DefaultVoice, ct);
        _log("info", $"KittenTTS 2: {synth.LastTiming}");
        return new VoiceSamples(samples, KittenTts2Synthesizer.SampleRate);
    }

    private KittenTts2Synthesizer? Loaded()
    {
        lock (SharedLock) return _shared?.Key == Key ? _shared.Value.Synth : null;
    }

    /// <summary>The process-wide synthesizer for this bundle + device; a different one is released first.</summary>
    private KittenTts2Synthesizer EnsureLoaded()
    {
        if (Loaded() is { } ready) return ready;
        // One load at a time; SharedLock is held only for the swap, so a status query never waits ~13 s for a load.
        lock (LoadLock)
        {
            if (Loaded() is { } raced) return raced;
            lock (SharedLock)
            {
                _shared?.Synth.Dispose();
                _shared = null;
            }
            _log("info", $"loading KittenTTS 2 from {ModelDirectory} (device {Device})");
            var started = DateTime.UtcNow;
            var synth = KittenTts2Synthesizer.Load(ModelDirectory, Device);
            _log("info", $"KittenTTS 2 ready in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms, LM on {synth.Device}");
            lock (SharedLock) _shared = (Key, synth);
            return synth;
        }
    }

    /// <summary>Free the loaded model (memory back) — when another voice is chosen, or on request.</summary>
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
