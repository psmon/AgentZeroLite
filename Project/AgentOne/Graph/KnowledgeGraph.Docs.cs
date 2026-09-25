namespace AgentOne.Graph;

/// <summary>One section of a scanned document as the graph holds it: enough to tell whether it changed.</summary>
public sealed record StoredSection(string Id, string Hash, string Kind);

/// <summary>Counts for `memory` and `/knowledge`: documents scanned, and how their sections were classified.</summary>
public readonly record struct DocStats(long Docs, long Guidelines, long Knowledge);

/// <summary>
/// The workspace's own documents in the graph — what `knowledge init` builds.
/// Every Markdown file is a <c>Doc</c> (path + content hash); every section
/// of it is a <c>Knowledge</c> node like the ones turns teach, so the recall
/// that runs before a turn finds them with no extra query. What the section
/// <i>is</i> lives on the edge, as the decision engine judged it:
/// <c>(Doc)-[:GUIDES]->(Knowledge)</c> for a guideline — a rule on how to
/// work here — and <c>(Doc)-[:INFORMS]->(Knowledge)</c> for knowledge — how
/// things are. The judgement itself hangs off the node as a Rationale, the
/// same way a learned item's does. Section ids are stable (file + heading
/// path), so an edited section is updated in place and keeps its use count.
/// </summary>
public sealed partial class KnowledgeGraph
{
    public const string GuidelineKind = "guideline";
    public const string KnowledgeKind = "knowledge";
    public const string DocSource = "doc";

    private void EnsureDocSchema()
    {
        Ddl(
            "ALTER TABLE Knowledge ADD source STRING DEFAULT ''",
            "ALTER TABLE Knowledge ADD hash STRING DEFAULT ''",
            "CREATE NODE TABLE Doc(path STRING, hash STRING, scanned STRING, sections INT64, PRIMARY KEY(path))",
            "CREATE REL TABLE GUIDES(FROM Doc TO Knowledge, confidence DOUBLE)",
            "CREATE REL TABLE INFORMS(FROM Doc TO Knowledge, confidence DOUBLE)");
    }

    /// <summary>The content hash recorded for a document, or null when it has never been scanned.</summary>
    public string? DocHash(string docPath)
    {
        lock (_gate)
        {
            var rows = _graph.Query("MATCH (d:Doc {path: $p}) RETURN d.hash", 1, P(("p", docPath)));
            return rows.Count == 0 ? null : rows[0][0];
        }
    }

    /// <summary>Every document the graph has scanned.</summary>
    public IReadOnlyList<string> DocPaths()
    {
        lock (_gate) return _graph.Query("MATCH (d:Doc) RETURN d.path", 1).Select(r => r[0]).ToList();
    }

    /// <summary>The sections the graph holds for a document, by id.</summary>
    public IReadOnlyDictionary<string, StoredSection> DocSections(string docPath)
    {
        lock (_gate)
        {
            return _graph.Query("MATCH (d:Doc {path: $p})-[]->(k:Knowledge) RETURN k.id, k.hash, k.kind", 3, P(("p", docPath)))
                .Select(r => new StoredSection(r[0], r[1], r[2]))
                .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        }
    }

