using System.Text.RegularExpressions;

namespace AgentOne.Graph;

/// <summary>One thing the workspace knows, as the graph returns it.</summary>
public sealed record KnowledgeItem(string Id, string Title, string Text, string Kind, string Created, long Uses);

/// <summary>Why a piece of knowledge was kept: the decision engine's judgement, attached to it.</summary>
/// <param name="Basis">What the engine was shown when it judged — the evidence.</param>
public sealed record Rationale(string Question, string Choice, double Confidence, string Basis);

/// <summary>Counts for the status block.</summary>
public readonly record struct GraphStats(long Knowledge, long Turns, long Paths, long Helped);

/// <summary>
/// The workspace's long-term memory as a graph, in an embedded Kùzu database
/// under the workspace folder. Not a log: the memory file is the log. This
/// holds what was <em>judged</em> worth keeping — a fact, a decision, a fix,
/// a procedure — linked to the turn it came from, the files it is about, the
/// engine's reason for keeping it, and the later turns it helped. The more
/// it is used, the more edges it has, and the better the next search ranks.
/// </summary>
public sealed partial class KnowledgeGraph : IDisposable
{
    private readonly KuzuGraph _graph;
    private readonly Lock _gate = new();

    public string Path { get; }

    private KnowledgeGraph(string path, KuzuGraph graph)
    {
        Path = path;
        _graph = graph;
    }

    /// <summary>Opens (creating the schema on first use). Null when Kùzu is not available here.</summary>
    public static KnowledgeGraph? Open(string directory)
    {
        if (!KuzuNative.IsAvailable()) return null;

        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "knowledge.kuzu");

        KuzuGraph graph;
        try { graph = new KuzuGraph(path); }
        catch (InvalidOperationException) { return null; }

