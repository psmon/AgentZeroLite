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
                foreach (var (path, n) in graph.KnownPaths(10)) Console.WriteLine($"  {n,3} × {path}");
                return 0;

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
            agent-one memory query "<cypher>" [--columns n]
                                                Any Cypher against the graph (tables: Knowledge, Turn, Rationale, Path;
                                                rels: LEARNED, JUSTIFIED_BY, ABOUT, HELPED)
            Options: -r/--root <dir>  the workspace (default: cwd)

            The graph fills itself: after each turn the decision engine judges whether
            the turn taught anything, the model distils it, and it is stored with the
            engine's judgement attached. Before a turn, the engine decides whether the
            graph can help and which query to run; what it finds reaches the model
            before any file is scanned.
            """);
    }
}

file static class KnowledgeItemFormat
{
    /// <summary>"created" is already the stored timestamp; this only keeps the call site readable.</summary>
    public static string created(this KnowledgeItem item) => item.Created;
}
