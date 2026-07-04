using System.Text.Json;

namespace Agent.Common.Vision;

/// <summary>
/// JSON persistence + path convention for the vision (Florence-2) model. Mirrors
/// <see cref="Agent.Common.Music.MusicSettingsStore"/> so <c>vision-settings.json</c>
/// sits alongside <c>music-settings.json</c> / <c>voice-settings.json</c> under
/// <c>%LOCALAPPDATA%\AgentZeroLite\</c>, and the model cache lives under the shared
/// <c>models\florence2\</c> root.
///
/// Presence is tracked with a sentinel <c>.florence2-ready</c> marker written by
/// <see cref="VisionModelDownloader"/> on a fully successful download, rather than
/// probing Florence2's internal ONNX filenames — that keeps this layer decoupled
/// from the wrapper package's on-disk layout.
/// </summary>
public static class VisionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private const string ReadyMarkerName = ".florence2-ready";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "vision-settings.json");

    /// <summary>Convention root for Florence-2 ONNX files when <see cref="VisionSettings.ModelDir"/> is empty.</summary>
    public static string DefaultModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "models", "florence2");

    public static VisionSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new VisionSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<VisionSettings>(json) ?? new VisionSettings();
        }
        catch
        {
            return new VisionSettings();
        }
    }

    public static void Save(VisionSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>Resolve the active model dir, falling back to <see cref="DefaultModelDirectory"/>.</summary>
    public static string ResolveModelDir(VisionSettings s)
        => string.IsNullOrWhiteSpace(s.ModelDir) ? DefaultModelDirectory : s.ModelDir;

    /// <summary>Absolute path to the completion sentinel inside <paramref name="modelDir"/>.</summary>
    public static string ReadyMarkerPath(string modelDir) => Path.Combine(modelDir, ReadyMarkerName);

    /// <summary>True once a full download has written the sentinel marker.</summary>
    public static bool IsModelPresent(string modelDir)
        => !string.IsNullOrWhiteSpace(modelDir) && File.Exists(ReadyMarkerPath(modelDir));
}
