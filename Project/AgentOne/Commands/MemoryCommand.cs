using AgentOne.Graph;
using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// A window onto the workspace's knowledge graph: what it holds, the newest
/// and the most-used items, a keyword or path search, or any Cypher at all.
/// For a person checking what the agent has learned, and for another agent
/// reading it as data.
/// </summary>
public sealed class MemoryCommand
{
    public int Execute(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var columns = 1;
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": PrintHelp(); return 0;
                case "-r" or "--root":
                    if (i + 1 >= args.Length || !Directory.Exists(args[i + 1])) { Console.Error.WriteLine("agent-one memory: --root needs an existing directory"); return 2; }
                    root = Path.GetFullPath(args[++i]);
                    break;
                case "--columns":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out columns) || columns < 1) { Console.Error.WriteLine("agent-one memory: --columns needs a number"); return 2; }
                    i++;
                    break;
                default: rest.Add(args[i]); break;
            }
        }

        var workspace = new WorkspaceStore(root);
        if (!KuzuNative.IsAvailable())
        {
            Console.Error.WriteLine("agent-one memory: the knowledge graph is off — Kùzu's library (kuzu_shared.dll / libkuzu.so / libkuzu.dylib) is not next to the binary");
            return 1;
        }

        using var graph = KnowledgeGraph.Open(Path.Combine(workspace.Dir, "graph"));
        if (graph is null)
        {
            // Kùzu allows one open handle per database, across processes too.
            Console.Error.WriteLine("agent-one memory: the graph is open in another agent-one process — the background session, most likely.");
            Console.Error.WriteLine("  ask it instead (`agent-one ask \"/status\"`), or `agent-one session stop` and run this again.");
            return 1;
        }

        var sub = rest.Count > 0 ? rest[0].ToLowerInvariant() : "stats";
        var tail = rest.Skip(1).ToList();

        switch (sub)
        {
            case "stats":
                var s = graph.Stats();
                Console.WriteLine($"workspace  {workspace.Root}");
                Console.WriteLine($"graph      {graph.Path}");
                Console.WriteLine($"knowledge  {s.Knowledge} items · {s.Paths} paths · {s.Turns} turns · helped {s.Helped} times");
                var c = graph.CycleStats();
                Console.WriteLine($"cycles     {c.Cycles} PDSA cycles · {c.Phases} phases · plan met {c.Met}/{c.Judged} · {c.KnowledgeEdges} knowledge edges");
                foreach (var (path, n) in graph.KnownPaths(10)) Console.WriteLine($"  {n,3} × {path}");
                return 0;

            case "pdsa" or "cycles":
                return PrintCycles(graph, tail.Count > 0 && int.TryParse(tail[0], out var n0) ? n0 : 5);

            case "recent":
                return Print(graph.Recent(tail.Count > 0 && int.TryParse(tail[0], out var n1) ? n1 : 10));

            case "helpful":
                return Print(graph.MostHelpful(tail.Count > 0 && int.TryParse(tail[0], out var n2) ? n2 : 10));

            case "search":
                if (tail.Count == 0) { Console.Error.WriteLine("agent-one memory search: give some words"); return 2; }
                return Print(graph.ByKeywords(tail, 10));

            case "path":
                if (tail.Count == 0) { Console.Error.WriteLine("agent-one memory path: give a path fragment"); return 2; }
                return Print(graph.ByPath(tail[0], 10));

            case "query":
                if (tail.Count == 0) { Console.Error.WriteLine("agent-one memory query: give a Cypher statement"); return 2; }
                try
                {
                    foreach (var row in graph.Query(string.Join(' ', tail), columns))
                        Console.WriteLine(string.Join("\t", row));
                    return 0;
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine("agent-one memory query: " + ex.Message);
                    return 1;
                }

            default:
                Console.Error.WriteLine($"agent-one memory: unknown subcommand '{sub}'");
                PrintHelp();
                return 2;
        }
    }

    /// <summary>
    /// The improvement cycles, newest first: each phase on its own line, and
    /// the knowledge a closed cycle is wired to — what it taught, and what it
    /// stood on. That pair is the reason the cycles live in this graph.
    /// </summary>
    private static int PrintCycles(KnowledgeGraph graph, int limit)
    {
        var cycles = graph.RecentCycles(limit);
        if (cycles.Count == 0)
        {
            Console.WriteLine("(no improvement cycle has run here yet — a planning request in smart mode starts one)");
            return 0;
        }

        foreach (var cycle in cycles)
        {
            var verdict = cycle.Verdict.Length > 0 ? " · " + cycle.Verdict : "";
            Console.WriteLine($"#{cycle.Id} [{cycle.Status}{verdict}] {cycle.Title}   ({cycle.Started})");

            foreach (var phase in cycle.Phases)
            {
                var mark = phase.Verdict.Length > 0 ? $" → {phase.Verdict}" : "";
                Console.WriteLine($"    {phase.Kind,-5} {phase.Note}{mark}");
            }

            foreach (var (relation, label) in new[] { ("TAUGHT", "taught"), ("BUILT_ON", "built on") })
                foreach (var item in graph.CycleKnowledge(cycle.Id, relation))
                    Console.WriteLine($"    {label,-8} ({item.Kind}) {item.Title}");
        }
        return 0;
    }

    private static int Print(IReadOnlyList<KnowledgeItem> items)
    {
        if (items.Count == 0) { Console.WriteLine("(nothing)"); return 0; }
        foreach (var item in items)
        {
            Console.WriteLine($"[{item.Kind}] {item.Title}   ({item.created()} · used {item.Uses}×)");
            Console.WriteLine("    " + item.Text.ReplaceLineEndings("\n    "));
        }
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one memory                    What the workspace's knowledge graph holds
            agent-one memory recent [n]         Newest items
            agent-one memory helpful [n]        Items that helped the most turns
            agent-one memory search <words…>    Items whose title or text mentions any of the words
            agent-one memory path <fragment>    Items about a file or folder
            agent-one memory pdsa [n]           Improvement cycles: Plan/Do/Study/Act, and the knowledge each one
                                                taught and built on
            agent-one memory query "<cypher>" [--columns n]
                                                Any Cypher against the graph (tables: Knowledge, Turn, Rationale, Path,
                                                Cycle, Phase; rels: LEARNED, JUSTIFIED_BY, ABOUT, HELPED, HAS_PHASE,
                                                RAN_IN, NEXT_CYCLE, REINFORCES, TAUGHT, BUILT_ON)
            Options: -r/--root <dir>  the workspace (default: cwd)

            The graph fills itself: after each turn the decision engine judges whether
            the turn taught anything, the model distils it, and it is stored with the
            engine's judgement attached. Before a turn, the engine decides whether the
            graph can help and which query to run; what it finds reaches the model
            before any file is scanned.

            A planning request opens a PDSA cycle; the turns after it are placed in
            Plan/Do/Study/Act by the same engine, Study judges the result against what
            Plan predicted, and Act closes the cycle onto the knowledge it produced.
            """);
    }
}

file static class KnowledgeItemFormat
{
    /// <summary>"created" is already the stored timestamp; this only keeps the call site readable.</summary>
    public static string created(this KnowledgeItem item) => item.Created;
}
