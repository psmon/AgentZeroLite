// ---------------------------------------------------------------------------
// Native SuperTonic-3 ONNX inference engine.
//
// Ported (near-verbatim) from Supertone Inc's official C# reference —
// https://github.com/supertone-inc/supertonic  (csharp/Helper.cs), MIT License,
// Copyright (c) Supertone Inc. Adapted for AgentZero Lite: namespace change,
// CLI/console I/O stripped, WAV emitted to a byte[] instead of a file, and the
// text/tokenizer/WAV pieces split out as pure static helpers so the headless
// xUnit suite can cover them without the ~398 MB model.
//
// The critical property: SuperTonic needs NO espeak-ng / g2p / Python
// phonemizer. "Tokenization" is a direct Unicode-codepoint -> id lookup through
// unicode_indexer.json, which ports cleanly to C#.
// ---------------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Agent.Common.Voice;

/// <summary>Languages Supertonic-3 accepts (BCP-47 short tags + <c>na</c> auto).</summary>
public static class SuperTonicLanguages
{
    public static readonly string[] Available =
    {
        "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi",
        "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro",
        "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na",
    };

    /// <summary>Coerce an unsupported tag to <c>na</c> (script auto-detect) rather than throwing.</summary>
    public static string Coerce(string? lang)
        => !string.IsNullOrWhiteSpace(lang) && Available.Contains(lang) ? lang! : "na";
}

/// <summary>Config parsed from <c>tts.json</c> (sample rate + latent geometry).</summary>
public sealed class SuperTonicConfig
{
    public int SampleRate { get; init; }
    public int BaseChunkSize { get; init; }
    public int ChunkCompressFactor { get; init; }
    public int LatentDim { get; init; }

    public static SuperTonicConfig Load(string onnxDir)
    {
        var json = File.ReadAllText(Path.Combine(onnxDir, "tts.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ae = root.GetProperty("ae");
        var ttl = root.GetProperty("ttl");
        return new SuperTonicConfig
        {
            SampleRate = ae.GetProperty("sample_rate").GetInt32(),
            BaseChunkSize = ae.GetProperty("base_chunk_size").GetInt32(),
            ChunkCompressFactor = ttl.GetProperty("chunk_compress_factor").GetInt32(),
            LatentDim = ttl.GetProperty("latent_dim").GetInt32(),
        };
    }
}

/// <summary>A speaker style (two flattened tensors + their shapes).</summary>
public sealed class SuperTonicStyle
{
    public float[] Ttl { get; }
    public long[] TtlShape { get; }
    public float[] Dp { get; }
    public long[] DpShape { get; }

    public SuperTonicStyle(float[] ttl, long[] ttlShape, float[] dp, long[] dpShape)
    {
        Ttl = ttl; TtlShape = ttlShape; Dp = dp; DpShape = dpShape;
    }

    /// <summary>Load a single voice-style JSON (<c>{id}.json</c>) into a batch-1 style.</summary>
    public static SuperTonicStyle Load(string voiceStylePath)
    {
        var root = JsonDocument.Parse(File.ReadAllText(voiceStylePath)).RootElement;

        var ttlDims = ParseInt64Array(root.GetProperty("style_ttl").GetProperty("dims"));
        var dpDims = ParseInt64Array(root.GetProperty("style_dp").GetProperty("dims"));

        var ttl = FlattenFloat3D(root.GetProperty("style_ttl").GetProperty("data"));
        var dp = FlattenFloat3D(root.GetProperty("style_dp").GetProperty("data"));

        return new SuperTonicStyle(
            ttl, new long[] { 1, ttlDims[1], ttlDims[2] },
            dp, new long[] { 1, dpDims[1], dpDims[2] });
    }

    private static float[] FlattenFloat3D(JsonElement element)
    {
        var flat = new List<float>();
        foreach (var batch in element.EnumerateArray())
            foreach (var row in batch.EnumerateArray())
                foreach (var val in row.EnumerateArray())
                    flat.Add(val.GetSingle());
        return flat.ToArray();
    }

    private static long[] ParseInt64Array(JsonElement element)
    {
        var result = new List<long>();
        foreach (var val in element.EnumerateArray())
            result.Add(val.GetInt64());
        return result.ToArray();
    }
}

/// <summary>
/// Pure text normalization + Unicode-codepoint tokenization. Split into static
/// methods (no ONNX, no model) so the headless suite can assert the exact
/// normalization the reference applies.
/// </summary>
public static class SuperTonicText
{
    private static readonly (string from, string to)[] SymbolReplacements =
    {
        ("–", "-"), ("‑", "-"), ("—", "-"), ("_", " "),
        ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"),
        ("´", "'"), ("`", "'"),
        ("[", " "), ("]", " "), ("|", " "), ("/", " "), ("#", " "),
        ("→", " "), ("←", " "),
    };

    private static readonly (string from, string to)[] ExprReplacements =
    {
        ("@", " at "), ("e.g.,", "for example, "), ("i.e.,", "that is, "),
    };

    /// <summary>Strip emoji code points (matches the reference's Unicode ranges).</summary>
    public static string RemoveEmojis(string text)
    {
        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                codePoint = text[i];
            }

            bool isEmoji =
                (codePoint >= 0x1F600 && codePoint <= 0x1F64F) ||
                (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) ||
                (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) ||
                (codePoint >= 0x1F700 && codePoint <= 0x1F77F) ||
                (codePoint >= 0x1F780 && codePoint <= 0x1F7FF) ||
                (codePoint >= 0x1F800 && codePoint <= 0x1F8FF) ||
                (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) ||
                (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) ||
                (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) ||
                (codePoint >= 0x2600 && codePoint <= 0x26FF) ||
                (codePoint >= 0x2700 && codePoint <= 0x27BF) ||
                (codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF);

            if (!isEmoji)
                result.Append(codePoint > 0xFFFF ? char.ConvertFromUtf32(codePoint) : (char)codePoint);
        }
        return result.ToString();
    }