    /// <summary>
    /// Stores one section — created, or updated in place when the id is known —
    /// with the edge its classification names and the engine's reason for it.
    /// </summary>
    public void PutDocSection(string docPath, string id, string title, string text, string hash,
        string kind, Rationale why, string keywords)
    {
        var rel = kind == GuidelineKind ? "GUIDES" : "INFORMS";
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        lock (_gate)
        {
            using var tx = _graph.Begin();

            _graph.Execute("MERGE (d:Doc {path: $p}) ON CREATE SET d.hash = '', d.scanned = $now, d.sections = 0", P(("p", docPath), ("now", now)));
            _graph.Execute("MERGE (:Path {path: $p})", P(("p", docPath)));

            var exists = _graph.Query("MATCH (k:Knowledge {id: $id}) RETURN k.id", 1, P(("id", id))).Count > 0;
            var fields = P(("id", id), ("title", Clip(title, 120)), ("text", Clip(text, 2000)), ("kind", kind),
                ("now", now), ("keywords", Clip(keywords.ToLowerInvariant(), 300)), ("hash", hash), ("source", DocSource));

            if (exists)
            {
                // Same section, new content: keep the node (and its use count), replace what was judged.
                _graph.Execute("MATCH (k:Knowledge {id: $id}) SET k.title = $title, k.text = $text, k.kind = $kind, k.created = $now, k.keywords = $keywords, k.hash = $hash, k.source = $source", fields);
                _graph.Execute("MATCH (k:Knowledge {id: $id})-[:JUSTIFIED_BY]->(r:Rationale) DETACH DELETE r", P(("id", id)));
                _graph.Execute("MATCH (:Doc)-[e:GUIDES]->(k:Knowledge {id: $id}) DELETE e", P(("id", id)));
                _graph.Execute("MATCH (:Doc)-[e:INFORMS]->(k:Knowledge {id: $id}) DELETE e", P(("id", id)));
            }
            else
            {
                _graph.Execute("CREATE (:Knowledge {id: $id, title: $title, text: $text, kind: $kind, created: $now, uses: 0, keywords: $keywords, hash: $hash, source: $source})", fields);
                _graph.Execute("MATCH (k:Knowledge {id: $id}), (p:Path {path: $p}) CREATE (k)-[:ABOUT]->(p)", P(("id", id), ("p", docPath)));
            }

            var rid = NewId("r");
            _graph.Execute("CREATE (:Rationale {id: $id, question: $q, choice: $c, confidence: $conf, basis: $basis})",
                P(("id", rid), ("q", why.Question), ("c", why.Choice), ("conf", why.Confidence), ("basis", Clip(why.Basis, 1500))));
            _graph.Execute("MATCH (k:Knowledge {id: $k}), (r:Rationale {id: $r}) CREATE (k)-[:JUSTIFIED_BY]->(r)", P(("k", id), ("r", rid)));
            _graph.Execute($"MATCH (d:Doc {{path: $p}}), (k:Knowledge {{id: $id}}) CREATE (d)-[:{rel} {{confidence: $conf}}]->(k)",
                P(("p", docPath), ("id", id), ("conf", why.Confidence)));

            tx.Commit();
        }
    }

    /// <summary>A section that is gone from its file: the node, its rationale and every edge.</summary>
    public void RemoveDocSection(string id)
    {
        lock (_gate)
        {
            using var tx = _graph.Begin();
            _graph.Execute("MATCH (k:Knowledge {id: $id})-[:JUSTIFIED_BY]->(r:Rationale) DETACH DELETE r", P(("id", id)));
            _graph.Execute("MATCH (k:Knowledge {id: $id}) DETACH DELETE k", P(("id", id)));
            tx.Commit();
        }
    }

    /// <summary>Records the document as scanned at this content hash.</summary>
    public void MarkDocScanned(string docPath, string hash, int sections)
    {
        lock (_gate)
        {
            _graph.Execute("MERGE (d:Doc {path: $p}) SET d.hash = $hash, d.scanned = $now, d.sections = $n",
                P(("p", docPath), ("hash", hash), ("now", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("n", (long)sections)));
        }
    }

    /// <summary>A document that is gone: its sections, then itself.</summary>
    public void RemoveDoc(string docPath)
    {
        foreach (var id in DocSections(docPath).Keys) RemoveDocSection(id);
        lock (_gate) _graph.Execute("MATCH (d:Doc {path: $p}) DETACH DELETE d", P(("p", docPath)));
    }

    public DocStats DocCounts()
    {
        lock (_gate)
        {
            long Count(string cypher) => long.TryParse(_graph.Query(cypher, 1)[0][0], out var n) ? n : 0;
            return new DocStats(
                Count("MATCH (d:Doc) RETURN count(d)"),
                Count("MATCH (:Doc)-[e:GUIDES]->(:Knowledge) RETURN count(e)"),
                Count("MATCH (:Doc)-[e:INFORMS]->(:Knowledge) RETURN count(e)"));
        }
    }

    /// <summary>The guidelines the documents state, strongest classification first — for `memory guidelines`.</summary>
    public IReadOnlyList<(string Doc, KnowledgeItem Item, double Confidence)> Guidelines(int limit = 20, string? docFragment = null)
    {
        lock (_gate)
        {
            var where = docFragment is null ? "" : "WHERE contains(lower(d.path), $f) ";
            var parameters = P(("limit", (long)limit));
            if (docFragment is not null) parameters["f"] = docFragment.ToLowerInvariant();
            return _graph.Query(
                    $"MATCH (d:Doc)-[e:GUIDES]->(k:Knowledge) {where}RETURN d.path, {Columns}, e.confidence ORDER BY e.confidence DESC, d.path LIMIT $limit",
                    8, parameters)
                .Select(r => (r[0], new KnowledgeItem(r[1], r[2], r[3], r[4], r[5], long.TryParse(r[6], out var u) ? u : 0),
                    double.TryParse(r[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0))
                .ToList();
        }
    }

    private static Dictionary<string, object> P(params (string Name, object Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}
