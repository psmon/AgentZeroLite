using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Common.Services;

namespace Agent.Common.Agents;

/// <summary>One prior conversation an agent CLI can be relaunched into.</summary>
/// <param name="Id">The agent's own session id — what <c>claude --resume</c> / <c>codex resume</c> take.</param>
/// <param name="Title">The agent's title for it (Claude's ai/custom title, Codex's thread name), or null.</param>
/// <param name="Preview">The first thing the person typed, one line, or null.</param>
/// <param name="LastActivityUtc">When the session file was last written.</param>
/// <param name="SizeBytes">The session file's size — a rough "how much happened".</param>
public sealed record AgentSessionInfo(string Id, string? Title, string? Preview, DateTime LastActivityUtc, long SizeBytes)
{
    /// <summary>What a list shows first: the title, else the first prompt, else the id.</summary>
    public string Headline => !string.IsNullOrWhiteSpace(Title) ? Title!
        : !string.IsNullOrWhiteSpace(Preview) ? Preview!
        : Id;
}

/// <summary>
/// The sessions an agent CLI has already recorded for a folder, newest first — read from
/// the agent's own store, so the list is the one the agent itself would offer:
/// <list type="bullet">
/// <item>Claude Code: <c>$CLAUDE_CONFIG_DIR</c> (default <c>~/.claude</c>)
/// <c>/projects/&lt;slug(cwd)&gt;/&lt;sessionId&gt;.jsonl</c>.</item>
/// <item>Codex: <c>$CODEX_HOME</c> (default <c>~/.codex</c>)
/// <c>/sessions/YYYY/MM/DD/rollout-…-&lt;uuid&gt;.jsonl</c>, whose first record
/// (<c>session_meta</c>) names the cwd; titles from <c>session_index.jsonl</c>.</item>
/// </list>
/// Only the store of the profile the tab will actually run is read: an id from another
/// <c>~/.claude*</c> profile would not resume there ("No conversation found").
///
/// <para>Session files grow large (26 MB measured for one long Claude session), so a file
/// is never read whole: a bounded head for the first prompt and a bounded tail for the
/// latest title. Lines cut by the bound are skipped, not parsed.</para>
/// </summary>
public static class AgentSessionCatalog
{
    private const int HeadBytes = 128 * 1024;
    private const int TailBytes = 96 * 1024;

    /// <summary>
    /// A second, longer look at a Claude head when the first found only a bare command: a
    /// skill's expansion and the CLAUDE.md attachments sit between "/skill" and the first
    /// typed prompt, and measured, they pushed it past 128 KB.
    /// </summary>
    private const int LongHeadBytes = 640 * 1024;
    private const int PreviewChars = 160;

    /// <summary>The sessions <paramref name="tool"/> has for <paramref name="cwd"/>, or empty for a tool without a reader.</summary>
    /// <param name="env">The tab's environment — where <c>CLAUDE_CONFIG_DIR</c> / <c>CODEX_HOME</c> are looked up.</param>
    public static IReadOnlyList<AgentSessionInfo> List(AgentCliTool tool, string cwd,
        IReadOnlyDictionary<string, string>? env = null, int max = 30, string? homeDir = null)
    {
        if (ReferenceEquals(tool, AgentCliTools.Claude))
            return ListClaude(cwd, ConfigDir(env, "CLAUDE_CONFIG_DIR", ".claude", homeDir), max);
        if (ReferenceEquals(tool, AgentCliTools.Codex))
            return ListCodex(cwd, ConfigDir(env, "CODEX_HOME", ".codex", homeDir), max);
        return Array.Empty<AgentSessionInfo>();
    }

    /// <summary>
    /// The sessions <paramref name="tool"/> has for <paramref name="cwd"/> in one named profile
    /// folder — a wrapper's (<see cref="AgentProfileCommand"/>), whose folder is set inside the
    /// wrapper and so is not in the tab's environment.
    /// </summary>
    public static IReadOnlyList<AgentSessionInfo> ListIn(AgentCliTool tool, string cwd, string configDir, int max = 30)
    {
        if (ReferenceEquals(tool, AgentCliTools.Claude)) return ListClaude(cwd, configDir, max);
        if (ReferenceEquals(tool, AgentCliTools.Codex)) return ListCodex(cwd, configDir, max);
        return Array.Empty<AgentSessionInfo>();
    }

    /// <summary>Whether <see cref="List"/> knows how to read this tool's store.</summary>
    public static bool Supports(AgentCliTool? tool) =>
        ReferenceEquals(tool, AgentCliTools.Claude) || ReferenceEquals(tool, AgentCliTools.Codex);