        var store = new KnowledgeGraph(path, graph);
        store.EnsureSchema();
        return store;
    }

    private void EnsureSchema()
    {
        // "already exists" is the normal case after the first open.
        Ddl(
            "CREATE NODE TABLE Knowledge(id STRING, title STRING, text STRING, kind STRING, created STRING, uses INT64, keywords STRING, PRIMARY KEY(id))",
            // Graphs made before keywords existed get the column; "already exists" is the normal case after that.
            "ALTER TABLE Knowledge ADD keywords STRING DEFAULT ''",
            "CREATE NODE TABLE Turn(id STRING, asked STRING, outcome STRING, at STRING, PRIMARY KEY(id))",
            "CREATE NODE TABLE Rationale(id STRING, question STRING, choice STRING, confidence DOUBLE, basis STRING, PRIMARY KEY(id))",
            "CREATE NODE TABLE Path(path STRING, PRIMARY KEY(path))",
            "CREATE REL TABLE LEARNED(FROM Turn TO Knowledge)",
            "CREATE REL TABLE JUSTIFIED_BY(FROM Knowledge TO Rationale)",
            "CREATE REL TABLE ABOUT(FROM Knowledge TO Path)",
            "CREATE REL TABLE HELPED(FROM Knowledge TO Turn, how STRING)");

        EnsurePdsaSchema();
    }

    /// <summary>
    /// Runs schema statements, ignoring the one failure that is not a failure:
    /// the table is already there. Kùzu has no IF NOT EXISTS for every form
    /// used here, and every open after the first hits this path.
    /// </summary>
    private void Ddl(params string[] statements)
    {
        foreach (var ddl in statements)
        {
            try { _graph.Execute(ddl); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                                                       || ex.Message.Contains("already has", StringComparison.OrdinalIgnoreCase)) { }
        }
    }

    // ------------------------------------------------------------ writing

    /// <summary>A turn that the graph should know about, whether or not it taught anything.</summary>
    public void RememberTurn(string turnId, string asked, string outcome)
    {
        lock (_gate)
        {
            _graph.Execute(
                "MERGE (t:Turn {id: $id}) SET t.asked = $asked, t.outcome = $outcome, t.at = $at",
                new Dictionary<string, object>
                {
                    ["id"] = turnId, ["asked"] = Clip(asked, 400), ["outcome"] = Clip(outcome, 600),
                    ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                });
        }
    }

    /// <summary>
    /// One thing learned, from one turn, for the reason the engine gave. The
    /// paths it mentions become nodes it is ABOUT, so a later question about a
    /// file finds it without a text match.
    /// </summary>
    /// <param name="keywords">Search words in any language, space-separated; the keyword query matches them as well as title and text.</param>
    public void Learn(string turnId, string title, string text, string kind, Rationale why, IEnumerable<string> paths, string keywords = "")
    {
        lock (_gate)
        {
            using var tx = _graph.Begin();
            var id = NewId("k");
            var rid = NewId("r");

            _graph.Execute(
                "CREATE (:Knowledge {id: $id, title: $title, text: $text, kind: $kind, created: $created, uses: 0, keywords: $keywords})",
                new Dictionary<string, object>
                {
                    ["id"] = id, ["title"] = Clip(title, 120), ["text"] = Clip(text, 2000), ["kind"] = kind,
                    ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["keywords"] = Clip(keywords.ToLowerInvariant(), 300)
                });

            _graph.Execute(
                "CREATE (:Rationale {id: $id, question: $q, choice: $c, confidence: $conf, basis: $basis})",
                new Dictionary<string, object>
                {
                    ["id"] = rid, ["q"] = why.Question, ["c"] = why.Choice, ["conf"] = why.Confidence, ["basis"] = Clip(why.Basis, 1500)
                });

            _graph.Execute(
                "MATCH (k:Knowledge {id: $k}), (r:Rationale {id: $r}) CREATE (k)-[:JUSTIFIED_BY]->(r)",
                new Dictionary<string, object> { ["k"] = id, ["r"] = rid });

            _graph.Execute(
                "MATCH (t:Turn {id: $t}), (k:Knowledge {id: $k}) CREATE (t)-[:LEARNED]->(k)",
                new Dictionary<string, object> { ["t"] = turnId, ["k"] = id });

            foreach (var path in paths.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _graph.Execute("MERGE (:Path {path: $p})", new Dictionary<string, object> { ["p"] = path });
                _graph.Execute(
                    "MATCH (k:Knowledge {id: $k}), (p:Path {path: $p}) CREATE (k)-[:ABOUT]->(p)",
                    new Dictionary<string, object> { ["k"] = id, ["p"] = path });
            }

            tx.Commit();
        }
    }

    /// <summary>Knowledge that was handed to a turn: an edge, and a use counted, so ranking learns from use.</summary>
    public void MarkHelped(IEnumerable<string> knowledgeIds, string turnId, string how)
    {
        lock (_gate)
        {
            using var tx = _graph.Begin();
            foreach (var id in knowledgeIds)
            {
                _graph.Execute(
                    "MATCH (k:Knowledge {id: $k}), (t:Turn {id: $t}) CREATE (k)-[:HELPED {how: $how}]->(t) SET k.uses = k.uses + 1",
                    new Dictionary<string, object> { ["k"] = id, ["t"] = turnId, ["how"] = how });
            }
            tx.Commit();
        }
    }

    // ------------------------------------------------------------ reading

    private const string Columns = "k.id, k.title, k.text, k.kind, k.created, k.uses";

    /// <summary>Items whose title, text or keywords contain any of the words (case-insensitive), most used and newest first.</summary>
    public IReadOnlyList<KnowledgeItem> ByKeywords(IEnumerable<string> words, int limit = 5)
    {
        var terms = words.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length >= 2).Distinct().Take(8).ToList();
        if (terms.Count == 0) return [];

        var parameters = new Dictionary<string, object> { ["limit"] = (long)limit };
        var clauses = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            parameters[$"w{i}"] = terms[i];
            clauses.Add($"contains(lower(k.title), $w{i}) OR contains(lower(k.text), $w{i}) OR contains(k.keywords, $w{i})");
        }

        var cypher = $"MATCH (k:Knowledge) WHERE {string.Join(" OR ", clauses)} RETURN {Columns} ORDER BY k.uses DESC, k.created DESC LIMIT $limit";
        return Read(cypher, parameters);
    }

    /// <summary>Items ABOUT a path, or any path containing the fragment.</summary>
    public IReadOnlyList<KnowledgeItem> ByPath(string fragment, int limit = 5)
    {
        var cypher = $"MATCH (k:Knowledge)-[:ABOUT]->(p:Path) WHERE contains(lower(p.path), $f) RETURN DISTINCT {Columns} ORDER BY k.uses DESC, k.created DESC LIMIT $limit";
        return Read(cypher, new Dictionary<string, object> { ["f"] = Normalize(fragment).ToLowerInvariant(), ["limit"] = (long)limit });
    }

    public IReadOnlyList<KnowledgeItem> Recent(int limit = 5) =>
        Read($"MATCH (k:Knowledge) RETURN {Columns} ORDER BY k.created DESC LIMIT $limit", new Dictionary<string, object> { ["limit"] = (long)limit });

    public IReadOnlyList<KnowledgeItem> MostHelpful(int limit = 5) =>
        Read($"MATCH (k:Knowledge) WHERE k.uses > 0 RETURN {Columns} ORDER BY k.uses DESC, k.created DESC LIMIT $limit", new Dictionary<string, object> { ["limit"] = (long)limit });

    /// <summary>The paths the graph knows most about, for the "would the graph help?" question.</summary>
    public IReadOnlyList<(string Path, long Count)> KnownPaths(int limit = 10)
    {
        lock (_gate)
        {
            return _graph.Query(
                    "MATCH (k:Knowledge)-[:ABOUT]->(p:Path) RETURN p.path, count(k) AS n ORDER BY n DESC LIMIT $limit", 2,
                    new Dictionary<string, object> { ["limit"] = (long)limit })
                .Select(r => (r[0], long.TryParse(r[1], out var n) ? n : 0)).ToList();
        }
    }

    public GraphStats Stats()
    {
        lock (_gate)
        {
            long Count(string cypher) => long.TryParse(_graph.Query(cypher, 1)[0][0], out var n) ? n : 0;
            return new GraphStats(
                Count("MATCH (k:Knowledge) RETURN count(k)"),
                Count("MATCH (t:Turn) RETURN count(t)"),
                Count("MATCH (p:Path) RETURN count(p)"),
                Count("MATCH ()-[h:HELPED]->() RETURN count(h)"));
        }
    }

    /// <summary>Raw Cypher, for `agent-one memory query`. Rows of strings.</summary>
    public List<string[]> Query(string cypher, int columns)
    {
        lock (_gate) return _graph.Query(cypher, columns);
    }

    private IReadOnlyList<KnowledgeItem> Read(string cypher, IReadOnlyDictionary<string, object> parameters)
    {
        lock (_gate)
        {
            return _graph.Query(cypher, 6, parameters)
                .Select(r => new KnowledgeItem(r[0], r[1], r[2], r[3], r[4], long.TryParse(r[5], out var u) ? u : 0))
                .ToList();
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Words worth searching for: letters and digits, three characters or more, no stop words.</summary>
    public static IReadOnlyList<string> Keywords(string text, int max = 8)
    {
        var words = Word().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length >= 2 && !Stop.Contains(w))
            .Distinct()
            .OrderByDescending(w => w.Length)
            .Take(max)
            .ToList();
        return words;
    }

    /// <summary>Paths mentioned in text: something with a slash or a file extension.</summary>
    public static IReadOnlyList<string> PathsIn(string text) =>
        PathLike().Matches(text).Select(m => Normalize(m.Value)).Where(p => p.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Normalize(string path) => path.Trim().Trim('`', '"', '\'', '(', ')', ',', ';', ':').Replace('\\', '/').TrimStart('.', '/');

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string NewId(string prefix) => prefix + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "have", "has", "are", "was", "were", "not", "you", "our",
        "please", "make", "create", "add", "use", "using", "how", "what", "why", "where", "when", "can", "should", "would",
        "이", "그", "저", "것", "수", "등", "및", "해줘", "해봐", "만들어", "하고", "있는", "없는", "위해", "대해", "에서", "으로",
    };

    [GeneratedRegex(@"[\p{L}\p{N}_.-]{2,}")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?:[\w.-]+[\\/])+[\w.-]+|[\w-]+\.(?:cs|csproj|py|js|ts|tsx|json|md|yml|yaml|toml|txt|sql|sh|ps1|html|css|go|rs|java|kt)\b")]
    private static partial Regex PathLike();

    public void Dispose() => _graph.Dispose();
}