    /// <summary>
    /// Normalize + language-tag-wrap a single utterance, matching the reference
    /// <c>PreprocessText</c> exactly. <paramref name="lang"/> is coerced to a
    /// supported tag (or <c>na</c>) so callers never crash on an unknown code.
    /// </summary>
    public static string Preprocess(string text, string lang)
    {
        lang = SuperTonicLanguages.Coerce(lang);

        text = text.Normalize(NormalizationForm.FormKD);
        text = RemoveEmojis(text);

        foreach (var (from, to) in SymbolReplacements)
            text = text.Replace(from, to);

        text = Regex.Replace(text, @"[♥☆♡©\\]", "");

        foreach (var (from, to) in ExprReplacements)
            text = text.Replace(from, to);

        text = Regex.Replace(text, @" ,", ",");
        text = Regex.Replace(text, @" \.", ".");
        text = Regex.Replace(text, @" !", "!");
        text = Regex.Replace(text, @" \?", "?");
        text = Regex.Replace(text, @" ;", ";");
        text = Regex.Replace(text, @" :", ":");
        text = Regex.Replace(text, @" '", "'");

        while (text.Contains("\"\"")) text = text.Replace("\"\"", "\"");
        while (text.Contains("''")) text = text.Replace("''", "'");
        while (text.Contains("``")) text = text.Replace("``", "`");

        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (!Regex.IsMatch(text, "[.!?;:,'\"“”‘’)\\]}…。」』】〉》›»]$"))
            text += ".";

        return $"<{lang}>{text}</{lang}>";
    }

    /// <summary>
    /// Split long text into chunks the model can synthesize, at sentence
    /// boundaries. ko/ja cap at 120 chars, everything else 300 (reference values).
    /// </summary>
    public static List<string> Chunk(string text, string lang)
    {
        int maxLen = (lang == "ko" || lang == "ja") ? 120 : 300;
        var chunks = new List<string>();

        var paragraphs = Regex.Split(text.Trim(), @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var sentenceRegex = new Regex(
            @"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+");

        foreach (var paragraph in paragraphs)
        {
            var sentences = sentenceRegex.Split(paragraph);
            string currentChunk = "";
            foreach (var sentence in sentences)
            {
                if (string.IsNullOrEmpty(sentence)) continue;
                if (currentChunk.Length + sentence.Length + 1 <= maxLen)
                {
                    if (!string.IsNullOrEmpty(currentChunk)) currentChunk += " ";
                    currentChunk += sentence;
                }
                else
                {
                    if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
                    currentChunk = sentence;
                }
            }
            if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
        }

        if (chunks.Count == 0) chunks.Add(text.Trim());
        return chunks;
    }
}

/// <summary>
/// Unicode-indexer tokenizer: maps each preprocessed char's code point through
/// <c>unicode_indexer.json</c> (a flat <c>long[]</c> keyed by code point) to a
/// token id. Unknown code points map to 0.
/// </summary>
public sealed class SuperTonicTokenizer
{
    private readonly long[] _indexer;

