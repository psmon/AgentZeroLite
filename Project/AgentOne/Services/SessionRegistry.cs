using System.Diagnostics;
using System.Text.Json;

namespace AgentOne.Services;

/// <summary>
/// Where the one background session is: a small file under ~/.agent-one, the
/// pipe name derived from that folder so two homes never collide. One session
/// at a time is the rule — a second `session start` is refused while the
/// first's process is alive, and a stale record from a crash is cleared.
/// </summary>
public static class SessionRegistry
{
    public static string Path => System.IO.Path.Combine(AppPaths.BaseDir, "session.json");

    /// <summary>The pipe name for this home. Short and safe on every platform's pipe namespace.</summary>
    public static string PipeName(string? suffix = null)
    {
        var key = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.BaseDir.ToLowerInvariant()));
        return "agent-one-" + Convert.ToHexString(key)[..8].ToLowerInvariant() + (suffix ?? "");
    }

    public static SessionRecord? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(Path), AgentOneWireJson.Default.SessionRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The record, only if its process is still running; a dead one is forgotten.</summary>
    public static SessionRecord? LoadAlive()
    {
        var record = Load();
        if (record is null) return null;
        if (IsAlive(record.Pid)) return record;
        Clear();
        return null;
    }

    public static void Save(SessionRecord record)
    {
        AppPaths.EnsureBaseDir();
        File.WriteAllText(Path, JsonSerializer.Serialize(record, AgentOneWireJson.Default.SessionRecord));
    }

    public static void Clear()
    {
        try { File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing to clear */ }
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
