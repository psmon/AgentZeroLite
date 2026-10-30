using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Agent.Common.Llm;
using Agent.Common.Voice.KittenTts2.TextNorm;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Agent.Common.Voice.KittenTts2;

/// <summary>
/// KittenTTS 2 on AgentZero's own runtimes, text to 24 kHz samples — no Python, no PyTorch:
/// <list type="number">
///   <item>text → kittenml's normalization (<see cref="KittenText"/>: a C# port of kitten_text_processing's FST
///   grammars, identical to Python on 78/78 test texts) → ≤380-character chunks (<see cref="KittenTts2Text"/>);</item>
///   <item>each chunk → prompt (kittenml prompt.py: reference-role layout with the voice's transcript and codec
///   tokens, <c>final_seg</c>), tokenized by llama.cpp from the GGUF's own Qwen2 vocabulary;</item>
///   <item>the speech LM (Qwen3, Q8_0 GGUF) on llama.cpp — Vulkan when the process loaded those natives — fed
///   <b>embeddings</b>, not ids, because position 0 carries the speaker (<c>spk_proj</c>) instead of a token; every
///   row is scaled by the bundle's alpha (the residual stream is carried at 1/256 so f16 activations cannot
///   overflow — an exact rewrite, see the bundle's NOTICE);</item>
///   <item>sampling as kittenml does it: token-run penalty, repetition penalty over the last 50 generated tokens
///   (end/stop exempt), temperature 0.8 / top-k 50 / top-p 0.8;</item>
///   <item>audio ids → chatterbox-turbo's conditional decoder (ONNX, CPU) with the voice's precomputed
///   conditioning → chunks joined.</item>
/// </list>
/// Measured on a Radeon 8060S (Vulkan): ~28 audio tokens/s against ~24.5 needed for real time; the decoder takes
/// ~0.5 s per second of audio on the CPU. One instance holds one llama context, so calls are serialised.
/// </summary>
public sealed class KittenTts2Synthesizer : IDisposable
{
    public const int SampleRate = 24000;
    private const int Hidden = 2048, SpkDim = 512, Silence = 4299, CodecVocab = 6561;
    private const int ContextSize = 2048, Batch = 512, MaxNewTokens = 1000;
    private const string EmotionControl = "{emo: 1}";

    private readonly string _dir;
    private readonly KittenTts2Manifest _manifest;
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _ctx;
    private readonly InferenceSession _decoder;
    private readonly KittenText _text;
    private readonly FileStream _table;
    private readonly float[] _w0, _b0, _lnW, _lnB;
    private readonly JsonNode _tokens;
    private readonly Dictionary<string, Voice> _voices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>True when the LM runs on the GPU.</summary>
    public bool OnGpu { get; }

    /// <summary>Where the LM ended up and why, for logs and status lines ("GPU (Vulkan)", "CPU", "CPU — …").</summary>
    public string Device { get; private set; } = "";

    private sealed record Voice(int[] TranscriptIds, int[] ReferenceTokens, float[] SpeakerRow,
        long[] DecoderPromptTokens, float[] DecoderEmbedding, float[] DecoderFeatures, int DecoderFrames);

    private KittenTts2Synthesizer(string dir, KittenTts2Manifest manifest, LLamaWeights weights, LLamaContext ctx,
        InferenceSession decoder, KittenText text, bool onGpu)
    {
        _dir = dir; _manifest = manifest; _weights = weights; _ctx = ctx; _decoder = decoder; _text = text; OnGpu = onGpu;
        var assets = Path.Combine(dir, "assets");
        _table = new FileStream(Path.Combine(assets, "embed_tokens.f16"), FileMode.Open, FileAccess.Read, FileShare.Read);
        _w0 = ReadF32(Path.Combine(assets, "spk_proj.w0.f32"));
        _b0 = ReadF32(Path.Combine(assets, "spk_proj.b0.f32"));
        _lnW = ReadF32(Path.Combine(assets, "spk_proj.ln_w.f32"));
        _lnB = ReadF32(Path.Combine(assets, "spk_proj.ln_b.f32"));
        _tokens = JsonNode.Parse(File.ReadAllText(Path.Combine(assets, "token_map.json")))!;
    }