    private SuperTonicTokenizer(long[] indexer) => _indexer = indexer;

    public static SuperTonicTokenizer Load(string onnxDir)
    {
        var json = File.ReadAllText(Path.Combine(onnxDir, "unicode_indexer.json"));
        var arr = JsonSerializer.Deserialize<long[]>(json)
                  ?? throw new InvalidOperationException("Failed to parse unicode_indexer.json");
        return new SuperTonicTokenizer(arr);
    }

    /// <summary>For tests: build from an in-memory indexer array.</summary>
    public static SuperTonicTokenizer FromArray(long[] indexer) => new(indexer);

    /// <summary>Encode an already-preprocessed (tag-wrapped) string to token ids.</summary>
    public long[] Encode(string preprocessed)
    {
        var ids = new long[preprocessed.Length];
        for (int i = 0; i < preprocessed.Length; i++)
        {
            int cp = preprocessed[i];
            if (cp >= 0 && cp < _indexer.Length)
                ids[i] = _indexer[cp];
        }
        return ids;
    }
}

/// <summary>WAV (PCM16 mono) byte assembly — pure + static for headless tests.</summary>
public static class SuperTonicWav
{
    /// <summary>Encode float samples in [-1,1] as a 16-bit PCM mono WAV byte array.</summary>
    public static byte[] ToWavBytes(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            const int numChannels = 1;
            const int bitsPerSample = 16;
            int byteRate = sampleRate * numChannels * bitsPerSample / 8;
            short blockAlign = numChannels * bitsPerSample / 8;
            int dataSize = samples.Length * bitsPerSample / 8;

            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataSize);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));

            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write((short)numChannels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write((short)bitsPerSample);

            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize);

            foreach (var s in samples)
            {
                float clamped = Math.Max(-1.0f, Math.Min(1.0f, s));
                w.Write((short)(clamped * 32767));
            }
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Owns the four ONNX sessions + config + tokenizer and runs the full
/// text_encoder -> duration_predictor -> vector_estimator (flow-matching) ->
/// vocoder pipeline. Faithful port of the reference <c>TextToSpeech</c> class.
/// </summary>
public sealed class SuperTonicSynthesizer : IDisposable
{
    private readonly SuperTonicConfig _cfg;
    private readonly SuperTonicTokenizer _tokenizer;
    private readonly InferenceSession _dp;
    private readonly InferenceSession _textEnc;
    private readonly InferenceSession _vectorEst;
    private readonly InferenceSession _vocoder;

    public int SampleRate => _cfg.SampleRate;

    private SuperTonicSynthesizer(SuperTonicConfig cfg, SuperTonicTokenizer tokenizer,
        InferenceSession dp, InferenceSession textEnc, InferenceSession vectorEst, InferenceSession vocoder)
    {
        _cfg = cfg; _tokenizer = tokenizer;
        _dp = dp; _textEnc = textEnc; _vectorEst = vectorEst; _vocoder = vocoder;
    }

