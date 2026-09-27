using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOne.Graph;
using AgentOne.Services;

namespace AgentOne.Dashboard;

/// <summary>
/// Everything the dashboard shows, read from what agent-one left under
/// <c>~/.agent-one/workspaces/</c>: each workspace's memory.md, its session
/// transcripts and its Kùzu knowledge graph. Read only, by construction —
/// files are read, never written, and every graph is opened with Kùzu's
/// read-only flag, so a Cypher that tries to CREATE or DELETE is refused by
/// the database rather than by a keyword filter here.
///
/// A graph is opened per request and closed again. Kùzu allows one writer
/// per database across processes, and a chat or background session in that
/// workspace is that writer: holding the file open here between requests
/// would make the next agent-one run there start without its memory.
/// </summary>
public sealed partial class DashboardData
{
    public const string All = "all";

    /// <summary>Per node table, how many rows the graph view loads. The newest win when a table has more.</summary>
    public const int DefaultNodeLimit = 150;
    public const int MaxNodeLimit = 2000;
    public const int MaxCypherRows = 1000;
    private const int MaxEdgesPerRel = 5000;
    private const int PropChars = 1500;

    /// <summary>
    /// Buffer pool for the dashboard's brief read-only opens. Kùzu's default is
    /// a share of physical memory; a page that opens every workspace's graph in
    /// turn has no use for that much.
    /// </summary>
    private const ulong ReaderBufferPool = 64UL * 1024 * 1024;

    // Kùzu's connection is not thread-safe and the server answers requests
    // concurrently; one graph at a time is plenty for a page a person reads.
    private static readonly Lock GraphGate = new();

    private readonly string _workspacesDir;

    public DashboardData(string? workspacesDir = null)
    {
        _workspacesDir = workspacesDir ?? AppPaths.WorkspacesDir;
    }

    // ------------------------------------------------------------ workspaces

