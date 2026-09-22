using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOne.Services;

/// <summary>One saved session, as the resume list shows it.</summary>
/// <param name="Title">The task title the session ended on, or its first prompt when none was made.</param>
public sealed record SessionSummary(string Path, string Id, DateTime Started, string Title, int Turns, string FirstPrompt);

/// <summary>
/// Everything agent-one remembers about one workspace, under
/// <c>~/.agent-one/workspaces/&lt;name-hash&gt;/</c>: its sessions, and a memory
/// file — a rolling log of what was done there, capped at 50 000 characters —
/// that the next session starts from. Without it a new chat in the same folder
/// knew nothing of the project it had been building a minute earlier.
/// </summary>
public sealed class WorkspaceStore
{
    /// <summary>How much memory is kept on disk. Older entries fall off the top.</summary>
    public const int MemoryCapChars = 50_000;

    /// <summary>How much of it (the newest part) goes into the model's prompt each turn.</summary>
    public const int MemoryPromptChars = 6_000;

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }
    public string Dir { get; }
    public string SessionsDir => Path.Combine(Dir, "sessions");
    public string MemoryPath => Path.Combine(Dir, "memory.md");

    public WorkspaceStore(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Dir = AppPaths.WorkspaceDir(Root);
    }

    /// <summary>Creates the folder and writes where it came from, so a listing of ~/.agent-one is readable.</summary>
    public WorkspaceStore Ensure()
    {
        Directory.CreateDirectory(SessionsDir);
        var meta = Path.Combine(Dir, "workspace.json");
        if (!File.Exists(meta))
            File.WriteAllText(meta, JsonSerializer.Serialize(new WorkspaceMeta { Root = Root }, AgentOneJson.Default.WorkspaceMeta), NoBom);
        return this;
    }

    // ------------------------------------------------------------- memory

    public string ReadMemory()
    {
        try { return File.Exists(MemoryPath) ? File.ReadAllText(MemoryPath) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The newest part of the memory, for the prompt. Cut at an entry boundary.</summary>
    public string MemoryForPrompt()
    {
        var all = ReadMemory();
        if (all.Length <= MemoryPromptChars) return all.Trim();

        var tail = all[^MemoryPromptChars..];
        var boundary = tail.IndexOf("\n## ", StringComparison.Ordinal);
        return (boundary >= 0 ? tail[(boundary + 1)..] : tail).Trim();
    }

    /// <summary>Appends one entry and trims the oldest ones past the cap. Best effort, never throws.</summary>
    public void Remember(string entry)
    {
        try
        {
            Ensure();
            var all = ReadMemory();
            all = (all.Length == 0 ? "" : all.TrimEnd() + "\n\n") + entry.Trim() + "\n";

            if (all.Length > MemoryCapChars)
            {
                var cut = all[^MemoryCapChars..];
                var boundary = cut.IndexOf("\n## ", StringComparison.Ordinal);
                all = boundary >= 0 ? cut[(boundary + 1)..] : cut;
            }

            File.WriteAllText(MemoryPath, all, NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Memory is a convenience; a locked file must not fail the turn.
        }
    }

    public int MemoryChars => ReadMemory().Length;

    // ----------------------------------------------------------- sessions

    /// <summary>This workspace's sessions, newest first.</summary>
    public IReadOnlyList<SessionSummary> ListSessions(int limit = 20)
    {
        if (!Directory.Exists(SessionsDir)) return [];

        var list = new List<SessionSummary>();
        foreach (var path in Directory.EnumerateFiles(SessionsDir, "*.jsonl").OrderByDescending(p => p, StringComparer.Ordinal))
        {
            if (Summarize(path) is { } summary) list.Add(summary);
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>One session's listing line, from its file. Null for an empty or unreadable one.</summary>
    public static SessionSummary? Summarize(string path)
    {
        var entries = ReadEntries(path);
        var prompts = entries.Where(e => e.Kind == "prompt").ToList();
        if (prompts.Count == 0) return null;

        var title = entries.LastOrDefault(e => e.Kind == "title")?.Text;
        var first = FirstLine(prompts[0].Text, 60);
        var id = Path.GetFileNameWithoutExtension(path);

        DateTime started;
        if (!DateTime.TryParse(prompts[0].Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out started))
            started = File.GetCreationTime(path);

        return new SessionSummary(path, id, started.ToLocalTime(), string.IsNullOrWhiteSpace(title) ? first : title!, prompts.Count, first);
    }

    /// <summary>Every entry of a session file, in order. Unparseable lines are skipped.</summary>
    public static IReadOnlyList<SessionEntry> ReadEntries(string path)
    {
        var entries = new List<SessionEntry>();
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return entries; }

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, AgentOneWireJson.Default.SessionEntry) is { } entry) entries.Add(entry);
            }
            catch (JsonException) { /* a half-written line at the end */ }
        }
        return entries;
    }

    public static string FirstLine(string text, int max)
    {
        var first = text.Split('\n')[0].Trim();
        return first.Length > max ? first[..max] + "…" : first;
    }
}

/// <summary>What a workspace folder under ~/.agent-one is for.</summary>
public sealed class WorkspaceMeta
{
    [System.Text.Json.Serialization.JsonPropertyName("root")]
    public string Root { get; set; } = "";
}
