using System.Text.Json;

namespace AgentOne.Services;

/// <summary>
/// Reads and writes ~/.agent-one/config.json. A missing or corrupt file is not
/// an error — defaults are returned — because a CLI that refuses to start over
/// its own settings file is worse than one that falls back and says so.
/// </summary>
public static class ConfigStore
{
    public static AgentConfig Load() => Load(out _);

    /// <param name="warning">Non-empty when an existing file could not be parsed and defaults were used instead.</param>
    public static AgentConfig Load(out string warning)
    {
        warning = "";
        var path = AppPaths.ConfigPath;
        if (!File.Exists(path)) return new AgentConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, AgentOneJson.Default.AgentConfig) ?? new AgentConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warning = $"could not read {path} ({ex.Message}) — using defaults";
            return new AgentConfig();
        }
    }

    public static void Save(AgentConfig config)
    {
        AppPaths.EnsureBaseDir();
        var json = JsonSerializer.Serialize(config, AgentOneJson.Default.AgentConfig);
        File.WriteAllText(AppPaths.ConfigPath, json);
    }
}
