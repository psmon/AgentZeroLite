using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common;
using Agent.Common.Agents;

namespace AgentZeroWpf.Services;

/// <summary>
/// Installs AgentZero state-reporting hooks into per-account
/// <c>~/.claude*/settings.json</c> (mission W1, orca-adoption). The hooks call
/// <c>AgentZeroLite.exe -cli agent-hook --event &lt;Event&gt;</c> so the GUI learns
/// the hosted agent CLI's real state instead of scraping terminal output.
///
/// Modeled on <see cref="Browser.StatusLineWrapperInstaller"/>: discover
/// profiles → backup → JSON-subtree patch via the pure
/// <see cref="AgentHookSettingsMerger"/> → atomic write (.tmp + Move). Uninstall
/// removes only AgentZero's own entries (marker-matched), leaving foreign hooks
/// intact.
/// </summary>
public static class AgentHookInstaller
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string BackupRoot => Path.Combine(LocalAppData, "AgentZeroLite", "agent-hook-backup");

    public sealed record HookProfile(string AccountKey, string ConfigDir, string SettingsJsonPath, bool SettingsJsonExists, bool OurHooksInstalled);

    public sealed record HookResult(string AccountKey, bool Ok, string? Action, string? BackupPath, string? Error);

    /// <summary>Discover every Claude Code profile (~/.claude*) on disk.</summary>
    public static IReadOnlyList<HookProfile> DiscoverProfiles()
    {
        var list = new List<HookProfile>();
        if (!Directory.Exists(UserProfile)) return list;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(UserProfile, ".claude*", SearchOption.TopDirectoryOnly); }
        catch { return list; }

        foreach (var dir in dirs)
        {
            var hasProjects = Directory.Exists(Path.Combine(dir, "projects"));
            var settingsPath = Path.Combine(dir, "settings.json");
            var hasSettings = File.Exists(settingsPath);
            if (!hasProjects && !hasSettings) continue;

            var name = Path.GetFileName(dir).TrimStart('.');
            if (string.IsNullOrEmpty(name)) name = "claude";

            bool ours = false;
            if (hasSettings)
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(settingsPath), null,
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                        is JsonObject obj)
                        ours = AgentHookSettingsMerger.HasOurHooks(obj);
                }
                catch { /* unreadable → treat as not-installed */ }
            }

            list.Add(new HookProfile(name, dir, settingsPath, hasSettings, ours));
        }
        return list;
    }

    /// <summary>Install hooks into every discovered profile.</summary>
    public static IReadOnlyList<HookResult> InstallAll()
    {
        var results = new List<HookResult>();
        foreach (var p in DiscoverProfiles())
            results.Add(Install(p.AccountKey));
        return results;
    }

    /// <summary>Remove AgentZero hooks from every discovered profile.</summary>
    public static IReadOnlyList<HookResult> UninstallAll()
    {
        var results = new List<HookResult>();
        foreach (var p in DiscoverProfiles())
            results.Add(Uninstall(p.AccountKey));
        return results;
    }

    public static HookResult Install(string accountKey)
    {
        try
        {
            var prof = FindProfile(accountKey);
            if (prof is null) return new HookResult(accountKey, false, null, null, $"profile '{accountKey}' not found");

            var (root, originalRaw) = ReadOrEmpty(prof);
            if (root is null) return new HookResult(accountKey, false, null, null, "settings.json is not a JSON object");

            string? backupPath = Backup(accountKey, prof.SettingsJsonPath, originalRaw);

            var exe = SelfExePath();
            AgentHookSettingsMerger.AddHooks(root, ev => $"\"{exe}\" -cli agent-hook --event {ev} --no-wait");
            WriteAtomic(prof.SettingsJsonPath, root);

            AppLogger.Log($"[AgentHookInstaller] installed for '{accountKey}'");
            return new HookResult(accountKey, true, prof.OurHooksInstalled ? "reinstalled" : "installed", backupPath, null);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[AgentHookInstaller] install failed for '{accountKey}'", ex);
            return new HookResult(accountKey, false, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static HookResult Uninstall(string accountKey)
    {
        try
        {
            var prof = FindProfile(accountKey);
            if (prof is null) return new HookResult(accountKey, false, null, null, $"profile '{accountKey}' not found");
            if (!prof.SettingsJsonExists) return new HookResult(accountKey, true, "noop", null, null);

            var (root, originalRaw) = ReadOrEmpty(prof);
            if (root is null) return new HookResult(accountKey, false, null, null, "settings.json is not a JSON object");

            string? backupPath = Backup(accountKey, prof.SettingsJsonPath, originalRaw);
            bool changed = AgentHookSettingsMerger.RemoveHooks(root);
            if (changed) WriteAtomic(prof.SettingsJsonPath, root);

            AppLogger.Log($"[AgentHookInstaller] uninstalled for '{accountKey}' (changed={changed})");
            return new HookResult(accountKey, true, changed ? "uninstalled" : "noop", backupPath, null);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[AgentHookInstaller] uninstall failed for '{accountKey}'", ex);
            return new HookResult(accountKey, false, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static HookProfile? FindProfile(string accountKey)
    {
        foreach (var p in DiscoverProfiles())
            if (string.Equals(p.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    private static (JsonObject? Root, string? OriginalRaw) ReadOrEmpty(HookProfile prof)
    {
        if (!prof.SettingsJsonExists) return (new JsonObject(), null);
        var raw = File.ReadAllText(prof.SettingsJsonPath);
        var node = JsonNode.Parse(raw, null,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return (node as JsonObject, raw);
    }

    private static string? Backup(string accountKey, string settingsPath, string? originalRaw)
    {
        if (originalRaw is null) return null;
        var dir = Path.Combine(BackupRoot, accountKey);
        Directory.CreateDirectory(dir);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(dir, $"settings.json.{ts}.bak");
        File.WriteAllText(backupPath, originalRaw);
        return backupPath;
    }

    private static void WriteAtomic(string settingsPath, JsonObject root)
    {
        var tmp = settingsPath + ".tmp";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        try { File.Move(tmp, settingsPath, overwrite: true); }
        catch { File.Delete(tmp); throw; }
    }

    private static string SelfExePath()
        => Process.GetCurrentProcess().MainModule?.FileName
           ?? Path.Combine(AppContext.BaseDirectory, "AgentZeroLite.exe");
}
