using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOne.Services;

/// <summary>The secrets agent-one holds. Their own file, never config.json.</summary>
public sealed class Credentials
{
    /// <summary>The LLM provider key.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>The TypeSafe / Jev key used by smart mode. A different service, a different key.</summary>
    [JsonPropertyName("jevApiKey")]
    public string? JevApiKey { get; set; }
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

    /// <summary>Names the keys this store holds, so callers never pass a bare string.</summary>
    public enum Slot { Provider, Jev }

    /// <summary>Everything in the file, or an empty set when there is nothing yet.</summary>
    public static Credentials LoadAll()
    {
        if (!File.Exists(Path)) return new Credentials();

        try
        {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize(json, AgentOneJson.Default.Credentials) ?? new Credentials();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Credentials();
        }
    }

    /// <summary>One stored key, or null when there is none.</summary>
    public static string? Load(Slot slot = Slot.Provider)
    {
        var key = slot == Slot.Jev ? LoadAll().JevApiKey : LoadAll().ApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>
    /// Writes one key, leaving the other alone. Read-modify-write rather than
    /// overwrite: storing the Jev key must not silently delete the provider key.
    /// </summary>
    public static void Save(string apiKey, Slot slot = Slot.Provider)
    {
        AppPaths.EnsureBaseDir();

        var all = LoadAll();
        if (slot == Slot.Jev) all.JevApiKey = apiKey.Trim();
        else all.ApiKey = apiKey.Trim();

        File.WriteAllText(Path, JsonSerializer.Serialize(all, AgentOneJson.Default.Credentials));
        RestrictToOwner(Path);
    }

    /// <summary>Forgets one key and keeps the rest of the file.</summary>
    public static void Clear(Slot slot)
    {
        if (!File.Exists(Path)) return;

        var all = LoadAll();
        if (slot == Slot.Jev) all.JevApiKey = null;
        else all.ApiKey = null;

        if (all.ApiKey is null && all.JevApiKey is null) { Clear(); return; }

        File.WriteAllText(Path, JsonSerializer.Serialize(all, AgentOneJson.Default.Credentials));
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
