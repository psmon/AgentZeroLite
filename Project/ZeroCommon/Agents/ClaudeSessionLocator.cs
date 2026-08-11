using System;
using System.IO;
using System.Linq;

namespace Agent.Common.Agents;

/// <summary>
/// Discovers a Claude Code conversation's native session id from a working
/// directory (herdr-adoption H3). Claude stores each conversation at
/// <c>~/.claude*/projects/&lt;slugged-cwd&gt;/&lt;sessionId&gt;.jsonl</c>; this finds the most
/// recent one so AgentZero can offer to resume it (<c>claude --resume &lt;id&gt;</c>)
/// without needing a hook. Pure IO; the home dir is injectable for testing.
/// </summary>
public static class ClaudeSessionLocator
{
    /// <summary>
    /// Slugs a cwd the way Claude Code names its <c>projects/</c> subfolders:
    /// path separators and the drive colon become '-' (e.g.
    /// <c>C:\code\psmon\CodeScan</c> → <c>C--code-psmon-CodeScan</c>).
    /// </summary>
    public static string Slug(string cwd)
    {
        var chars = (cwd ?? "").Select(c => c is '\\' or '/' or ':' ? '-' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Returns the most-recent Claude session id for <paramref name="cwd"/> across
    /// all <c>~/.claude*</c> profiles, or null if none found.
    /// </summary>
    public static string? FindLatestSessionId(string cwd, string? homeDir = null)
    {
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(homeDir)) return null;

        var slug = Slug(cwd);
        string? bestId = null;
        DateTime bestTime = DateTime.MinValue;

        try
        {
            foreach (var profile in Directory.EnumerateDirectories(homeDir, ".claude*"))
            {
                var projectDir = Path.Combine(profile, "projects", slug);
                if (!Directory.Exists(projectDir)) continue;
                foreach (var f in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime)
                    {
                        bestTime = t;
                        bestId = Path.GetFileNameWithoutExtension(f);
                    }
                }
            }
        }
        catch { return bestId; }

        return bestId;
    }

    /// <summary>
    /// Builds the resume launch command for a Claude cwd (discovery + command),
    /// or null if no prior session is found.
    /// </summary>
    public static string? BuildResumeCommand(string cwd, string? homeDir = null)
    {
        var id = FindLatestSessionId(cwd, homeDir);
        return id is null ? null : AgentResumeCatalog.BuildResumeCommand("claude", id);
    }
}
