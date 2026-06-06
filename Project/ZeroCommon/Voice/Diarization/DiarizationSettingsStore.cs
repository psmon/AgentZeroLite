using System.Text.Json;

namespace Agent.Common.Voice.Diarization;

/// <summary>
/// JSON persistence for <see cref="DiarizationSettings"/>. Mirrors
/// VoiceSettingsStore / MusicSettingsStore so the side-car files live
/// next to each other under <c>%LOCALAPPDATA%\AgentZeroLite\</c>.
///
///   llm-settings.json
///   voice-settings.json
///   music-settings.json
///   diarization-settings.json   ← this file (M0024)
/// </summary>
public static class DiarizationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "diarization-settings.json");

    /// <summary>
    /// Convention root for Sherpa diarization model files when settings paths
    /// are empty. Public so the Voice tab can show the expected location in
    /// the UI.
    /// </summary>
    public static string DefaultModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "models", "sherpa-diarization");

    public static string DefaultSegmentationPath => Path.Combine(DefaultModelDirectory, "segmentation.onnx");

    public static string DefaultEmbeddingPath => Path.Combine(DefaultModelDirectory, "embedding.onnx");

    public static DiarizationSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DiarizationSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DiarizationSettings>(json) ?? new DiarizationSettings();
        }
        catch { return new DiarizationSettings(); }
    }

    public static void Save(DiarizationSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    public static string ResolveSegmentationPath(DiarizationSettings s)
        => string.IsNullOrWhiteSpace(s.SegmentationModelPath) ? DefaultSegmentationPath : s.SegmentationModelPath;

    public static string ResolveEmbeddingPath(DiarizationSettings s)
        => string.IsNullOrWhiteSpace(s.EmbeddingModelPath) ? DefaultEmbeddingPath : s.EmbeddingModelPath;
}
