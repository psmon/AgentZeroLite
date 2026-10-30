using System.Buffers.Binary;
using SherpaOnnx;

namespace Agent.Common.Voice.Streaming;

/// <summary>Knobs of the streaming recognizer that are not the model.</summary>
/// <param name="EndpointSilenceSeconds">
/// Silence after speech that closes an utterance (sherpa rule 2). This is what replaces the VAD hangover —
/// 1.2 s (Loose) / 2.0 s (Strict) there, 0.8 s here, and the text is already decoded when it fires.
/// </param>
/// <param name="MaxUtteranceSeconds">Force a final after this much continuous speech (rule 3).</param>
/// <param name="NumThreads">ONNX Runtime intra-op threads for the encoder.</param>
public sealed record StreamingSttOptions(
    float EndpointSilenceSeconds = 0.8f,
    float MaxUtteranceSeconds = 20f,
    int NumThreads = 2);

/// <summary>
/// <see cref="IStreamingSpeechToText"/> over sherpa-onnx's <c>OnlineRecognizer</c> — every entry of
/// <see cref="StreamingSttModelCatalog"/> is a streaming transducer it runs on the CPU.
///
/// <para>The recognizer is held once per process per (model, options), like the voice engines'
/// models (<c>ModelResidency</c>: load on first use, keep). This object is cheap; disposing it keeps the
/// model.</para>
/// </summary>
public sealed class SherpaStreamingStt : IStreamingSpeechToText
{
    private static readonly Dictionary<string, OnlineRecognizer> Recognizers = new();
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    private readonly StreamingSttModel _model;
    private readonly StreamingSttOptions _options;
    private OnlineRecognizer? _recognizer;

    public SherpaStreamingStt(StreamingSttModel model, StreamingSttOptions? options = null)
    {
        _model = model;
        _options = options ?? new StreamingSttOptions();
    }

    public string ProviderName => SttProviderNames.SherpaStreaming;
    public string ModelId => _model.Id;

    private string CacheKey => $"{_model.Id}|{_options.EndpointSilenceSeconds}|{_options.MaxUtteranceSeconds}|{_options.NumThreads}";

    public async Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_recognizer is not null) return true;
        var files = StreamingSttModelCatalog.FindFiles(_model);
        if (files is null)
        {
            progress?.Report($"{_model.Label} is not installed — Settings → Voice → Download.");
            return false;
        }

        await LoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Recognizers.TryGetValue(CacheKey, out var cached))
            {
                _recognizer = cached;
                return true;
            }
            progress?.Report($"Loading {_model.Label}…");
            var started = DateTime.UtcNow;
            var recognizer = await Task.Run(() => new OnlineRecognizer(BuildConfig(files)), ct).ConfigureAwait(false);
            Recognizers[CacheKey] = recognizer;
            _recognizer = recognizer;
            progress?.Report($"{_model.Label} ready ({(DateTime.UtcNow - started).TotalSeconds:F1} s).");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report($"{_model.Label} failed to load: {ex.Message}");
            return false;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private OnlineRecognizerConfig BuildConfig(StreamingSttModelFiles files)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.NumThreads = Math.Max(1, _options.NumThreads);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = _model.DecodingMethod;
        config.MaxActivePaths = 4;
        config.EnableEndpoint = 1;
        // Rule 1: nothing said yet — only give up on a long silence, it costs nothing to keep listening.
        config.Rule1MinTrailingSilence = 2.4f;
        config.Rule2MinTrailingSilence = _options.EndpointSilenceSeconds;
        config.Rule3MinUtteranceLength = _options.MaxUtteranceSeconds;
        return config;
    }

    public IStreamingSttSession StartSession(string language = "auto")
    {
        var recognizer = _recognizer
            ?? throw new InvalidOperationException("EnsureReadyAsync must succeed before StartSession.");
        var stream = recognizer.CreateStream();
        // Nemotron 3.5 picks its language per stream; empty / "auto" leaves it to the model's own detection.
        if (_model.Multilingual && !string.IsNullOrWhiteSpace(language) && language != "auto")
            stream.SetOption("language", language);
        return new Session(recognizer, stream);
    }

    public void Dispose() => _recognizer = null;

    /// <summary>
    /// sherpa-onnx joins tokens and drops the space between two CJK characters — right for Chinese, wrong for
    /// Korean, which it counts among them. Measured on both the Korean Zipformer and Nemotron 3.5:
    /// "지하철에서다리를벌리고앉지마라." while the tokens read [ 지하철][에서][ 다리][를]… — sherpa has already turned
    /// SentencePiece's word marker (▁) into a leading space there. So the words are rebuilt from the tokens, but only
    /// when they spell exactly the text sherpa gave (byte-fallback pieces like &lt;0xEC&gt; would not).
    /// </summary>
    internal static string Render(OnlineRecognizerResult result) => Render(result.Text, result.Tokens);

    internal static string Render(string text, string[]? tokens)
    {
        if (tokens is null || tokens.Length == 0) return text.Trim();
        var joined = string.Concat(tokens).Replace('▁', ' ');
        if (!joined.Contains(' ')) return text.Trim();
        static string Bare(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (Bare(joined) != Bare(text)) return text.Trim();
        return string.Join(' ', joined.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class Session(OnlineRecognizer recognizer, OnlineStream stream) : IStreamingSttSession
    {
        private const int SampleRate = 16000;
        private string _last = "";
        private int _segment;
        private long _segmentSamples;
        private bool _finished;

        public IReadOnlyList<SttUpdate> Accept(ReadOnlySpan<byte> pcm16k)
        {
            if (_finished || pcm16k.Length < 2) return [];
            var samples = new float[pcm16k.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16k.Slice(i * 2, 2)) / 32768f;
            stream.AcceptWaveform(SampleRate, samples);
            _segmentSamples += samples.Length;
            return Drain();
        }

        public SttUpdate? Finish()
        {
            if (_finished) return null;
            // Trailing silence lets the last chunk through the encoder (k2-fsa's examples pad the same way).
            stream.AcceptWaveform(SampleRate, new float[SampleRate * 2 / 3]);
            stream.InputFinished();
            var updates = Drain();
            _finished = true;
            var final = updates.LastOrDefault(u => u.IsFinal);
            if (final is not null) return final;
            var text = Render(recognizer.GetResult(stream));
            return text.Length == 0 ? null : new SttUpdate(text, true, _segment, Seconds);
        }

        private double Seconds => _segmentSamples / (double)SampleRate;

        private List<SttUpdate> Drain()
        {
            var updates = new List<SttUpdate>(2);
            while (recognizer.IsReady(stream)) recognizer.Decode(stream);
            var text = Render(recognizer.GetResult(stream));
            if (recognizer.IsEndpoint(stream))
            {
                if (text.Length > 0) updates.Add(new SttUpdate(text, true, _segment, Seconds));
                recognizer.Reset(stream);
                if (text.Length > 0) _segment++;
                _segmentSamples = 0;
                _last = "";
            }
            else if (text != _last)
            {
                _last = text;
                if (text.Length > 0) updates.Add(new SttUpdate(text, false, _segment, Seconds));
            }
            return updates;
        }

        public void Dispose() => stream.Dispose();
    }
}
