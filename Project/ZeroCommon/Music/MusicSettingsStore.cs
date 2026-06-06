using System.Text.Json;

namespace Agent.Common.Music;

/// <summary>
/// JSON persistence for <see cref="MusicSettings"/>. Mirrors VoiceSettingsStore
/// so the three side-car files (<c>llm-settings.json</c>, <c>voice-settings.json</c>,
/// <c>music-settings.json</c>) live next to each other under
/// <c>%LOCALAPPDATA%\AgentZeroLite\</c>.
/// </summary>
public static class MusicSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "music-settings.json");

    /// <summary>
    /// Convention root for AST model files when MusicSettings paths are empty.
    /// Public so the Music tab can show the expected location in the UI.
    /// </summary>
    public static string DefaultModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "models", "ast-audioset");

    public static string DefaultModelPath => Path.Combine(DefaultModelDirectory, "model.onnx");

    public static string DefaultLabelsPath => Path.Combine(DefaultModelDirectory, "class_labels_indices.csv");

    public static MusicSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new MusicSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<MusicSettings>(json) ?? new MusicSettings();
        }
        catch
        {
            return new MusicSettings();
        }
    }

    public static void Save(MusicSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>Resolve <see cref="MusicSettings.ModelPath"/>, falling back to <see cref="DefaultModelPath"/>.</summary>
    public static string ResolveModelPath(MusicSettings s)
        => string.IsNullOrWhiteSpace(s.ModelPath) ? DefaultModelPath : s.ModelPath;

    /// <summary>Resolve <see cref="MusicSettings.LabelsPath"/>, falling back to <see cref="DefaultLabelsPath"/>.</summary>
    public static string ResolveLabelsPath(MusicSettings s)
        => string.IsNullOrWhiteSpace(s.LabelsPath) ? DefaultLabelsPath : s.LabelsPath;
}
