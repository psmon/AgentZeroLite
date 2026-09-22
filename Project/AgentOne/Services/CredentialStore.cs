using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOne.Services;

/// <summary>The one secret agent-one holds. Its own file, never config.json.</summary>
public sealed class Credentials
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

/// <summary>
/// Stores the API key in ~/.agent-one/credentials.json, apart from config.json.
///
/// Two files rather than one because they have different lifetimes and different
/// risk: config.json is the thing you would paste into an issue or copy between
/// machines, and a secret must not ride along. The file is written user-only
/// where the platform can express that.
/// </summary>
public static class CredentialStore
{
    public static string Path => System.IO.Path.Combine(AppPaths.BaseDir, "credentials.json");

    /// <summary>The stored key, or null when there is none.</summary>
    public static string? Load()
    {
        if (!File.Exists(Path)) return null;

        try
        {
            var json = File.ReadAllText(Path);
            var key = JsonSerializer.Deserialize(json, AgentOneJson.Default.Credentials)?.ApiKey;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string apiKey)
    {
        AppPaths.EnsureBaseDir();
        var json = JsonSerializer.Serialize(new Credentials { ApiKey = apiKey.Trim() }, AgentOneJson.Default.Credentials);
        File.WriteAllText(Path, json);
        RestrictToOwner(Path);
    }

    public static void Clear()
    {
        try { File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing to clear */ }
    }

    /// <summary>
    /// Enough of the key to recognise it, never enough to use it. Short strings
    /// are hidden outright rather than half-revealed.
    /// </summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(none)";
        key = key.Trim();
        return key.Length <= 12 ? new string('•', key.Length) : $"{key[..6]}…{key[^4..]}";
    }

    private static void RestrictToOwner(string path)
    {
        // Unix can say "owner only" in one call. On Windows the profile directory
        // already restricts to the user, and tightening the ACL from a
        // non-Windows-targeted assembly needs a platform package we do not carry.
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: a key that is stored but world-readable still beats a
            // run that fails, and `agent-one auth show` reports where it lives.
        }
    }
}
