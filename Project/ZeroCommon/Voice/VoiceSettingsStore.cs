using System.Text.Json;
using Agent.Common.Security;

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
            var settings = JsonSerializer.Deserialize<VoiceSettings>(json) ?? new VoiceSettings();
            // Decrypt credential fields at rest (#6). Legacy plaintext files
            // pass through unchanged and migrate to ciphertext on next Save.
            settings.SttOpenAIApiKey = SecretProtection.Unprotect(settings.SttOpenAIApiKey);
            settings.TtsOpenAIApiKey = SecretProtection.Unprotect(settings.TtsOpenAIApiKey);
            return settings;
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
        // Encrypt credential fields for the file only; the caller's in-memory
        // object keeps plaintext (restored in finally) so nothing downstream
        // sees ciphertext.
        var stt = settings.SttOpenAIApiKey;
        var tts = settings.TtsOpenAIApiKey;
        try
        {
            settings.SttOpenAIApiKey = SecretProtection.Protect(stt);
            settings.TtsOpenAIApiKey = SecretProtection.Protect(tts);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        finally
        {
            settings.SttOpenAIApiKey = stt;
            settings.TtsOpenAIApiKey = tts;
        }
    }
}
