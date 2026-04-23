using System.Text.Json;

namespace Agent.Common.Llm;

public static class LlmSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "llm-settings.json");

    public static LlmRuntimeSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new LlmRuntimeSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LlmRuntimeSettings>(json) ?? new LlmRuntimeSettings();
        }
        catch
        {
            return new LlmRuntimeSettings();
        }
    }

    public static void Save(LlmRuntimeSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
