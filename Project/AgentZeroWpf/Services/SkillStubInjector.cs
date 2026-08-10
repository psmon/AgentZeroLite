using System.Collections.Generic;
using System.IO;
using Agent.Common;
using Agent.Common.Agents;

namespace AgentZeroWpf.Services;

/// <summary>
/// Injects the AgentZero discovery STUB into each Claude Code profile's skills
/// folder (mission W5, orca-adoption). Only the stub is written; the full guide
/// is served at runtime by <c>-cli help agentzero</c> (anti-drift). Discovery
/// mirrors <see cref="AgentHookInstaller"/> (~/.claude* enumeration).
/// </summary>
public static class SkillStubInjector
{
    private static readonly string UserProfile =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    private const string SkillDirName = "agentzero-control";

    public sealed record StubResult(string AccountKey, bool Ok, string Action, string? Error);

    private static IEnumerable<(string AccountKey, string ConfigDir)> Profiles()
    {
        if (!Directory.Exists(UserProfile)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(UserProfile, ".claude*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(Path.Combine(dir, "projects")) && !File.Exists(Path.Combine(dir, "settings.json")))
                continue;
            var name = Path.GetFileName(dir).TrimStart('.');
            if (string.IsNullOrEmpty(name)) name = "claude";
            yield return (name, dir);
        }
    }

    public static IReadOnlyList<StubResult> InstallAll()
    {
        var results = new List<StubResult>();
        foreach (var (acct, dir) in Profiles())
        {
            try
            {
                var skillDir = Path.Combine(dir, "skills", SkillDirName);
                Directory.CreateDirectory(skillDir);
                var path = Path.Combine(skillDir, "SKILL.md");
                var existed = File.Exists(path);
                File.WriteAllText(path, AgentSkillGuides.BuildStub());
                results.Add(new StubResult(acct, true, existed ? "updated" : "installed", null));
            }
            catch (System.Exception ex)
            {
                results.Add(new StubResult(acct, false, "failed", ex.Message));
            }
        }
        return results;
    }

    public static IReadOnlyList<StubResult> UninstallAll()
    {
        var results = new List<StubResult>();
        foreach (var (acct, dir) in Profiles())
        {
            try
            {
                var skillDir = Path.Combine(dir, "skills", SkillDirName);
                var path = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(path)) { results.Add(new StubResult(acct, true, "noop", null)); continue; }
                // Only remove if it is our stub (marker match) — never delete a foreign skill.
                if (File.ReadAllText(path).Contains(AgentSkillGuides.StubMarker))
                {
                    File.Delete(path);
                    try { if (Directory.GetFileSystemEntries(skillDir).Length == 0) Directory.Delete(skillDir); } catch { }
                    results.Add(new StubResult(acct, true, "removed", null));
                }
                else results.Add(new StubResult(acct, true, "skipped (foreign)", null));
            }
            catch (System.Exception ex)
            {
                results.Add(new StubResult(acct, false, "failed", ex.Message));
            }
        }
        return results;
    }
}