    public static SuperTonicSynthesizer Load(string modelDir)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 0, // 0 = number of cores
        };
        return new SuperTonicSynthesizer(
            SuperTonicConfig.Load(modelDir),
            SuperTonicTokenizer.Load(modelDir),
            new InferenceSession(Path.Combine(modelDir, "duration_predictor.onnx"), opts),
            new InferenceSession(Path.Combine(modelDir, "text_encoder.onnx"), opts),
            new InferenceSession(Path.Combine(modelDir, "vector_estimator.onnx"), opts),
            new InferenceSession(Path.Combine(modelDir, "vocoder.onnx"), opts));
    }

    /// <summary>
    /// Synthesize a single utterance (auto-chunked for long text). Returns the
    /// concatenated float PCM in [-1,1] at <see cref="SampleRate"/>.
    /// </summary>
    public float[] Synthesize(string text, string lang, SuperTonicStyle style, int totalStep,
        float speed = 1.05f, float silenceDuration = 0.3f)
    {
        lang = SuperTonicLanguages.Coerce(lang);
        var chunks = SuperTonicText.Chunk(text, lang);
        var wavCat = new List<float>();

        foreach (var chunk in chunks)
        {
            var wav = InferOne(chunk, lang, style, totalStep, speed);
            if (wavCat.Count > 0)
                wavCat.AddRange(new float[(int)(silenceDuration * SampleRate)]);
            wavCat.AddRange(wav);
        }
        return wavCat.ToArray();
    }

    private float[] InferOne(string chunk, string lang, SuperTonicStyle style, int totalStep, float speed)
    {
        var preprocessed = SuperTonicText.Preprocess(chunk, lang);
        var textIds = _tokenizer.Encode(preprocessed);
        long textLen = preprocessed.Length;

        var textIdsTensor = new DenseTensor<long>(textIds, new[] { 1, textIds.Length });
        var textMask = LengthToMask(textLen, textIds.Length);
        var textMaskTensor = new DenseTensor<float>(textMask, new[] { 1, 1, textIds.Length });

        var styleTtl = new DenseTensor<float>(style.Ttl, style.TtlShape.Select(x => (int)x).ToArray());
        var styleDp = new DenseTensor<float>(style.Dp, style.DpShape.Select(x => (int)x).ToArray());

        // 1) Duration predictor
        using var dpOut = _dp.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_dp", styleDp),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
        });
        var duration = dpOut.First(o => o.Name == "duration").AsTensor<float>().ToArray();
        for (int i = 0; i < duration.Length; i++) duration[i] /= speed;

        // 2) Text encoder
        using var teOut = _textEnc.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
        });
        var textEmb = teOut.First(o => o.Name == "text_emb").AsTensor<float>();
        // Materialize into an owned tensor so it survives past `teOut` disposal.
        var textEmbTensor = new DenseTensor<float>(textEmb.ToArray(), textEmb.Dimensions.ToArray());

        // 3) Sample noisy latent geometry
        float wavLenMax = duration.Max() * SampleRate;
        long wavLen = (long)(duration[0] * SampleRate);
        int chunkSize = _cfg.BaseChunkSize * _cfg.ChunkCompressFactor;
        int latentLen = (int)((wavLenMax + chunkSize - 1) / chunkSize);
        int latentDim = _cfg.LatentDim * _cfg.ChunkCompressFactor;

        var latentMask = LengthToMask((wavLen + chunkSize - 1) / chunkSize, latentLen);

        var rng = new Random();
        var xt = new float[latentDim * latentLen];
        for (int d = 0; d < latentDim; d++)
        {
            for (int t = 0; t < latentLen; t++)
            {
                // Box-Muller standard normal, masked to the valid latent length.
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                float z = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                xt[d * latentLen + t] = z * latentMask[t];
            }
        }
        var latentDims = new[] { 1, latentDim, latentLen };
        var latentMaskDims = new[] { 1, 1, latentLen };

        // 4) Iterative denoising (flow matching)
        for (int step = 0; step < totalStep; step++)
        {
            using var veOut = _vectorEst.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("noisy_latent", new DenseTensor<float>(xt, latentDims)),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmbTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
                NamedOnnxValue.CreateFromTensor("latent_mask", new DenseTensor<float>(latentMask, latentMaskDims)),
                NamedOnnxValue.CreateFromTensor("total_step", new DenseTensor<float>(new[] { (float)totalStep }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(new[] { (float)step }, new[] { 1 })),
            });
            var denoised = veOut.First(o => o.Name == "denoised_latent").AsTensor<float>();
            for (int i = 0; i < xt.Length; i++) xt[i] = denoised.GetValue(i);
        }

        // 5) Vocoder
        using var vocOut = _vocoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(xt, latentDims)),
        });
        return vocOut.First(o => o.Name == "wav_tts").AsTensor<float>().ToArray();
    }

    /// <summary>Row of 1.0 up to <paramref name="length"/>, 0.0 after, padded to <paramref name="maxLen"/>.</summary>
    private static float[] LengthToMask(long length, int maxLen)
    {
        var mask = new float[maxLen];
        for (int j = 0; j < maxLen; j++) mask[j] = j < length ? 1.0f : 0.0f;
        return mask;
    }

    public void Dispose()
    {
        _dp.Dispose();
        _textEnc.Dispose();
        _vectorEst.Dispose();
        _vocoder.Dispose();
    }
}