    private static string ConfigDir(IReadOnlyDictionary<string, string>? env, string variable, string defaultName, string? homeDir)
    {
        if (env is not null && env.TryGetValue(variable, out var v) && !string.IsNullOrWhiteSpace(v))
            return v.Trim().Trim('"');
        if (homeDir is null)
        {
            var own = Environment.GetEnvironmentVariable(variable);
            if (env is null && !string.IsNullOrWhiteSpace(own)) return own.Trim().Trim('"');
        }
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, defaultName);
    }

    // ── Claude ───────────────────────────────────────────────────────────────

    /// <summary>Claude sessions for <paramref name="cwd"/> under one config dir, newest first.</summary>
    public static IReadOnlyList<AgentSessionInfo> ListClaude(string cwd, string configDir, int max = 30)
    {
        var result = new List<AgentSessionInfo>();
        if (string.IsNullOrWhiteSpace(cwd)) return result;
        var projectDir = ClaudeProjectDir(configDir, cwd);
        if (projectDir is null) return result;

        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(projectDir).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }
        catch { return result; }

        foreach (var f in files)
        {
            if (result.Count >= max) break;
            var id = Path.GetFileNameWithoutExtension(f.Name);
            if (!AgentResumeCatalog.IsSafeId(id)) continue;
            var (preview, headTitle, bare) = ReadClaudeHead(f.FullName, HeadBytes);
            if (bare && f.Length > HeadBytes)
            {
                var (longer, _, stillBare) = ReadClaudeHead(f.FullName, LongHeadBytes);
                if (!stillBare) preview = longer;
            }
            // No prompt at all: a session that was opened and left (or only a snapshot) —
            // Claude's own picker leaves these out, and resuming one shows nothing.
            if (preview is null) continue;
            var title = ReadClaudeTailTitle(f.FullName) ?? headTitle;
            result.Add(new AgentSessionInfo(id, title, preview, f.LastWriteTimeUtc, f.Length));
        }
        return result;
    }

    /// <summary>
    /// The project folder Claude keeps <paramref name="cwd"/>'s sessions in. Claude replaces
    /// every character that is not an ASCII letter or digit with '-'
    /// (<c>C:\a\.claude\x</c> → <c>C--a--claude-x</c>); a very long slug is cut and given a
    /// suffix, so a prefix match is the fallback.
    /// </summary>
    internal static string? ClaudeProjectDir(string configDir, string cwd)
    {
        try
        {
            var projects = Path.Combine(configDir, "projects");
            if (!Directory.Exists(projects)) return null;
            var slug = ClaudeSessionLocator.Slug(cwd.TrimEnd('\\', '/'));
            var exact = Path.Combine(projects, slug);
            if (Directory.Exists(exact)) return exact;
            if (slug.Length > 200)
                return Directory.EnumerateDirectories(projects, slug[..200] + "*").FirstOrDefault();
            // Case differs between how the folder was typed then and now (C:\ vs c:\) on Windows.
            if (OperatingSystem.IsWindows())
                return Directory.EnumerateDirectories(projects)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), slug, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return null;
    }

    /// <returns>The first prompt (or a bare command when that is all there was), the first title, and whether it was a bare command.</returns>
    private static (string? Preview, string? Title, bool Bare) ReadClaudeHead(string path, int budget)
    {
        string? preview = null, bareCommand = null, title = null;
        foreach (var line in ReadLines(path, fromEnd: false, budget))
        {
            if (line.Contains("\"ai-title\"") || line.Contains("\"custom-title\"") || line.Contains("\"summary\""))
                title ??= ClaudeTitleOf(line);
            if (preview is not null) { if (title is not null) break; continue; }
            if (!line.Contains("\"type\":\"user\"")) continue;
            var prompt = ClaudePromptOf(line, out var bare);
            // "/clear" or a skill invoked with no words says little; the next real prompt says more.
            if (bare) bareCommand ??= prompt;
            else preview = prompt;
        }
        return (preview ?? bareCommand, title, preview is null && bareCommand is not null);
    }

    private static string? ReadClaudeTailTitle(string path)
    {
        string? ai = null, custom = null;
        foreach (var line in ReadLines(path, fromEnd: true, TailBytes))
        {
            if (!line.Contains("-title\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = Str(root, "type");
                if (type == "custom-title" && Str(root, "customTitle") is { Length: > 0 } c) custom = c;
                else if (type == "ai-title" && Str(root, "aiTitle") is { Length: > 0 } a) ai = a;
            }
            catch { }
        }
        // A name the person gave (/rename) wins over the one the model made up.
        return custom ?? ai;
    }

    private static string? ClaudeTitleOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return Str(root, "type") switch
            {
                "custom-title" => Str(root, "customTitle"),
                "ai-title" => Str(root, "aiTitle"),
                "summary" => Str(root, "summary"),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>The person's words in one Claude <c>user</c> record, or null when it is not a typed prompt.</summary>
    internal static string? ClaudePromptOf(string line) => ClaudePromptOf(line, out _);

    /// <param name="bareCommand">The prompt is a slash command with no arguments.</param>
    internal static string? ClaudePromptOf(string line, out bool bareCommand)
    {
        bareCommand = false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (Str(root, "type") != "user") return null;
            if (Bool(root, "isMeta") || Bool(root, "isSidechain")) return null;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return null;

            string? text = null;
            if (content.ValueKind == JsonValueKind.String) text = content.GetString();
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (Str(part, "type") == "tool_result") return null; // a tool's answer, not a prompt
                    if (Str(part, "type") == "text") { text = Str(part, "text"); break; }
                }
            }
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();

            // A slash command is recorded as tags; show the command itself.
            var command = Regex.Match(text, "<command-name>(.*?)</command-name>", RegexOptions.Singleline);
            if (command.Success)
            {
                var args = Regex.Match(text, "<command-args>(.*?)</command-args>", RegexOptions.Singleline);
                var words = args.Success ? args.Groups[1].Value.Trim() : "";
                bareCommand = words.Length == 0;
                return OneLine((command.Groups[1].Value + " " + words).Trim());
            }
            // Harness-written records (command output, caveats, interrupts) are not prompts.
            if (text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal)
                || text.StartsWith("[Request interrupted", StringComparison.Ordinal))
                return null;
            return OneLine(text);
        }
        catch { return null; }
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    private static readonly Regex CodexCwd = new("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex CodexThreadSource = new("\"thread_source\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex CodexIdInName = new("([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", RegexOptions.Compiled);

    /// <summary>How many of the newest rollout files are opened looking for this folder's sessions.</summary>
    private const int CodexScanLimit = 400;

    /// <summary>Codex sessions for <paramref name="cwd"/> under one <c>CODEX_HOME</c>, newest first.</summary>
    public static IReadOnlyList<AgentSessionInfo> ListCodex(string cwd, string codexHome, int max = 30)
    {
        var result = new List<AgentSessionInfo>();
        if (string.IsNullOrWhiteSpace(cwd)) return result;
        var sessions = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessions)) return result;

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(sessions).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(CodexScanLimit).ToList();
        }
        catch { return result; }

        var want = NormalizeDir(cwd);
        var found = new List<(FileInfo File, string Id, string? Preview)>();
        foreach (var f in files)
        {
            if (found.Count >= max) break;
            var nameMatch = CodexIdInName.Match(Path.GetFileNameWithoutExtension(f.Name));
            if (!nameMatch.Success) continue;
            var (fileCwd, preview) = ReadCodexHead(f.FullName);
            if (fileCwd is null || !SameDir(NormalizeDir(fileCwd), want)) continue;
            found.Add((f, nameMatch.Groups[1].Value, preview));
        }
        if (found.Count == 0) return result;

        var ids = found.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titles = ReadCodexTitles(codexHome, ids);
        var firstPrompts = found.Any(x => x.Preview is null) ? ReadCodexHistory(codexHome, ids) : null;

        foreach (var (f, id, preview) in found)
        {
            titles.TryGetValue(id, out var title);
            var p = preview;
            if (p is null) firstPrompts?.TryGetValue(id, out p);
            result.Add(new AgentSessionInfo(id, title, p, f.LastWriteTimeUtc, f.Length));
        }
        return result;
    }

    /// <summary>
    /// The cwd from the first record (<c>session_meta</c>) and the first prompt. The first
    /// record carries the model's base instructions and can be longer than the head bound,
    /// so the cwd is read by pattern off the raw text rather than by parsing that line.
    /// </summary>
    private static (string? Cwd, string? Preview) ReadCodexHead(string path)
    {
        string raw;
        try { raw = ReadChunk(path, fromEnd: false, HeadBytes); }
        catch { return (null, null); }

        var firstLineEnd = raw.IndexOf('\n');
        var first = firstLineEnd < 0 ? raw : raw[..firstLineEnd];
        if (!first.Contains("session_meta")) return (null, null);
        // Threads Codex starts for itself (the guardian approval reviewer, other sub-agents)
        // carry their own cwd but are not conversations a person had; `codex resume` hides them.
        if (CodexThreadSource.Match(first) is { Success: true } ts && ts.Groups[1].Value != "user") return (null, null);
        if (first.Contains("\"subagent\"")) return (null, null);
        var m = CodexCwd.Match(first);
        if (!m.Success) return (null, null);
        string? cwd;
        try
        {
            using var lit = JsonDocument.Parse("\"" + m.Groups[1].Value + "\"");
            cwd = lit.RootElement.GetString();
        }
        catch { return (null, null); }

        string? preview = null;
        foreach (var line in CompleteLines(raw, fromEnd: false).Skip(1))
        {
            preview = CodexPromptOf(line);
            if (preview is not null) break;
        }
        return (cwd, preview);
    }

    /// <summary>The person's words in one Codex record, or null.</summary>
    internal static string? CodexPromptOf(string line)
    {
        if (!line.Contains("user_message") && !line.Contains("\"role\":\"user\"")) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return null;
            string? text = null;
            var type = Str(root, "type");
            if (type == "event_msg" && Str(payload, "type") == "user_message")
                text = Str(payload, "message");
            else if (type == "response_item" && Str(payload, "type") == "message" && Str(payload, "role") == "user"
                     && payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                    if (Str(part, "type") == "input_text") { text = Str(part, "text"); break; }
            }
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            // Context Codex injects as a "user" message (environment, AGENTS.md, instructions).
            if (text.StartsWith('<') || text.StartsWith("# AGENTS.md", StringComparison.Ordinal)) return null;
            return OneLine(text);
        }
        catch { return null; }
    }

    /// <summary><c>session_index.jsonl</c>: id → the latest thread name.</summary>
    private static Dictionary<string, string> ReadCodexTitles(string codexHome, HashSet<string> ids)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(codexHome, "session_index.jsonl");
        foreach (var line in ReadAllLinesShared(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Str(root, "id") is { } id && ids.Contains(id) && Str(root, "thread_name") is { Length: > 0 } name)
                    titles[id] = name; // later lines are renames
            }
            catch { }
        }
        return titles;
    }

    /// <summary><c>history.jsonl</c>: id → the first prompt (used when the head did not reach one).</summary>
    private static Dictionary<string, string> ReadCodexHistory(string codexHome, HashSet<string> ids)
    {
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(codexHome, "history.jsonl");
        foreach (var line in ReadAllLinesShared(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Str(root, "session_id") is { } id && ids.Contains(id) && !first.ContainsKey(id)
                    && Str(root, "text") is { Length: > 0 } text)
                    first[id] = OneLine(text.Trim());
            }
            catch { }
        }
        return first;
    }

    private static string NormalizeDir(string dir)
    {
        try { dir = Path.GetFullPath(dir); } catch { }
        return dir.TrimEnd('\\', '/');
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ── bounded reading ──────────────────────────────────────────────────────

    private static IEnumerable<string> ReadLines(string path, bool fromEnd, int budget)
    {
        string raw;
        try { raw = ReadChunk(path, fromEnd, budget); }
        catch { yield break; }
        foreach (var line in CompleteLines(raw, fromEnd)) yield return line;
    }

    /// <summary>Up to <paramref name="budget"/> bytes from the start or the end, opened so a writing agent is not disturbed.</summary>
    private static string ReadChunk(string path, bool fromEnd, int budget)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        var take = (int)Math.Min(budget, length);
        if (fromEnd) fs.Seek(length - take, SeekOrigin.Begin);
        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = fs.Read(buffer, read, take - read);
            if (n <= 0) break;
            read += n;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        // Mark which ends were cut so CompleteLines drops the partial line there.
        var cutStart = fromEnd && take < length;
        var cutEnd = !fromEnd && take < length;
        return (cutStart ? "\u0001" : "") + text + (cutEnd ? "\u0001" : "");
    }

    /// <summary>The whole lines in a chunk — a line cut by the bound is dropped rather than half-parsed.</summary>
    private static IEnumerable<string> CompleteLines(string raw, bool fromEnd)
    {
        var lines = raw.Split('\n');
        var start = 0;
        var end = lines.Length;
        if (raw.StartsWith('\u0001')) start = 1;
        if (raw.EndsWith('\u0001')) end -= 1;
        for (var i = start; i < end; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0 && line[0] == '\u0001') line = line[1..];
            if (line.Length > 0 && line[^1] == '\u0001') line = line[..^1];
            if (line.Length > 0) yield return line;
        }
    }

    private static IEnumerable<string> ReadAllLinesShared(string path)
    {
        if (!File.Exists(path)) yield break;
        FileStream? fs = null;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch { yield break; }
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while (true)
        {
            try { line = reader.ReadLine(); }
            catch { yield break; }
            if (line is null) yield break;
            if (line.Length > 0) yield return line;
        }
    }

    private static string OneLine(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= PreviewChars ? flat : flat[..PreviewChars].TrimEnd() + "…";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