    /// <summary>
    /// Load the bundle with the LM on <paramref name="device"/> (<see cref="Kitten2Devices"/>): Auto = the GPU when a
    /// Vulkan device exists, else the CPU.
    /// <para>llama.cpp's natives load once per process and the GUI's local LLM shares them, so the CPU choice does
    /// not load the CPU-only natives when a Vulkan device exists — it loads the Vulkan build and offloads no layers,
    /// which leaves the local LLM free to use the GPU later. The CPU-only natives are for machines without a Vulkan
    /// device, where the Vulkan build may not even load. A GPU load that fails falls back to the CPU and says so.</para>
    /// </summary>
    public static KittenTts2Synthesizer Load(string dir, string device = Kitten2Devices.Auto)
    {
        var manifest = KittenTts2ModelStore.ReadManifest(dir)
                       ?? throw new InvalidOperationException($"KittenTTS 2 is not installed at {dir}");
        device = Kitten2Devices.Normalize(device);
        var hasVulkan = HasVulkanDevice();
        // If this loads llama.cpp before the local LLM does, it must set the environment the LLM would have set
        // (its GPU choice, the NVIDIA bf16 workaround): ggml-vulkan reads it once, when the natives load.
        if (hasVulkan) LlmService.PrepareVulkanEnvironment(LlmSettingsStore.Load());
        var backend = LlamaSharpLocalLlm.EnsureNative(hasVulkan ? LocalLlmBackend.Vulkan : LocalLlmBackend.Cpu);
        var wantGpu = device != Kitten2Devices.Cpu && hasVulkan;
        var onGpu = wantGpu && backend == LocalLlmBackend.Vulkan;
        var why = device switch
        {
            Kitten2Devices.Cpu => "CPU",
            _ when !hasVulkan => device == Kitten2Devices.Gpu ? "CPU — no Vulkan device found" : "CPU (no Vulkan device)",
            _ when !onGpu => "CPU — llama.cpp's CPU natives were already loaded in this process",
            _ => "GPU (Vulkan)",
        };

        var p = new ModelParams(Directory.EnumerateFiles(Path.Combine(dir, "gguf"), "*.gguf").Single())
        {
            ContextSize = ContextSize,
            BatchSize = Batch,
            UBatchSize = Batch,
            GpuLayerCount = onGpu ? 99 : 0,
        };
        LLamaWeights weights;
        LLamaContext ctx;
        try
        {
            weights = LLamaWeights.LoadFromFile(p);
            ctx = weights.CreateContext(p);
        }
        catch (Exception ex) when (onGpu)
        {
            // Out of video memory, a driver that refuses the device: speak slowly rather than not at all.
            AppLogger.Log($"[KittenTTS2] GPU load failed, using the CPU: {ex.Message}");
            p.GpuLayerCount = 0;
            onGpu = false;
            why = $"CPU — the GPU load failed ({ex.Message})";
            weights = LLamaWeights.LoadFromFile(p);
            ctx = weights.CreateContext(p);
        }
        var decoder = new InferenceSession(Path.Combine(dir, manifest.Decoder));
        var text = new KittenText(new Normalizer(Path.Combine(dir, "assets", "textnorm", "en")));
        return new KittenTts2Synthesizer(dir, manifest, weights, ctx, decoder, text, onGpu) { Device = why };
    }

    private static bool HasVulkanDevice()
    {
        try { return VulkanDeviceEnumerator.Enumerate().Count > 0; }
        catch { return false; }
    }

    /// <summary>What the model will hear for <paramref name="text"/> (kittenml's normalize_text).</summary>
    public string Normalize(string text) => _text.Normalize(text);

    /// <summary>Where the last <see cref="Synthesize"/> spent its time, for logs: normalize / prefill / LM / decode.</summary>
    public string LastTiming { get; private set; } = "";

