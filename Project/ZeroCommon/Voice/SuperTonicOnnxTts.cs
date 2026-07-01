namespace Agent.Common.Voice;

/// <summary>
/// Native, on-device SuperTonic-3 TTS. Runs the 4 ONNX graphs directly through
/// <c>Microsoft.ML.OnnxRuntime</c> (the same runtime the AST music classifier
/// and Sherpa diarization already ship) — <b>no Python, no pip, no subprocess</b>.
/// This replaces the M0020 <c>SuperTonicTts</c> which shelled out to a
/// user-installed <c>pip install supertonic</c> package.
///
/// 10 builtin voices (M1..M5 / F1..F5), 31 languages incl. Korean, 44.1 kHz
/// 16-bit WAV output. Models are pulled once via
/// <see cref="SuperTonicModelDownloader"/> from the Voice settings tab into
/// <see cref="SuperTonicModelStore.DefaultModelDirectory"/>; if they are absent
/// <see cref="SynthesizeAsync"/> throws a message pointing the user at that
/// Download button rather than silently hitting the network mid-call.
///
/// Sessions are built lazily on first synthesis and reused (ONNX
/// <c>InferenceSession.Run</c> is thread-safe); the actor layer
/// (<c>TtsWorkerActor</c>) calls this sequentially anyway.
/// </summary>
public sealed class SuperTonicOnnxTts : ITextToSpeech, IAsyncDisposable
{
    public string ProviderName => "Supertonic";
    public string AudioFormat => "wav";

    /// <summary>Builtin voice ids shipped with Supertonic-3 (model card).</summary>
    public static readonly string[] BuiltinVoices = SuperTonicModelStore.BuiltinVoices;

    /// <summary>ONNX denoising steps, 5..12 (default 8). Higher = better/slower.</summary>
    public int Steps { get; init; } = 8;

    /// <summary>BCP-47 short tag; unsupported tags auto-coerce to <c>na</c>.</summary>
    public string Language { get; init; } = "ko";

    /// <summary>Default voice id when a synthesis request doesn't specify one.</summary>
    public string Voice { get; init; } = "F1";

    /// <summary>Speech speed factor (0.7..2.0, default 1.05 per reference).</summary>
    public float Speed { get; init; } = 1.05f;

    private readonly string _modelDir;
    private readonly object _initLock = new();
    private SuperTonicSynthesizer? _synth;
    private readonly Dictionary<string, SuperTonicStyle> _styleCache = new();

    public SuperTonicOnnxTts(string modelDir)
    {
        _modelDir = string.IsNullOrWhiteSpace(modelDir)
            ? SuperTonicModelStore.DefaultModelDirectory
            : modelDir;
    }

    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(BuiltinVoices);

    public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            EnsureReady();

            var voiceId = string.IsNullOrWhiteSpace(voice) ? Voice : voice;
            if (string.IsNullOrWhiteSpace(voiceId)) voiceId = "F1";
            var style = GetStyle(voiceId);

            var steps = Math.Clamp(Steps, 5, 12);
            var speed = Math.Clamp(Speed, 0.7f, 2.0f);

            ct.ThrowIfCancellationRequested();
            var samples = _synth!.Synthesize(text, Language, style, steps, speed);
            return SuperTonicWav.ToWavBytes(samples, _synth.SampleRate);
        }, ct);
    }

    private void EnsureReady()
    {
        if (_synth is not null) return;
        lock (_initLock)
        {
            if (_synth is not null) return;
            if (!SuperTonicModelStore.IsModelPresent(_modelDir))
            {
                throw new InvalidOperationException(
                    "Supertonic model not found. Open Settings → Voice → Supertonic and click " +
                    $"\"Download Model\" (~398 MB, one-time). Expected at: {_modelDir}");
            }
            _synth = SuperTonicSynthesizer.Load(_modelDir);
        }
    }

    private SuperTonicStyle GetStyle(string voiceId)
    {
        lock (_initLock)
        {
            if (_styleCache.TryGetValue(voiceId, out var cached))
                return cached;

            var path = SuperTonicModelStore.VoiceStylePath(_modelDir, voiceId);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Voice style '{voiceId}' not found at {path}. Re-download the Supertonic " +
                    "model from Settings → Voice to fetch all 10 voice styles.");
            }
            var style = SuperTonicStyle.Load(path);
            _styleCache[voiceId] = style;
            return style;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_initLock)
        {
            _synth?.Dispose();
            _synth = null;
            _styleCache.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
