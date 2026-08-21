using System.Text.Json;
using Agent.Common.Security;

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
            var settings = JsonSerializer.Deserialize<LlmRuntimeSettings>(json) ?? new LlmRuntimeSettings();
            // Decrypt credential fields at rest (#6). Legacy plaintext files
            // pass through unchanged and migrate to ciphertext on next Save.
            settings.External.OpenAIApiKey = SecretProtection.Unprotect(settings.External.OpenAIApiKey);
            settings.External.LMStudioApiKey = SecretProtection.Unprotect(settings.External.LMStudioApiKey);
            return settings;
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
        // Encrypt credential fields for the file only; the caller's in-memory
        // object keeps plaintext (restored in finally).
        var openAi = settings.External.OpenAIApiKey;
        var lmStudio = settings.External.LMStudioApiKey;
        try
        {
            settings.External.OpenAIApiKey = SecretProtection.Protect(openAi);
            settings.External.LMStudioApiKey = SecretProtection.Protect(lmStudio);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        finally
        {
            settings.External.OpenAIApiKey = openAi;
            settings.External.LMStudioApiKey = lmStudio;
        }
    }
}
