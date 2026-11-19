using System.Text;
using System.Text.Json;

namespace Agent.Common.Agents;

/// <summary>Which agent conversation a terminal is running, when the agent told us (its status file).</summary>
/// <param name="Agent">"claude" — the only agent whose conversation is located this way so far.</param>
/// <param name="ConfigDir">The profile the status file came from (<c>~/.claude</c> or <c>$CLAUDE_CONFIG_DIR</c>).</param>
public sealed record AgentSessionRef(string Agent, string ConfigDir, string SessionId, string Cwd);

/// <summary>One sub-agent a Claude session started (the Agent / Task tool), as a web plugin sees it.</summary>
/// <param name="Id">The sub-agent's id (<c>agent-&lt;id&gt;.jsonl</c>).</param>
/// <param name="ParentId">The sub-agent that started this one, null when the main conversation did.</param>
/// <param name="Type">Its agent type: "Explore", "general-purpose", "Plan", …</param>
/// <param name="State">"working", "done", or "stopped" (no end recorded and quiet for <see cref="ClaudeSubagentReader.QuietStopMs"/>).</param>
/// <param name="Tool">The last tool it called, with a short hint ("Read · Program.cs").</param>
/// <param name="Tools">How many tool calls it has made.</param>
/// <param name="StartedAt">Unix ms of its first record.</param>
/// <param name="EndedAt">Unix ms it finished or was called stopped; null while working.</param>
/// <param name="Expert">The garden-harness expert it was launched as (<see cref="KakashiHarness.ExpertOf"/>) — Claude only; null otherwise.</param>
public sealed record SubagentView(
    string Id, string? ParentId, string Type, string Description, int Depth, bool Background,
    string State, string? Tool, int Tools, long StartedAt, long? EndedAt, HarnessExpert? Expert = null);

/// <summary>
/// The sub-agents a Claude Code session is running, read from its own transcript files — no hook.
/// Claude keeps each one under <c>&lt;config&gt;/projects/&lt;slug(cwd)&gt;/&lt;sessionId&gt;/subagents/</c>
/// as <c>agent-&lt;id&gt;.meta.json</c> (<c>agentType</c>, <c>description</c>, <c>toolUseId</c>,
/// <c>spawnDepth</c>, <c>requestShape</c>) beside <c>agent-&lt;id&gt;.jsonl</c>, its own transcript.
/// Measured on 2.1.310: a finished one's last record is an assistant message with
/// <c>stop_reason: end_turn</c> (it may also hand back through a <c>SubagentHandback</c> call).
///
/// <para>Transcripts grow to hundreds of KB, and the host asks every 1.5 s on its UI thread, so each
/// file is read incrementally (only the bytes appended since the last pass) and a file nobody has
/// written to for longer than anything here would still show is not opened at all.</para>
/// </summary>
public sealed class ClaudeSubagentReader
{
    private readonly Func<DateTimeOffset> _now;
    private readonly HarnessLocator? _harness;
    private readonly Dictionary<string, Transcript> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a finished sub-agent stays in the list, so the stage can show what just ran.</summary>
    public long KeepFinishedMs { get; init; } = 120_000;

    /// <summary>A sub-agent with no end recorded and nothing written for this long was stopped or lost.</summary>
    public long QuietStopMs { get; init; } = 600_000;

    /// <summary>The most a session reports — the newest first.</summary>
    public int MaxShown { get; init; } = 24;

    /// <param name="harness">When given, a sub-agent launched as an expert of the session's garden harness says which.</param>
    public ClaudeSubagentReader(Func<DateTimeOffset>? now = null, HarnessLocator? harness = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _harness = harness;
    }

    public static string FolderOf(AgentSessionRef s) =>
        Path.Combine(s.ConfigDir, "projects", ClaudeSessionLocator.Slug(s.Cwd), s.SessionId, "subagents");

    /// <summary>Working sub-agents, and the ones that ended within <see cref="KeepFinishedMs"/>; oldest first.</summary>
    public IReadOnlyList<SubagentView> Read(AgentSessionRef session)
    {
        var dir = FolderOf(session);
        if (!Directory.Exists(dir)) return [];

        var now = _now();
        var nowMs = now.ToUnixTimeMilliseconds();
        var horizon = now.UtcDateTime.AddMilliseconds(-(QuietStopMs + KeepFinishedMs));
        List<string> metas;
        try { metas = Directory.EnumerateFiles(dir, "agent-*.meta.json").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var found = new List<(Meta Meta, Transcript T)>();
        foreach (var metaPath in metas)
        {
            var jsonl = metaPath[..^".meta.json".Length] + ".jsonl";
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(jsonl); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (written < horizon) { _cache.Remove(jsonl); continue; }   // cannot be shown: skip the read

            if (!_cache.TryGetValue(jsonl, out var t))
            {
                if (Meta.Load(metaPath) is not { } meta) continue;
                _cache[jsonl] = t = new Transcript(jsonl, meta);
            }
            t.CatchUp();
            found.Add((t.Meta, t));
        }

        // A nested sub-agent's toolUseId is a tool call in its parent's transcript.
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, t) in found)
            foreach (var call in t.AgentCalls) parentOf[call] = t.Id;

