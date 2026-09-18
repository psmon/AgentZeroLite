using System.Text.Json;

namespace Agent.Common.Wearable;

/// <summary>
/// JSON persistence for <see cref="WearableSettings"/> — <c>wearable-settings.json</c>
/// under <c>%LOCALAPPDATA%\AgentZeroLite\</c>, defaulted POCO on any failure. Mirrors
/// <see cref="Agent.Common.Remote.RemoteSettingsStore"/>.
///
/// <para>Both processes read this file: the GUI panel writes it, the wearable host reads
/// it at startup. There is no live reload — the panel restarts the host when settings
/// change, which is also the only way to move a BLE radio handle safely.</para>
///
/// <para>Nothing here is a credential (the LLM key lives in <c>llm-settings.json</c>,
/// already DPAPI-protected), so no secret protection is applied.</para>
/// </summary>
public static class WearableSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "wearable-settings.json");

    public static WearableSettings Load() => Load(DefaultFilePath);

    /// <param name="path">Explicit file, so the host can be pointed at a scratch config.</param>
    public static WearableSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new WearableSettings();
            var json = File.ReadAllText(path);
            return (JsonSerializer.Deserialize<WearableSettings>(json) ?? new WearableSettings()).Normalize();
        }
        catch
        {
            return new WearableSettings();
        }
    }

    public static void Save(WearableSettings settings) => Save(settings, DefaultFilePath);

    public static void Save(WearableSettings settings, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