    public float[] Synthesize(string text, string voice, CancellationToken ct = default, int? seed = null)
    {
        lock (_lock)
        {
            var sw = Stopwatch.StartNew();
            var v = LoadVoice(voice);
            var spoken = _text.Normalize(text);
            var chunks = KittenTts2Text.Split(spoken);
            if (chunks.Count == 0) throw new ArgumentException("no text to speak");
            var emotion = KittenText.HasExpressionTag(text) ? Tok(EmotionControl) : [];
            var rng = seed is null ? new Random() : new Random(seed.Value);
            var normalizeMs = sw.Elapsed.TotalMilliseconds;
            double prefillMs = 0, lmMs = 0, decodeMs = 0;
            var tokens = 0;
            var wavs = new List<float[]>();
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var budget = chunks.Count == 1
                    ? MaxNewTokens
                    : Math.Max(200, Math.Min(MaxNewTokens, (int)(chunk.Length / 20.0 * 25 * 1.8)));
                var audio = Generate(v, chunk, emotion, budget, rng, ct, out var p, out var g);
                prefillMs += p;
                lmMs += g;
                tokens += audio.Count;
                if (audio.Count == 0) continue;
                var d = Stopwatch.StartNew();
                wavs.Add(Decode(v, audio));
                decodeMs += d.Elapsed.TotalMilliseconds;
            }
            var joined = KittenTts2Text.Join(wavs, SampleRate);
            if (joined.Length == 0) throw new InvalidOperationException("the model produced no audio tokens");
            LastTiming = $"{joined.Length / (double)SampleRate:0.0} s audio in {sw.Elapsed.TotalSeconds:0.0} s: " +
                         $"normalize {normalizeMs:0} ms, prefill {prefillMs:0} ms, LM {tokens} tok {lmMs:0} ms " +
                         $"({tokens / Math.Max(0.001, lmMs / 1000):0.0} tok/s), decode {decodeMs:0} ms";
            return joined;
        }
    }

    private int T(string key) => (int)_tokens[key]!;

    private int[] Tok(string s) => _weights.Tokenize(s, false, false, Encoding.UTF8).Select(t => (int)t).ToArray();

    private List<long> Generate(Voice v, string chunk, int[] emotion, int budget, Random rng, CancellationToken ct,
        out double prefillMs, out double lmMs)
    {
        var audioBase = T("audio_id_base");
        var audioEnd = audioBase + T("num_audio_tokens");
        int[] stops = [T("speech_end_id"), T("stop_id")];

        var prompt = new List<int> { T("start_id"), T("reference_text_start_id") };
        prompt.AddRange(v.TranscriptIds);
        prompt.Add(T("reference_text_end_id"));
        prompt.Add(T("reference_speech_start_id"));
        prompt.AddRange(v.ReferenceTokens.Select(t => audioBase + t));
        prompt.Add(T("reference_speech_end_id"));
        prompt.AddRange(emotion);
        prompt.Add(T("text_start_id"));
        prompt.AddRange(Tok(chunk));
        prompt.Add(T("final_seg_id"));
        prompt.Add(T("speech_start_id"));
        if (prompt.Count + budget > ContextSize) budget = ContextSize - prompt.Count;

        var timer = Stopwatch.StartNew();
        _ctx.NativeHandle.MemoryClear();
        var alpha = _manifest.Alpha;
        var batch = new LLamaBatchEmbeddings(Hidden);
        float[] logits = [];
        for (var start = 0; start < prompt.Count; start += Batch)
        {
            batch.Clear();
            var end = Math.Min(prompt.Count, start + Batch);
            for (var i = start; i < end; i++)
                batch.Add(i == 0 ? Scaled(v.SpeakerRow, alpha) : Row(prompt[i], alpha), i, LLamaSeqId.Zero, i == prompt.Count - 1);
            Check(_ctx.Decode(batch));
        }
        logits = _ctx.NativeHandle.GetLogitsIth(batch.EmbeddingsCount - 1).ToArray();
        prefillMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();

        var seq = new List<int>(prompt);
        var audio = new List<long>();
        for (var n = 0; n < budget; n++)
        {
            ct.ThrowIfCancellationRequested();
            var tok = Pick(seq, logits, prompt.Count, audioBase, audioEnd, stops, rng);
            seq.Add(tok);
            if (stops.Contains(tok)) break;
            if (tok >= audioBase && tok < audioEnd) audio.Add(tok - audioBase);
            if (seq.Count >= ContextSize) break;
            batch.Clear();
            batch.Add(Row(tok, alpha), seq.Count - 1, LLamaSeqId.Zero, true);
            Check(_ctx.Decode(batch));
            logits = _ctx.NativeHandle.GetLogitsIth(0).ToArray();
        }
        lmMs = timer.Elapsed.TotalMilliseconds;
        return audio;
    }

    private float[] Decode(Voice v, List<long> audio)
    {
        var tokens = v.DecoderPromptTokens.Concat(audio.Where(t => t is >= 0 and < CodecVocab))
            .Concat([Silence, Silence, Silence]).ToArray();
        using var results = _decoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("speech_tokens", new DenseTensor<long>(tokens, [1, tokens.Length])),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings", new DenseTensor<float>(v.DecoderEmbedding, [1, v.DecoderEmbedding.Length])),
            NamedOnnxValue.CreateFromTensor("speaker_features", new DenseTensor<float>(v.DecoderFeatures, [1, v.DecoderFrames, 80])),
        ]);
        return results.First().AsTensor<float>().ToArray();
    }

    private static int Pick(List<int> s, float[] scores, int promptLength, int audioBase, int audioEnd, int[] stops, Random rng)
    {
        var last = s[^1];
        var run = 0;
        for (var i = s.Count - 1; i >= 0 && s[i] == last; i--) run++;
        if (last >= audioBase && last < audioEnd && run >= 10) scores[last] -= (run - 10 + 1) * 1.3f;
        var keep = stops.Select(t => scores[t]).ToArray();
        foreach (var t in s.Skip(Math.Max(promptLength, s.Count - 50)).Distinct())
            scores[t] = scores[t] < 0 ? scores[t] * 1.1f : scores[t] / 1.1f;
        for (var i = 0; i < stops.Length; i++) scores[stops[i]] = keep[i];

        var top = new List<int>(51);
        for (var i = 0; i < scores.Length; i++)
        {
            if (top.Count == 50 && scores[i] <= scores[top[^1]]) continue;
            var at = top.FindIndex(j => scores[i] > scores[j]);
            top.Insert(at < 0 ? top.Count : at, i);
            if (top.Count > 50) top.RemoveAt(50);
        }
        var probs = top.Select(i => Math.Exp((scores[i] - scores[top[0]]) / 0.8)).ToArray();
        var sum = probs.Sum();
        double cum = 0;
        var cut = 0;
        while (cut < probs.Length) { cum += probs[cut] / sum; cut++; if (cum >= 0.8) break; }
        var r = rng.NextDouble() * probs.Take(cut).Sum();
        for (var i = 0; i < cut; i++) { r -= probs[i]; if (r <= 0) return top[i]; }
        return top[cut - 1];
    }

    private readonly byte[] _rowBytes = new byte[Hidden * 2];

    private float[] Row(int id, float alpha)
    {
        _table.Position = (long)id * Hidden * 2;
        _table.ReadExactly(_rowBytes);
        var r = new float[Hidden];
        for (var i = 0; i < Hidden; i++) r[i] = (float)BitConverter.ToHalf(_rowBytes, i * 2) * alpha;
        return r;
    }

    private static float[] Scaled(float[] row, float alpha) => row.Select(x => x * alpha).ToArray();

    private Voice LoadVoice(string name)
    {
        if (_voices.TryGetValue(name, out var cached)) return cached;
        var dir = Path.Combine(_dir, "assets", "voices");
        var json = Path.Combine(dir, $"{name}.json");
        if (!File.Exists(json)) throw new InvalidOperationException($"KittenTTS 2 has no voice '{name}' in {dir}");
        var v = JsonNode.Parse(File.ReadAllText(json))!;
        var voice = new Voice(
            Tok((string)v["transcript"]!),
            v["reference_tokens"]!.AsArray().Select(n => (int)n!).ToArray(),
            SpkProj(v["speaker_embedding"]!.AsArray().Select(n => (float)(double)n!).ToArray()),
            v["decoder_prompt_tokens"]!.AsArray().Select(n => (long)(int)n!).ToArray(),
            v["decoder_embedding"]!.AsArray().Select(n => (float)(double)n!).ToArray(),
            ReadF32(Path.Combine(dir, $"{name}.feat.f32")),
            (int)v["decoder_frames"]!);
        _voices[name] = voice;
        return voice;
    }

    /// <summary>spk_proj: Linear(512 → 2048) + LayerNorm(eps 1e-5).</summary>
    private float[] SpkProj(float[] e)
    {
        var h = new float[Hidden];
        for (var o = 0; o < Hidden; o++)
        {
            double acc = _b0[o];
            for (var i = 0; i < SpkDim; i++) acc += _w0[o * SpkDim + i] * e[i];
            h[o] = (float)acc;
        }
        var mean = h.Average();
        var variance = h.Select(x => (x - mean) * (x - mean)).Average();
        var inv = 1 / Math.Sqrt(variance + 1e-5);
        for (var o = 0; o < Hidden; o++) h[o] = (float)((h[o] - mean) * inv * _lnW[o] + _lnB[o]);
        return h;
    }

    private static float[] ReadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }

    private static void Check(DecodeResult r)
    {
        if (r != DecodeResult.Ok) throw new InvalidOperationException($"llama_decode: {r}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _ctx.Dispose();
            _weights.Dispose();
            _decoder.Dispose();
            _table.Dispose();
        }
    }
}
