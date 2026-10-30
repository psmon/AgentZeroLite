using Agent.Common.Platform;

namespace Agent.Common.Voice.Streaming;

/// <summary>
/// One streaming model AgentZero offers. All of them are sherpa-onnx streaming transducers
/// (encoder / decoder / joiner + tokens.txt) published as <c>.tar.bz2</c> on k2-fsa's
/// <c>asr-models</c> release, so one engine (<see cref="SherpaStreamingStt"/>) runs every entry.
/// </summary>
/// <param name="Id">Settings value (<see cref="VoiceSettings.StreamingSttModel"/>).</param>
/// <param name="Archive">Release asset name without <c>.tar.bz2</c>; also the folder it extracts to.</param>
/// <param name="Label">What the settings picker shows.</param>
/// <param name="ApproxMegabytes">Download size, for the button caption.</param>
/// <param name="ChunkMs">Encoder chunk — the floor of how soon a word can appear.</param>
/// <param name="Multilingual">Takes a per-stream <c>language</c> option (Nemotron 3.5); otherwise one language only.</param>
/// <param name="Punctuates">Emits punctuation and casing itself.</param>
/// <param name="DecodingMethod">sherpa-onnx decoding method.</param>
public sealed record StreamingSttModel(
    string Id,
    string Archive,
    string Label,
    int ApproxMegabytes,
    int ChunkMs,
    bool Multilingual,
    bool Punctuates,
    string DecodingMethod);

/// <summary>
/// The streaming models and where they live
/// (<c>%LOCALAPPDATA%\AgentZeroLite\models\streaming-stt\&lt;archive&gt;</c>, shared by every profile like
/// the other on-device models).
///
/// <para>Why these two families (researched 2026-10-30, details in
/// <c>harness/knowledge/voice-curator/streaming-stt-models.md</c>):</para>
/// <list type="bullet">
///   <item><b>Nemotron 3.5 ASR streaming 0.6B</b> — NVIDIA's cache-aware FastConformer-RNNT, 40 locales
///   with Korean in the "transcription-ready" tier (FLEURS CER 7.2 % at 560 ms, 7.7 % at 160 ms), punctuation
///   built in, OpenMDW-1.1. sherpa-onnx runs it since 1.13.3 (PR #3671); language is a stream option.</item>
///   <item><b>Korean streaming Zipformer</b> — k2-fsa's own Korean-only transducer: a quarter of the memory (int8 encoder 121 MB),
///   RTF ~0.1 on one CPU thread, no punctuation. The light choice.</item>
/// </list>
/// <para>Gemma 4 E2B/E4B (audio-in multimodal LLM) was the other candidate and is not here: it transcribes a
/// clip of up to 30 s at once — chunking it is still "hear it all, then answer", the latency this exists to
/// remove.</para>
/// </summary>
public static class StreamingSttModelCatalog
{
    public const string ReleaseBaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    public static readonly StreamingSttModel Nemotron560 = new(
        "nemotron-3.5-560ms",
        "sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11",
        "Nemotron 3.5 ASR · 560 ms (multilingual, punctuated)",
        ApproxMegabytes: 455, ChunkMs: 560, Multilingual: true, Punctuates: true, DecodingMethod: "greedy_search");

    public static readonly StreamingSttModel Nemotron160 = new(
        "nemotron-3.5-160ms",
        "sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-160ms-int8-2026-06-11",
        "Nemotron 3.5 ASR · 160 ms (multilingual, fastest words)",
        ApproxMegabytes: 455, ChunkMs: 160, Multilingual: true, Punctuates: true, DecodingMethod: "greedy_search");

    public static readonly StreamingSttModel ZipformerKorean = new(
        "zipformer-ko",
        "sherpa-onnx-streaming-zipformer-korean-2024-06-16",
        "Zipformer Korean (small, no punctuation)",
        ApproxMegabytes: 400, ChunkMs: 320, Multilingual: false, Punctuates: false, DecodingMethod: "modified_beam_search");

    public static readonly IReadOnlyList<StreamingSttModel> All = [Nemotron560, Nemotron160, ZipformerKorean];

    /// <summary>
    /// The official streaming model (operator decision 2026-10-30, after the measurements in
    /// <c>harness/knowledge/voice-curator/streaming-stt-models.md</c>): the best Korean of the two, punctuated,
    /// and still answering under a second.
    /// </summary>
    public static StreamingSttModel Default => Nemotron560;

    /// <summary>Unknown or empty ids resolve to <see cref="Default"/>.</summary>
    public static StreamingSttModel Resolve(string? id)
        => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static string RootDirectory => Path.Combine(AppPaths.SharedRoot, "models", "streaming-stt");

    public static string ModelDirectory(StreamingSttModel model) => Path.Combine(RootDirectory, model.Archive);

    public static string DownloadUrl(StreamingSttModel model) => ReleaseBaseUrl + model.Archive + ".tar.bz2";

    /// <summary>The four files the recognizer needs, or null when the folder does not hold a complete model.</summary>
    public static StreamingSttModelFiles? FindFiles(StreamingSttModel model) => FindFiles(ModelDirectory(model));

    /// <summary>
    /// Pick the files by role rather than by exact name — the two families name them differently
    /// (<c>encoder.int8.onnx</c> vs <c>encoder-epoch-99-avg-1.int8.onnx</c>). The encoder and joiner prefer int8
    /// (a quarter of the memory, the accuracy k2-fsa publishes); the decoder prefers fp32 when the archive ships
    /// it, as k2-fsa's own int8 examples do — it is tiny and int8 costs it accuracy.
    /// </summary>
    public static StreamingSttModelFiles? FindFiles(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var onnx = Directory.GetFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly);
        var tokens = Path.Combine(dir, "tokens.txt");
        string? Pick(string role, bool preferInt8)
        {
            var mine = onnx.Where(f => Path.GetFileName(f).StartsWith(role, StringComparison.OrdinalIgnoreCase)).ToList();
            var int8 = mine.FirstOrDefault(f => f.EndsWith(".int8.onnx", StringComparison.OrdinalIgnoreCase));
            var full = mine.FirstOrDefault(f => !f.EndsWith(".int8.onnx", StringComparison.OrdinalIgnoreCase)
                                             && !f.EndsWith(".fp16.onnx", StringComparison.OrdinalIgnoreCase));
            return preferInt8 ? int8 ?? full : full ?? int8;
        }
        var encoder = Pick("encoder", preferInt8: true);
        var decoder = Pick("decoder", preferInt8: false);
        var joiner = Pick("joiner", preferInt8: true);
        if (encoder is null || decoder is null || joiner is null || !File.Exists(tokens)) return null;
        return new StreamingSttModelFiles(encoder, decoder, joiner, tokens);
    }

    public static bool IsInstalled(StreamingSttModel model) => FindFiles(model) is not null;
}

public sealed record StreamingSttModelFiles(string Encoder, string Decoder, string Joiner, string Tokens);
