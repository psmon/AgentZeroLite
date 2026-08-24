using System.Text.Json;

namespace Agent.Common.Remote;

/// <summary>
/// JSON persistence for <see cref="RemoteSettings"/>. Mirrors
/// <see cref="Agent.Common.Vision.VisionSettingsStore"/> — <c>remote-settings.json</c>
/// under <c>%LOCALAPPDATA%\AgentZeroLite\</c>, defaulted POCO on any failure.
///
/// <para>No secret protection is applied here: the only sensitive-looking field,
/// <see cref="RemoteSettings.PairedTokenHashes"/>, already holds one-way hashes, so
/// there is nothing reversible to encrypt.</para>
/// </summary>
public static class RemoteSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "remote-settings.json");

    public static RemoteSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new RemoteSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<RemoteSettings>(json) ?? new RemoteSettings();
        }
        catch
        {
            return new RemoteSettings();
        }
    }

    public static void Save(RemoteSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
