using System.Text.Json;

namespace Agent.Common.Voice;

/// <summary>
/// JSON persistence for <see cref="VoiceSettings"/>. Mirrors
/// <c>LlmSettingsStore</c> exactly so the two side-car files
/// (<c>llm-settings.json</c> + <c>voice-settings.json</c>) live next to each
/// other under <c>%LOCALAPPDATA%\AgentZeroLite\</c>.
/// </summary>
public static class VoiceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "voice-settings.json");

    public static VoiceSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new VoiceSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<VoiceSettings>(json) ?? new VoiceSettings();
        }
        catch
        {
            return new VoiceSettings();
        }
    }

    public static void Save(VoiceSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
