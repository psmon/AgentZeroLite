using System.Text.Json;

namespace Agent.Common.Mp3;

/// <summary>
/// JSON persistence for <see cref="Mp3Settings"/>. Mirrors
/// <see cref="Agent.Common.Vision.VisionSettingsStore"/> so
/// <c>mp3-settings.json</c> sits alongside <c>vision-settings.json</c> /
/// <c>music-settings.json</c> under <c>%LOCALAPPDATA%\AgentZeroLite\</c>.
/// The playlist rows themselves live in the SQLite DB (<c>Mp3Track</c>) —
/// this file only remembers which folder to map/scan.
/// </summary>
public static class Mp3SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "mp3-settings.json");

    public static Mp3Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Mp3Settings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Mp3Settings>(json) ?? new Mp3Settings();
        }
        catch
        {
            return new Mp3Settings();
        }
    }

    public static void Save(Mp3Settings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
