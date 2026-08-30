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

    /// <summary>
    /// Raised whenever the persisted music settings change (Save) or the model
    /// file is replaced (download). Cached consumers — notably the live
    /// classifier in <c>WebDevHost</c> — subscribe to drop an instance pinned to
    /// the old model path (M0025 follow-up #12). Static, so it reaches every
    /// consumer without a direct reference; consumers MUST unsubscribe on
    /// dispose or the delegate pins them alive.
    /// </summary>
    public static event Action? Changed;

    /// <summary>Raise <see cref="Changed"/> — for callers that alter the model on disk without going through <see cref="Save"/> (e.g. a model download).</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    public static void Save(MusicSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
        Changed?.Invoke();
    }

    /// <summary>Resolve <see cref="MusicSettings.ModelPath"/>, falling back to <see cref="DefaultModelPath"/>.</summary>
    public static string ResolveModelPath(MusicSettings s)
        => string.IsNullOrWhiteSpace(s.ModelPath) ? DefaultModelPath : s.ModelPath;

    /// <summary>Resolve <see cref="MusicSettings.LabelsPath"/>, falling back to <see cref="DefaultLabelsPath"/>.</summary>
    public static string ResolveLabelsPath(MusicSettings s)
        => string.IsNullOrWhiteSpace(s.LabelsPath) ? DefaultLabelsPath : s.LabelsPath;
}