    /// <summary>The workspace folder for an id from the page, or null. Only a name that is listed resolves — no paths.</summary>
    public string? ResolveWorkspace(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Directory.Exists(_workspacesDir)) return null;
        foreach (var dir in Directory.EnumerateDirectories(_workspacesDir))
            if (string.Equals(Path.GetFileName(dir), id, StringComparison.Ordinal)) return dir;
        return null;
    }

    /// <summary>The folders a request covers: one, or every workspace for "all". Null when the id is unknown.</summary>
    public IReadOnlyList<string>? Scope(string? id)
    {
        if (string.IsNullOrEmpty(id) || id == All)
            return Directory.Exists(_workspacesDir) ? Directory.EnumerateDirectories(_workspacesDir).Order(StringComparer.Ordinal).ToList() : [];
        return ResolveWorkspace(id) is { } dir ? [dir] : null;
    }

    public WorkspaceList Workspaces(bool withCounts = true)
    {
        var background = SessionRegistry.LoadAlive();
        var list = new WorkspaceList
        {
            Home = AppPaths.BaseDir,
            Version = Program.Version,
            Kuzu = KuzuNative.IsAvailable(),
            Background = background?.Root
        };

        foreach (var dir in Scope(All)!)
        {
            var root = ReadRoot(dir);
            var memory = ReadText(Path.Combine(dir, "memory.md"));
            var sessionsDir = Path.Combine(dir, "sessions");
            var sessionFiles = Directory.Exists(sessionsDir) ? Directory.GetFiles(sessionsDir, "*.jsonl") : [];

            var info = new WorkspaceInfo
            {
                Id = Path.GetFileName(dir),
                Name = root.Length > 0 ? Path.GetFileName(root.TrimEnd('\\', '/')) : Path.GetFileName(dir),
                Root = root,
                RootExists = root.Length > 0 && Directory.Exists(root),
                MemoryChars = memory.Length,
                MemoryEntries = MemoryLog.Parse(memory).Count,
                Sessions = sessionFiles.Length,
                LastActivity = LastActivity(dir, sessionFiles),
                Background = background is not null && root.Length > 0 && SamePath(background.Root, root)
            };

            if (!GraphExists(dir)) info.Graph = "none";
            else if (!list.Kuzu) info.Graph = "off";
            else if (withCounts)
            {
                var counts = WithGraph(dir, CountNodes, out var state, out var note);
                info.Graph = state;
                info.GraphNote = note;
                info.Counts = counts;
            }
            else info.Graph = "ok";

            list.Workspaces.Add(info);
        }

        list.Workspaces.Sort((a, b) => string.CompareOrdinal(b.LastActivity, a.LastActivity));
        return list;
    }

    // --------------------------------------------------------------- memory

    public MemoryView? Memory(string? ws)
    {
        if (Scope(ws) is not { } dirs) return null;
        var view = new MemoryView();
        foreach (var dir in dirs)
        {
            var text = ReadText(Path.Combine(dir, "memory.md"));
            var id = Path.GetFileName(dir);
            foreach (var entry in MemoryLog.Parse(text))
            {
                entry.Ws = id;
                view.Entries.Add(entry);
            }
            if (dirs.Count == 1) view.Raw = text;
        }
        // Newest first; the header's "yyyy-MM-dd HH:mm" sorts as text.
        view.Entries = view.Entries.OrderByDescending(e => e.At, StringComparer.Ordinal).ToList();
        return view;
    }

    // ------------------------------------------------------------- sessions

    public SessionList? Sessions(string? ws, int limit = 200)
    {
        if (Scope(ws) is not { } dirs) return null;
        var list = new SessionList();
        foreach (var dir in dirs)
        {
            var sessionsDir = Path.Combine(dir, "sessions");
            if (!Directory.Exists(sessionsDir)) continue;
            foreach (var path in Directory.EnumerateFiles(sessionsDir, "*.jsonl"))
            {
                if (WorkspaceStore.Summarize(path) is not { } s) continue;
                list.Sessions.Add(new SessionInfo
                {
                    Ws = Path.GetFileName(dir),
                    Id = s.Id,
                    Title = s.Title,
                    Turns = s.Turns,
                    Started = s.Started.ToString("yyyy-MM-dd HH:mm"),
                    FirstPrompt = s.FirstPrompt
                });
            }
        }
        list.Sessions = list.Sessions.OrderByDescending(s => s.Started, StringComparer.Ordinal)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal).Take(limit).ToList();
        return list;
    }

    /// <summary>One transcript. Only a file that is actually in the workspace's sessions folder resolves.</summary>
    public SessionDetail? Session(string ws, string id)
    {
        if (ResolveWorkspace(ws) is not { } dir) return null;
        var sessionsDir = Path.Combine(dir, "sessions");
        if (!Directory.Exists(sessionsDir)) return null;

        var path = Directory.EnumerateFiles(sessionsDir, "*.jsonl")
            .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), id, StringComparison.Ordinal));
        if (path is null) return null;

        return new SessionDetail { Ws = ws, Id = id, Entries = WorkspaceStore.ReadEntries(path).ToList() };
    }

    // ---------------------------------------------------------------- graph

    public GraphView? Graph(string? ws, int perTable = DefaultNodeLimit)
    {
        if (Scope(ws) is not { } dirs) return null;
        perTable = Math.Clamp(perTable, 1, MaxNodeLimit);
        var view = new GraphView();

        if (!KuzuNative.IsAvailable())
        {
            view.Notes.Add("the knowledge graph is off here — Kùzu's library is not next to the binary");
            return view;
        }

        foreach (var dir in dirs)
        {
            if (!GraphExists(dir)) continue;
            var id = Path.GetFileName(dir);
            WithGraph(dir, g => { ReadGraph(g, id, perTable, view); return true; }, out var state, out var note);
            if (state != "ok" && note is not null) view.Notes.Add($"{id}: {note}");
        }
        return view;
    }

    private static void ReadGraph(KuzuGraph g, string ws, int perTable, GraphView view)
    {
        var (nodes, rels) = Tables(g);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);   // label → primary key
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var label in nodes)
        {
            var props = Properties(g, label, out var pk);
            if (pk is null || props.Count == 0) continue;
            keys[label] = pk;

            var order = new[] { "created", "at", "started", "scanned" }.FirstOrDefault(props.Contains);
            var cypher = $"MATCH (n:{Q(label)}) RETURN {string.Join(", ", props.Select(p => "n." + Q(p)))}"
                         + (order is null ? "" : $" ORDER BY n.{Q(order)} DESC")
                         + $" LIMIT {perTable + 1}";
            List<string[]> rows;
            try { rows = g.Query(cypher, props.Count); }
            catch (InvalidOperationException ex) { view.Notes.Add($"{ws}: {label}: {FirstLine(ex.Message)}"); continue; }

            if (rows.Count > perTable)
            {
                view.Truncated.Add($"{ws}/{label}");
                rows.RemoveAt(rows.Count - 1);
            }

            var pkIndex = props.IndexOf(pk);
            foreach (var row in rows)
            {
                var node = new GraphNode { Id = NodeId(ws, label, row[pkIndex]), Label = label, Ws = ws };
                for (var i = 0; i < props.Count; i++) node.Props[props[i]] = Clip(row[i], PropChars);
                node.Caption = Caption(label, node.Props);
                if (present.Add(node.Id)) view.Nodes.Add(node);
            }
        }

        foreach (var rel in rels)
        {
            var relProps = Properties(g, rel, out _);
            foreach (var (from, to) in Connections(g, rel))
            {
                if (!keys.TryGetValue(from, out var fk) || !keys.TryGetValue(to, out var tk)) continue;
                var cypher = $"MATCH (a:{Q(from)})-[r:{Q(rel)}]->(b:{Q(to)}) RETURN a.{Q(fk)}, b.{Q(tk)}"
                             + string.Concat(relProps.Select(p => ", r." + Q(p)))
                             + $" LIMIT {MaxEdgesPerRel}";
                List<string[]> rows;
                try { rows = g.Query(cypher, 2 + relProps.Count); }
                catch (InvalidOperationException ex) { view.Notes.Add($"{ws}: {rel}: {FirstLine(ex.Message)}"); continue; }

                foreach (var row in rows)
                {
                    var a = NodeId(ws, from, row[0]);
                    var b = NodeId(ws, to, row[1]);
                    if (!present.Contains(a) || !present.Contains(b)) continue;
                    var edge = new GraphEdge { From = a, To = b, Type = rel };
                    if (relProps.Count > 0)
                    {
                        edge.Props = [];
                        for (var i = 0; i < relProps.Count; i++) edge.Props[relProps[i]] = Clip(row[2 + i], 300);
                    }
                    view.Edges.Add(edge);
                }
            }
        }
    }

    private static Dictionary<string, long> CountNodes(KuzuGraph g)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var label in Tables(g).Nodes)
        {
            try
            {
                var rows = g.Query($"MATCH (n:{Q(label)}) RETURN count(n)", 1);
                counts[label] = rows.Count > 0 && long.TryParse(rows[0][0], out var n) ? n : 0;
            }
            catch (InvalidOperationException) { /* a table the catalog lists but cannot count — skip it */ }
        }
        return counts;
    }

    // --------------------------------------------------------------- cypher

    /// <summary>
    /// Any Cypher, against one workspace's graph or every one of them. Across
    /// workspaces the rows gain a leading "workspace" column; a graph that is
    /// busy or rejects the query becomes an entry in <c>errors</c>, and the
    /// others still answer.
    /// </summary>
    public CypherResult? Cypher(string? ws, string query)
    {
        if (Scope(ws) is not { } dirs) return null;
        var result = new CypherResult();
        var many = dirs.Count > 1 || ws == All;
        var watch = Stopwatch.StartNew();

        if (!KuzuNative.IsAvailable())
        {
            result.Errors.Add("the knowledge graph is off here — Kùzu's library is not next to the binary");
            return result;
        }

        var failures = new List<(string Ws, string Error)>();
        foreach (var dir in dirs)
        {
            if (!GraphExists(dir)) { if (!many) result.Errors.Add("this workspace has no knowledge graph yet"); continue; }
            var id = Path.GetFileName(dir);
            string? error = null;
            var table = WithGraph(dir, g =>
            {
                try { return g.QueryTable(query); }
                catch (InvalidOperationException ex) { error = ex.Message; return default; }
            }, out var state, out var note);

            if (state != "ok") { failures.Add((id, note ?? "unreadable")); continue; }
            if (error is not null) { failures.Add((id, error)); continue; }
            if (table.Columns is null) continue;

            if (result.Columns.Count == 0)
                result.Columns = many ? ["workspace", .. table.Columns] : [.. table.Columns];

            foreach (var row in table.Rows)
            {
                if (result.Rows.Count >= MaxCypherRows) { result.Truncated = true; break; }
                result.Rows.Add(many ? [id, .. row] : row);
            }
        }

        // Across workspaces the same failure repeats — every graph made before
        // PDSA existed answers "Table Cycle does not exist" — so it is said once,
        // with the workspaces it came from.
        if (!many) result.Errors.AddRange(failures.Select(f => f.Error));
        else
            foreach (var group in failures.GroupBy(f => FirstLine(f.Error), StringComparer.Ordinal))
            {
                var names = group.Select(f => f.Ws).ToList();
                result.Errors.Add(names.Count == 1
                    ? $"{names[0]}: {group.First().Error}"
                    : $"{names.Count} workspaces: {group.Key}\n  {string.Join(", ", names)}");
            }

        result.ElapsedMs = watch.ElapsedMilliseconds;
        return result;
    }

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Opens the workspace's graph read-only, runs <paramref name="read"/>,
    /// closes it. <paramref name="state"/> is "ok", or "busy" when another
    /// agent-one process holds the database — the usual reason a read-only
    /// open fails, since Kùzu takes the file lock across processes.
    /// </summary>
    private static T? WithGraph<T>(string dir, Func<KuzuGraph, T> read, out string state, out string? note)
    {
        var path = GraphPath(dir);
        lock (GraphGate)
        {
            KuzuGraph graph;
            try { graph = new KuzuGraph(path, readOnly: true, bufferPoolBytes: ReaderBufferPool); }
            catch (InvalidOperationException)
            {
                state = "busy";
                note = "the graph is open in another agent-one process (a chat or the background session in that workspace) — it can be read once that ends";
                return default;
            }

            using (graph)
            {
                state = "ok";
                note = null;
                return read(graph);
            }
        }
    }

    private static (List<string> Nodes, List<string> Rels) Tables(KuzuGraph g)
    {
        var nodes = new List<string>();
        var rels = new List<string>();
        foreach (var row in g.Query("CALL show_tables() RETURN name, type", 2))
            (row[1] == "NODE" ? nodes : row[1] == "REL" ? rels : null)?.Add(row[0]);
        nodes.Sort(StringComparer.Ordinal);
        rels.Sort(StringComparer.Ordinal);
        return (nodes, rels);
    }

    private static List<string> Properties(KuzuGraph g, string table, out string? primaryKey)
    {
        primaryKey = null;
        var props = new List<string>();
        var (columns, rows) = g.QueryTable($"CALL table_info('{table.Replace("'", "")}') RETURN *");
        var name = Array.IndexOf(columns, "name");
        var pk = Array.IndexOf(columns, "primary key");
        if (name < 0) return props;
        foreach (var row in rows)
        {
            props.Add(row[name]);
            if (pk >= 0 && row[pk] == "True") primaryKey = row[name];
        }
        return props;
    }

    private static List<(string From, string To)> Connections(KuzuGraph g, string rel)
    {
        var list = new List<(string, string)>();
        try
        {
            var (columns, rows) = g.QueryTable($"CALL show_connection('{rel.Replace("'", "")}') RETURN *");
            var src = Array.IndexOf(columns, "source table name");
            var dst = Array.IndexOf(columns, "destination table name");
            if (src < 0 || dst < 0) return list;
            foreach (var row in rows) list.Add((row[src], row[dst]));
        }
        catch (InvalidOperationException) { /* an older Kùzu without show_connection: no edges for this table */ }
        return list;
    }

    private static readonly string[] CaptionKeys = ["title", "asked", "path", "note", "choice", "name", "kind"];

    private static string Caption(string label, Dictionary<string, string> props)
    {
        string text;
        if (label == "Rationale" && props.TryGetValue("choice", out var choice))
            text = props.TryGetValue("confidence", out var c) && double.TryParse(c, System.Globalization.CultureInfo.InvariantCulture, out var conf)
                ? $"{choice} {conf:0.00}" : choice;
        else if (label == "Phase" && props.TryGetValue("kind", out var kind))
            text = props.TryGetValue("note", out var note) && note.Length > 0 ? $"{kind}: {note}" : kind;
        else
            text = CaptionKeys.Select(k => props.GetValueOrDefault(k, "")).FirstOrDefault(v => v.Length > 0)
                   ?? props.Values.FirstOrDefault() ?? label;
        return Clip(text.Replace('\n', ' '), 60);
    }

    private static string NodeId(string ws, string label, string key) => $"{ws}/{label}/{key}";

    /// <summary>Backtick-quotes a table or property name from the catalog.</summary>
    private static string Q(string name) => "`" + name.Replace("`", "") + "`";

    private static string GraphPath(string dir) => Path.Combine(dir, "graph", "knowledge.kuzu");

    private static bool GraphExists(string dir)
    {
        var path = GraphPath(dir);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static string ReadRoot(string dir)
    {
        try
        {
            var meta = Path.Combine(dir, "workspace.json");
            if (!File.Exists(meta)) return "";
            return JsonSerializer.Deserialize(File.ReadAllText(meta), AgentOneJson.Default.WorkspaceMeta)?.Root ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string LastActivity(string dir, string[] sessionFiles)
    {
        var times = new List<DateTime>();
        foreach (var path in sessionFiles.Append(Path.Combine(dir, "memory.md")).Append(GraphPath(dir)))
            if (File.Exists(path)) times.Add(File.GetLastWriteTime(path));
        return times.Count == 0 ? "" : times.Max().ToString("yyyy-MM-dd HH:mm");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// memory.md back into entries. The writer is <c>ChatSession.Remember</c>:
/// <c>## yyyy-MM-dd HH:mm · title</c>, then <c>- asked:</c>, <c>- did:</c>
/// (tool runs joined by "; ") and <c>- outcome:</c>. A block that does not
/// match — hand-edited, or from an older version — is kept with its text in
/// <see cref="MemoryEntryDto.Extra"/> rather than dropped.
/// </summary>
public static partial class MemoryLog
{
    [GeneratedRegex(@"^##\s+(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\s*·?\s*(.*)$")]
    private static partial Regex Header();

    public static List<MemoryEntryDto> Parse(string text)
    {
        var entries = new List<MemoryEntryDto>();
        MemoryEntryDto? current = null;
        var extra = new List<string>();

        void Close()
        {
            if (current is null) return;
            if (extra.Count > 0) current.Extra = string.Join('\n', extra).Trim();
            if (current.Extra?.Length == 0) current.Extra = null;
            entries.Add(current);
            extra.Clear();
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                var m = Header().Match(raw);
                current = m.Success
                    ? new MemoryEntryDto { At = m.Groups[1].Value, Title = m.Groups[2].Value.Trim() }
                    : new MemoryEntryDto { Title = raw[3..].Trim() };
                continue;
            }
            if (current is null) continue;

            if (raw.StartsWith("- asked: ", StringComparison.Ordinal)) current.Asked = raw[9..].Trim();
            else if (raw.StartsWith("- did: ", StringComparison.Ordinal))
            {
                var did = raw[7..].Trim();
                current.Did = did == "(no tools)" ? [] : did.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else if (raw.StartsWith("- outcome: ", StringComparison.Ordinal)) current.Outcome = raw[11..].Trim();
            else if (raw.Trim().Length > 0) extra.Add(raw);
        }
        Close();
        return entries;
    }
}
