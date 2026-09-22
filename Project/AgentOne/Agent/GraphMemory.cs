using System.Text;
using AgentOne.Graph;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Agent;

/// <summary>What the graph was asked before a turn, and what it gave.</summary>
/// <param name="Helps">The engine's "would the graph help?" decision.</param>
/// <param name="Strategy">Which query it chose, or null when it said no.</param>
/// <param name="Items">What the query returned; empty when nothing matched.</param>
public sealed record GraphConsult(Decision Helps, Decision? Strategy, string StrategyName, IReadOnlyList<KnowledgeItem> Items)
{
    public bool Consulted => Helps.Ok && Helps.Choice == SmartRouter.ConsultGraph;

    /// <summary>The items as a material line for the model, tagged like a tool result.</summary>
    public string Material()
    {
        var sb = new StringBuilder("[graph memory] What this project's knowledge graph says (from earlier sessions; ");
        sb.Append("check the files before relying on a path or a command):");
        foreach (var item in Items)
            sb.Append('\n').Append("- (").Append(item.Kind).Append(") ").Append(item.Title).Append(": ").Append(item.Text);
        return sb.ToString();
    }
}

/// <summary>
/// The workspace's knowledge graph in use: before a turn, the decision engine
/// says whether the graph is worth consulting and which of four queries to
/// run, and what comes back is handed to the model as material — so a
/// question about the project starts from what earlier sessions learned
/// rather than from a fresh scan of the files. After a turn, the engine
/// says whether the turn taught anything, the everyday model distils it,
/// and it is stored with the engine's judgement attached. Every use adds an
/// edge, and ranking follows use: the graph gets better the more it is asked.
/// </summary>
public sealed class GraphMemory : IDisposable
{
    public const int MaxItems = 5;

    public KnowledgeGraph Graph { get; }

    private GraphMemory(KnowledgeGraph graph) => Graph = graph;

    /// <summary>Null when Kùzu's library is not next to the binary; the agent runs without the graph then.</summary>
    public static GraphMemory? Open(WorkspaceStore workspace)
    {
        var graph = KnowledgeGraph.Open(Path.Combine(workspace.Dir, "graph"));
        return graph is null ? null : new GraphMemory(graph);
    }

    public GraphStats Stats() => Graph.Stats();

    public bool HasKnowledge => Graph.Stats().Knowledge > 0;

    /// <summary>What the engine is told the graph holds: counts, the paths it knows most about, the newest titles.</summary>
    public string Summary()
    {
        var stats = Graph.Stats();
        var sb = new StringBuilder();
        sb.Append(stats.Knowledge).Append(" items about ").Append(stats.Paths).Append(" paths, from ").Append(stats.Turns).Append(" turns; ")
          .Append(stats.Helped).Append(" times knowledge helped a later turn.");

        var paths = Graph.KnownPaths(8);
        if (paths.Count > 0) sb.Append("\nPaths it knows about: ").Append(string.Join(", ", paths.Select(p => p.Path)));

        var recent = Graph.Recent(6);
        if (recent.Count > 0)
        {
            sb.Append("\nNewest items:");
            foreach (var item in recent) sb.Append("\n- (").Append(item.Kind).Append(") ").Append(item.Title);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ before

    /// <summary>
    /// Asks the engine whether the graph helps, then which query, runs it, and
    /// records that the items helped this turn. Null when the engine said no
    /// or the query found nothing.
    /// </summary>
    public async Task<GraphConsult?> ConsultAsync(SmartRouter router, string turnId, string request, CancellationToken ct)
    {
        var summary = Summary();
        var helps = await router.GraphHelpsAsync(request, summary, ct);
        if (!helps.Ok || helps.Choice != SmartRouter.ConsultGraph)
            return new GraphConsult(helps, null, "", []);

        var strategy = await router.GraphStrategyAsync(request, summary, ct);
        var name = strategy.Ok ? strategy.Choice : SmartRouter.ByKeywords;

        var items = Run(name, request);

        // The chosen query found nothing: a keyword pass is the broadest net,
        // and cheap, so it is the fallback before giving up.
        if (items.Count == 0 && name != SmartRouter.ByKeywords)
        {
            items = Run(SmartRouter.ByKeywords, request);
            if (items.Count > 0) name = SmartRouter.ByKeywords + " (fallback)";
        }

        if (items.Count > 0)
            Graph.MarkHelped(items.Select(i => i.Id), turnId, name);

        return new GraphConsult(helps, strategy, name, items);
    }

    /// <summary>The four queries the engine chooses between — the "best Cypher" is one of these, run with the request's words.</summary>
    public IReadOnlyList<KnowledgeItem> Run(string strategy, string request) => strategy switch
    {
        SmartRouter.ByPaths => ByPaths(request),
        SmartRouter.RecentFirst => Graph.Recent(MaxItems),
        SmartRouter.MostHelpful => Graph.MostHelpful(MaxItems),
        _ => Graph.ByKeywords(KnowledgeGraph.Keywords(request), MaxItems)
    };

    private IReadOnlyList<KnowledgeItem> ByPaths(string request)
    {
        var found = new List<KnowledgeItem>();
        var fragments = KnowledgeGraph.PathsIn(request);
        if (fragments.Count == 0) fragments = KnowledgeGraph.Keywords(request, 4);
        foreach (var fragment in fragments)
            foreach (var item in Graph.ByPath(fragment, MaxItems))
                if (found.All(f => f.Id != item.Id)) found.Add(item);
        return found.Take(MaxItems).ToList();
    }

    // ------------------------------------------------------------- after

    /// <summary>
    /// The turn is over: the engine judges whether it taught anything; if so
    /// the everyday model distils 1–3 items and each is stored with the
    /// engine's decision as its rationale, linked to the turn and to the
    /// files it names. Returns what was kept, for the renderers.
    /// </summary>
    public async Task<IReadOnlyList<Distilled>> LearnAsync(
        SmartRouter router, IChatProvider provider, string turnId, string request, AgentRun run, CancellationToken ct)
    {
        var did = string.Join("; ", run.Steps
            .Where(s => s.Tool is not ("final" or "unwrapped" or "(unparsed)"))
            .Select(s => $"{s.Tool}{(s.Ok ? "" : "(failed)")}: {WorkspaceStore.FirstLine(s.Detail, 100)}"));
        if (did.Length == 0) did = "(no tools)";
        var outcome = run.Succeeded ? run.Text : $"stopped ({run.Reason}): {run.Text}";

        Graph.RememberTurn(turnId, request, outcome);

        var verdict = await router.WorthSavingAsync(request, did, outcome, ct);
        if (!verdict.Ok || verdict.Choice != SmartRouter.SaveKnowledge) return [];

        var items = await KnowledgeDistiller.DistillAsync(provider, request, did, outcome, ct);
        if (items.Count == 0) return [];

        var why = new Rationale(SmartRouter.WorthSavingQuestion, verdict.Choice, verdict.Confidence,
            "asked: " + WorkspaceStore.FirstLine(request, 200) + "\ndid: " + did + "\noutcome: " + WorkspaceStore.FirstLine(outcome, 300));

        var pathsFromSteps = run.Steps
            .Where(s => s.Tool is "write_file" or "read_file")
            .SelectMany(s => KnowledgeGraph.PathsIn(s.Detail));

        foreach (var item in items)
            Graph.Learn(turnId, item.Title, item.Text, item.Kind, why, KnowledgeGraph.PathsIn(item.Text).Concat(pathsFromSteps));

        return items;
    }

    public void Dispose() => Graph.Dispose();
}
