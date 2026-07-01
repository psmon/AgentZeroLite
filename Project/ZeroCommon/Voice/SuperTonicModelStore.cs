namespace Agent.Common.Voice;

/// <summary>
/// Path convention + presence checks for the native SuperTonic-3 ONNX bundle.
/// Mirrors <c>MusicSettingsStore</c> / <c>DiarizationSettingsStore</c> so every
/// on-device model lives under the same <c>%LOCALAPPDATA%\AgentZeroLite\models\</c>
/// root instead of the pip package's old HuggingFace-Hub cache
/// (<c>~/.cache/supertonic3</c>). No Python, no pip — the 4 ONNX graphs plus the
/// two data files (tokenizer + config) and the 10 voice-style embeddings are the
/// entire runtime surface (M0020 follow-up: pip dependency removed).
/// </summary>
public static class SuperTonicModelStore
{
    /// <summary>The four ONNX graphs Supertonic-3 runs, in pipeline order.</summary>
    public static readonly string[] OnnxFiles =
    [
        "text_encoder.onnx",
        "duration_predictor.onnx",
        "vector_estimator.onnx",
        "vocoder.onnx",
    ];

    /// <summary>Config (tts.json) + tokenizer (unicode_indexer.json). Both required.</summary>
    public static readonly string[] DataFiles =
    [
        "tts.json",
        "unicode_indexer.json",
    ];

    /// <summary>Builtin voice ids shipped with Supertonic-3 (5 male, 5 female).</summary>
    public static readonly string[] BuiltinVoices =
        ["M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5"];

    /// <summary>
    /// Convention root when <see cref="VoiceSettings.SupertonicModelDir"/> is empty.
    /// The ONNX graphs + data files sit directly here; voice styles under
    /// <c>voice_styles/</c>.
    /// </summary>
    public static string DefaultModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "models", "supertonic");

    /// <summary>Sub-directory holding the per-voice <c>{id}.json</c> embeddings.</summary>
    public static string VoiceStylesSubdir => "voice_styles";

    /// <summary>Resolve the active model dir, falling back to the default when unset.</summary>
    public static string ResolveModelDir(VoiceSettings s)
        => string.IsNullOrWhiteSpace(s.SupertonicModelDir)
            ? DefaultModelDirectory
            : s.SupertonicModelDir;

    /// <summary>Absolute path to a voice-style JSON inside <paramref name="modelDir"/>.</summary>
    public static string VoiceStylePath(string modelDir, string voice)
        => Path.Combine(modelDir, VoiceStylesSubdir, $"{voice}.json");

    /// <summary>
    /// True when the 4 ONNX graphs + 2 data files are all present in
    /// <paramref name="modelDir"/>. Voice-style files are checked separately at
    /// synthesis time (a single voice can be missing without the core model
    /// being broken).
    /// </summary>
    public static bool IsModelPresent(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;

        foreach (var f in OnnxFiles)
            if (!File.Exists(Path.Combine(modelDir, f)))
                return false;
        foreach (var f in DataFiles)
            if (!File.Exists(Path.Combine(modelDir, f)))
                return false;
        return true;
    }
}
