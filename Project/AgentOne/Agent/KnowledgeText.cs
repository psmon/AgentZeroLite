using System.Text;
using AgentOne.Graph;

namespace AgentOne.Agent;

/// <summary>
/// How the knowledge commands print — one copy for `/knowledge` in chat and
/// `agent-one knowledge` on the command line, so the two never drift.
/// </summary>
public static class KnowledgeText
{
    /// <summary>Rows printed for a Cypher result; the rest is counted, not shown.</summary>
    public const int MaxRows = 200;

    public const string Usage = """
        /knowledge                      what the graph holds (documents, guidelines, knowledge, turns)
        /knowledge init [folder]        scan the workspace's markdown into the graph (creates it on first use)
        /knowledge update [folder]      the same, incremental: only changed files and sections are judged again
        /knowledge rebuild [folder]     judge every section again
        /knowledge guidelines [n|file]  the guidelines the documents state
        /cypher <query>                 any Cypher against the graph
        """;

    /// <summary>The schema a Cypher author needs, in one line per kind.</summary>
    public const string Schema = """
        nodes  Doc(path, hash, scanned, sections) · Knowledge(id, title, text, kind, created, uses, keywords, source, hash)
               Turn(id, asked, outcome, at) · Rationale(id, question, choice, confidence, basis) · Path(path)
               Cycle · Phase (PDSA)
        rels   (Doc)-[:GUIDES {confidence}]->(Knowledge)    a guideline the document states
               (Doc)-[:INFORMS {confidence}]->(Knowledge)   knowledge the document states
               (Knowledge)-[:JUSTIFIED_BY]->(Rationale)    why it was kept / how it was classified
               (Knowledge)-[:ABOUT]->(Path) · (Turn)-[:LEARNED]->(Knowledge) · (Knowledge)-[:HELPED {how}]->(Turn)
               HAS_PHASE · RAN_IN · NEXT_CYCLE · REINFORCES · TAUGHT · BUILT_ON (PDSA)
        """;

    public static IReadOnlyList<string> Status(KnowledgeGraph graph, string workspaceRoot)
    {
        var s = graph.Stats();
        var d = graph.DocCounts();
        return new List<string>
        {
            $"workspace  {workspaceRoot}",
            $"graph      {graph.Path}",
            $"documents  {d.Docs} scanned · {d.Guidelines} guideline sections (GUIDES) · {d.Knowledge} knowledge sections (INFORMS)",
            $"learned    {s.Knowledge - d.Guidelines - d.Knowledge} items from turns · {s.Turns} turns · helped {s.Helped} times",
            d.Docs == 0 ? "           (no documents yet — `/knowledge init` or `agent-one knowledge init` scans the markdown)" : ""
        }.Where(l => l.Length > 0).ToList();
    }

    public static string Guidelines(IReadOnlyList<(string Doc, KnowledgeItem Item, double Confidence)> rows)
    {
        if (rows.Count == 0) return "(no guidelines in the graph — run `knowledge init` first)";
        var sb = new StringBuilder();
        foreach (var (doc, item, confidence) in rows)
        {
            sb.Append($"[{confidence:0.00}] {doc} › {item.Title}\n");
            sb.Append("    ").Append(Clip(item.Text, 240).ReplaceLineEndings("\n    ")).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A Cypher result as a tab-separated table under its header.</summary>
    public static string Table((string[] Columns, List<string[]> Rows) result)
    {
        var sb = new StringBuilder(string.Join('\t', result.Columns));
        foreach (var row in result.Rows.Take(MaxRows)) sb.Append('\n').Append(string.Join('\t', row.Select(c => c.ReplaceLineEndings(" "))));
        sb.Append($"\n({result.Rows.Count} row{(result.Rows.Count == 1 ? "" : "s")}{(result.Rows.Count > MaxRows ? $", first {MaxRows} shown" : "")})");
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
