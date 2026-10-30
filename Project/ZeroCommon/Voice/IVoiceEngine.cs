using Agent.Common.Voice.KittenTts2;

namespace Agent.Common.Voice;

/// <summary>Synthesized speech before any container: float PCM in [-1, 1] and its rate.</summary>
public sealed record VoiceSamples(float[] Samples, int SampleRate);

/// <summary>
/// An on-device voice as both voice consumers need it: the desktop (wrapped as an <see cref="ITextToSpeech"/> by
/// <see cref="VoiceEngineTts"/>) and the wearable host, which resamples the raw samples to 16 kHz ADPCM for the
/// watch. Before this, the wearable host carried its own copy of each engine's model handling; now an engine is
/// written once — Supertonic and KittenTTS 2 — and both sides pick it with
/// <see cref="VoiceEngines.Create"/> from the same <see cref="VoiceSettings"/>.
///
/// <para>Engines load their models lazily on the first <see cref="Synthesize"/> (or <see cref="Warm"/>) and hold
/// them <b>once per process</b>: an engine object is cheap, the loaded model is shared by every engine for the same
/// model, and only <see cref="VoiceEngines.ReleaseAllExcept"/> gives the memory back. Synthesis is blocking and
/// CPU/GPU-heavy, so callers run it off the UI and actor threads. An engine never downloads.</para>
/// </summary>
public interface IVoiceEngine : IDisposable
{
    /// <summary>Provider name as stored in <see cref="VoiceSettings.TtsProvider"/>.</summary>
    string Name { get; }

    /// <summary>The model files are installed and this OS can run them.</summary>
    bool Available { get; }

    /// <summary>One line for a status panel: what will speak, or why nothing will.</summary>
    string Status { get; }

    /// <summary>The model is in memory now — the next <see cref="Synthesize"/> pays no load.</summary>
    bool IsLoaded { get; }

    /// <summary>Load the model now if it is installed (blocking; run it on the pool). A no-op when already loaded.</summary>
    void Warm();

    /// <summary>Voice ids a request may name. Empty when <see cref="Available"/> is false.</summary>
    IReadOnlyList<string> Voices { get; }

    /// <summary>Settings → Voice's voice for this engine.</summary>
    string DefaultVoice { get; }

    /// <summary>The language answers should be written in for this voice (the wearable brain answers in it).</summary>
    string DefaultLanguage { get; }

    /// <summary>
    /// Speak <paramref name="text"/>. A null or unknown <paramref name="voice"/> falls back to
    /// <see cref="DefaultVoice"/> (the watch remembers the list another engine gave it); the
    /// <paramref name="language"/> is a hint an engine may ignore.
    /// </summary>
    VoiceSamples Synthesize(string text, string? voice = null, string? language = null, CancellationToken ct = default);
}

/// <summary>Picks the engine Settings → Voice names.</summary>
public static class VoiceEngines
{
    /// <summary>The providers backed by an <see cref="IVoiceEngine"/>.</summary>
    public static readonly IReadOnlyList<string> OnDevice =
        [TtsProviderNames.Supertonic, TtsProviderNames.KittenTts2];

    /// <summary>
    /// The engine for <see cref="VoiceSettings.TtsProvider"/>, or null for a provider that has none (Off, Windows SAPI,
    /// OpenAI — none of them produces raw samples on this machine). <paramref name="log"/> gets (level, message).
    /// </summary>
    public static IVoiceEngine? Create(VoiceSettings s, Action<string, string>? log = null)
    {
        log ??= (_, _) => { };
        return s.TtsProvider switch
        {
            TtsProviderNames.Supertonic => new SupertonicVoiceEngine(s, log),
            TtsProviderNames.KittenTts2 => new KittenTts2VoiceEngine(s, log),
            _ => null,
        };
    }

    /// <summary>
    /// Free every engine's loaded model except <paramref name="keep"/>'s (a provider name) — what a Save does when the
    /// voice changes, so the previous model's memory comes back.
    /// </summary>
    public static void ReleaseAllExcept(string? keep)
    {
        if (keep != TtsProviderNames.Supertonic) SupertonicVoiceEngine.Release();
        if (keep != TtsProviderNames.KittenTts2) KittenTts2VoiceEngine.Release();
    }

    /// <summary>The first voice in <paramref name="voices"/> equal to <paramref name="wanted"/>, ignoring case.</summary>
    public static string? Match(IReadOnlyList<string> voices, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) ? null
            : voices.FirstOrDefault(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An <see cref="IVoiceEngine"/> as the desktop's <see cref="ITextToSpeech"/>: WAV at the engine's own rate.</summary>
public sealed class VoiceEngineTts(IVoiceEngine engine) : ITextToSpeech, IAsyncDisposable
{
    public IVoiceEngine Engine => engine;
    public string ProviderName => engine.Name;
    public string AudioFormat => "wav";

    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default) => Task.FromResult(engine.Voices);

    public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!engine.Available) throw new InvalidOperationException($"{engine.Name}: {engine.Status}");
            var v = engine.Synthesize(text, voice, null, ct);
            return WavPcm.ToWav(new WavPcm.Decoded(v.Samples, v.SampleRate));
        }, ct);

    public ValueTask DisposeAsync()
    {
        engine.Dispose();
        return ValueTask.CompletedTask;
    }
}