        var roster = _harness?.Find(session.Cwd);
        var views = new List<SubagentView>();
        foreach (var (meta, t) in found)
        {
            string state;
            long? ended;
            if (t.EndedAt is { } e) { state = "done"; ended = e; }
            // Ended when it was called stopped, not when it last wrote — or it would leave the list
            // the moment it was noticed.
            else if (nowMs - t.LastAt > QuietStopMs) { state = "stopped"; ended = t.LastAt + QuietStopMs; }
            else { state = "working"; ended = null; }
            if (ended is { } end && nowMs - end > KeepFinishedMs) continue;

            views.Add(new SubagentView(t.Id, meta.ToolUseId is { } id && parentOf.TryGetValue(id, out var p) ? p : null,
                meta.Type, meta.Description, meta.Depth, meta.Background, state, t.LastTool, t.ToolCount,
                t.StartedAt, ended, roster is null ? null : KakashiHarness.ExpertOf(roster, t.Prompt, meta.Description)));
        }
        return views.OrderBy(v => v.StartedAt).TakeLast(MaxShown).ToList();
    }

    private sealed record Meta(string Type, string Description, string? ToolUseId, int Depth, bool Background)
    {
        public static Meta? Load(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                return new Meta(Str(r, "agentType") ?? "agent", Str(r, "description") ?? "", Str(r, "toolUseId"),
                    r.TryGetProperty("spawnDepth", out var d) && d.TryGetInt32(out var n) ? n : 1,
                    Str(r, "requestShape") == "background");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;   // being written: the next pass reads it whole
            }
        }
    }

    /// <summary>One sub-agent transcript, folded as it grows.</summary>
    private sealed class Transcript(string path, Meta meta)
    {
        private long _offset;
        private bool _handedBack;
        private readonly StringBuilder _partial = new();

        public Meta Meta { get; } = meta;
        public string Id { get; } = Path.GetFileName(path)["agent-".Length..^".jsonl".Length];
        public long StartedAt { get; private set; }
        public long LastAt { get; private set; }
        public long? EndedAt { get; private set; }
        public string? LastTool { get; private set; }
        public int ToolCount { get; private set; }
        public List<string> AgentCalls { get; } = [];

        /// <summary>The head of the first user message — the prompt it was launched with (whom it is to act as).</summary>
        public string? Prompt { get; private set; }

        public void CatchUp()
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _offset) { _offset = 0; _partial.Clear(); }   // rewritten: start over
                if (fs.Length == _offset) return;
                fs.Seek(_offset, SeekOrigin.Begin);
                var buf = new byte[fs.Length - _offset];
                var read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                _offset += read;
                _partial.Append(Encoding.UTF8.GetString(buf, 0, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            var text = _partial.ToString();
            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;                       // no whole line yet
            _partial.Clear().Append(text, lastNl + 1, text.Length - lastNl - 1);
            foreach (var line in text[..lastNl].Split('\n'))
                if (line.Length > 1) Fold(line);
        }

        private void Fold(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("user" or "assistant")) return;
                if (Str(r, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var at))
                {
                    var ms = at.ToUnixTimeMilliseconds();
                    if (StartedAt == 0) StartedAt = ms;
                    LastAt = ms;
                }
                if (!_handedBack) EndedAt = null;   // anything after an end_turn means it went on
                if (type == "user" && Prompt is null && r.TryGetProperty("message", out var first))
                    Prompt = TextOf(first, PromptChars);
                if (type != "assistant" || !r.TryGetProperty("message", out var msg)) return;

                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var c in content.EnumerateArray())
                    {
                        if (Str(c, "type") != "tool_use" || Str(c, "name") is not { } name) continue;
                        ToolCount++;
                        if (name == "SubagentHandback") { _handedBack = true; EndedAt = LastAt; continue; }
                        LastTool = c.TryGetProperty("input", out var input) && Hint(input) is { } h ? $"{name} · {h}" : name;
                        if (name is "Agent" or "Task" && Str(c, "id") is { } id) AgentCalls.Add(id);
                    }
                if (Str(msg, "stop_reason") == "end_turn") EndedAt = LastAt;
            }
            catch (JsonException) { /* a torn line — skipped, the rest still folds */ }
        }
    }

    /// <summary>How much of a launch prompt is kept — its opening says whom the sub-agent is to act as.</summary>
    internal const int PromptChars = 4000;

    /// <summary>A message's text: <c>content</c> as a string, or its text parts joined; cut at <paramref name="max"/>.</summary>
    internal static string TextOf(JsonElement message, int max)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content)) return "";
        string text;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var c in content.EnumerateArray())
                if (Str(c, "type") == "text" && Str(c, "text") is { } t)
                {
                    sb.Append(t).Append('\n');
                    if (sb.Length >= max) break;
                }
            text = sb.ToString();
        }
        else return "";
        return text.Length > max ? text[..max] : text;
    }

    /// <summary>The argument that says what a call is about, cut short (shared with <see cref="HermesSubagentReader"/>).</summary>
    internal static string? Hint(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "description", "file_path", "path", "pattern", "url", "query", "command", "prompt" })
        {
            if (Str(input, key) is not { Length: > 0 } v) continue;
            // Split on both separators by hand: Path.GetFileName on macOS/Linux does not treat '\' as
            // one, and a transcript holds the paths of the OS Claude ran on, not this one.
            if (key is "file_path" or "path")
            {
                var trimmed = v.TrimEnd('/', '\\');
                var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
                if (name.Length > 0) v = name;
            }
            v = v.ReplaceLineEndings(" ").Trim();
            return v.Length > 48 ? v[..48] + "…" : v;
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
