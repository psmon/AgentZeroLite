using AgentOne.Agent;
using AgentOne.Graph;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>The graph store itself, on a real embedded Kùzu database in a temp folder.</summary>
public class KnowledgeGraphTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-one-graph-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly KnowledgeGraph _graph;

    public KnowledgeGraphTests()
    {
        _graph = KnowledgeGraph.Open(_dir) ?? throw new InvalidOperationException("Kùzu is not available in the test output — did native/Kuzu.targets run?");
    }

    public void Dispose()
    {
        _graph.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly Rationale Why = new("worth it?", "save", 0.83, "asked: x\ndid: y\noutcome: z");

    [Fact]
    public void OpensWithAnEmptySchemaAndReopensWithoutComplaint()
    {
        Assert.Equal(new GraphStats(0, 0, 0, 0), _graph.Stats());

        _graph.RememberTurn("t1", "hello", "hi");
        _graph.Dispose();

        using var again = KnowledgeGraph.Open(_dir)!;
        Assert.Equal(1, again.Stats().Turns);
    }

    [Fact]
    public void KeywordsInAnotherLanguageFindEnglishKnowledge()
    {
        // Measured: "Python Versioning" (English) was invisible to "파이썬 버전" (Korean).
        _graph.RememberTurn("t1", "py -3.99", "fixed");
        _graph.Learn("t1", "Python versioning", "py -X.Y needs that runtime installed; else 'No runtime installed'.", "constraint", Why,
            [], keywords: "python 파이썬 version 버전 py launcher");

        Assert.Single(_graph.ByKeywords(["파이썬", "버전"]));
        Assert.Single(_graph.ByKeywords(["Launcher"]));          // case-insensitive on the stored side too
        Assert.Empty(_graph.ByKeywords(["도커"]));
    }

    [Fact]
    public void LearnLinksTheItemToItsTurnRationaleAndPaths()
    {
        _graph.RememberTurn("t1", "build the board api", "built");
        _graph.Learn("t1", "Build command", "Build with `dotnet build src/BoardApi/BoardApi.csproj`.", "procedure", Why,
            ["src/BoardApi/BoardApi.csproj", "src/BoardApi/Program.cs"]);

        var stats = _graph.Stats();
        Assert.Equal(1, stats.Knowledge);
        Assert.Equal(2, stats.Paths);

        var rows = _graph.Query("MATCH (t:Turn)-[:LEARNED]->(k:Knowledge)-[:JUSTIFIED_BY]->(r:Rationale) RETURN t.id, k.title, r.choice, r.confidence", 4);
        Assert.Single(rows);
        Assert.Equal("t1", rows[0][0]);
        Assert.Equal("Build command", rows[0][1]);
        Assert.Equal("save", rows[0][2]);
        Assert.StartsWith("0.83", rows[0][3]);

        Assert.Equal(["src/BoardApi/BoardApi.csproj", "src/BoardApi/Program.cs"],
            _graph.Query("MATCH (:Knowledge)-[:ABOUT]->(p:Path) RETURN p.path ORDER BY p.path", 1).Select(r => r[0]));
    }

    [Fact]
    public void KeywordsFindTitlesAndTextCaseInsensitively()
    {
        _graph.RememberTurn("t1", "x", "y");
        _graph.Learn("t1", "Entry point", "Program.cs is missing; ASP.NET needs a Main or top-level statements.", "fix", Why, []);
        _graph.Learn("t1", "Storage decision", "SQLite via EF Core was chosen for the board API.", "decision", Why, []);

        var hits = _graph.ByKeywords(["sqlite", "nothing-here"]);
        Assert.Single(hits);
        Assert.Equal("Storage decision", hits[0].Title);

        Assert.Equal(2, _graph.ByKeywords(["PROGRAM", "board"]).Count);
        Assert.Empty(_graph.ByKeywords(["zzz"]));
    }

    [Fact]
    public void ByPathFollowsTheAboutEdges()
    {
        _graph.RememberTurn("t1", "x", "y");
        _graph.Learn("t1", "Csproj target", "TargetFramework is net8.0.", "fact", Why, ["src/BoardApi/BoardApi.csproj"]);

        Assert.Single(_graph.ByPath("boardapi.csproj"));
        Assert.Single(_graph.ByPath("src/BoardApi"));
        Assert.Empty(_graph.ByPath("nowhere"));
    }

    [Fact]
    public void HelpedCountsUsesAndRanksAboveTheRest()
    {
        _graph.RememberTurn("t1", "x", "y");
        _graph.Learn("t1", "Old but useful", "The build lives in src.", "fact", Why, []);
        _graph.Learn("t1", "Newer", "Tests are under tests/.", "fact", Why, []);
        var useful = _graph.ByKeywords(["useful"])[0];

        _graph.RememberTurn("t2", "again", "ok");
        _graph.MarkHelped([useful.Id], "t2", "by_keywords");

        Assert.Equal(1, _graph.Stats().Helped);
        Assert.Equal("Old but useful", _graph.MostHelpful()[0].Title);
        Assert.Equal(1, _graph.MostHelpful()[0].Uses);
        Assert.Equal("Old but useful", _graph.ByKeywords(["src", "tests"])[0].Title);      // uses first, then recency
    }

    [Fact]
    public void KnownPathsAreCountedByHowMuchIsKnownAboutThem()
    {
        _graph.RememberTurn("t1", "x", "y");
        _graph.Learn("t1", "a", "a", "fact", Why, ["src/a.cs"]);
        _graph.Learn("t1", "b", "b", "fact", Why, ["src/a.cs", "src/b.cs"]);

        var known = _graph.KnownPaths();
        Assert.Equal("src/a.cs", known[0].Path);
        Assert.Equal(2, known[0].Count);
    }

    [Theory]
    [InlineData("빌드가 되는지 dotnet build src/BoardApi 로 확인해줘", "dotnet", "build")]
    [InlineData("please fix the Program.cs entry point error CS5001", "program.cs", "cs5001")]
    public void KeywordsDropStopWordsAndKeepTheRest(string text, string first, string second)
    {
        var words = KnowledgeGraph.Keywords(text);
        Assert.Contains(first, words);
        Assert.Contains(second, words);
        Assert.DoesNotContain("please", words);
    }

    [Fact]
    public void PathsAreFoundInProseAndNormalized()
    {
        var paths = KnowledgeGraph.PathsIn("wrote src\\BoardApi\\Program.cs and read `README.md`; see ./docs/x.md (also app.py).");
        Assert.Contains("src/BoardApi/Program.cs", paths);
        Assert.Contains("README.md", paths);
        Assert.Contains("docs/x.md", paths);
        Assert.Contains("app.py", paths);
    }
}

public class KnowledgeDistillerTests
{
    [Fact]
    public void LinesInTheAskedFormatBecomeItems()
    {
        var items = KnowledgeDistiller.Parse("""
            fix | Missing entry point | Add src/BoardApi/Program.cs with top-level statements; CS5001 goes away.
            procedure | Build | Run `dotnet build src/BoardApi/BoardApi.csproj` from the root.
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal("fix", items[0].Kind);
        Assert.Equal("Missing entry point", items[0].Title);
        Assert.Equal("Build", items[1].Title);
    }

    [Fact]
    public void AFourthSegmentIsTheKeywordsAndAThirdIsNotRequired()
    {
        var items = KnowledgeDistiller.Parse("""
            constraint | Python versioning | py -X.Y needs that runtime installed. | python 파이썬 version 버전
            fix | Old format | Three segments still parse.
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal("python 파이썬 version 버전", items[0].Keywords);
        Assert.Equal("py -X.Y needs that runtime installed.", items[0].Text);
        Assert.Equal("", items[1].Keywords);
    }

    [Fact]
    public void ABareLineIsKeptAsAFactAndTheHeaderAndFencesAreNot()
    {
        var items = KnowledgeDistiller.Parse("kind | title | text\n```\n- The API lives under src/BoardApi and targets net8.0.\n```");

        Assert.Single(items);
        Assert.Equal("fact", items[0].Kind);
        Assert.StartsWith("The API lives", items[0].Title);
    }

    [Fact]
    public void AtMostThreeAndAnEnvelopeIsUnwrapped()
    {
        var five = string.Join("\n", Enumerable.Range(1, 5).Select(i => $"fact | title {i} | text number {i}"));
        Assert.Equal(3, KnowledgeDistiller.Parse(five).Count);

        var wrapped = "{\"tool\":\"final\",\"args\":{\"text\":\"decision | Storage | SQLite was chosen.\"}}";
        Assert.Equal("Storage", KnowledgeDistiller.Parse(wrapped)[0].Title);
    }
}

/// <summary>The graph in a session: consulted before a turn, taught after it.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class GraphMemorySessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public GraphMemorySessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-gm-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 10);

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine engine, bool smart = true)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", smart ? "on" : "off", out _);
        config.TrySet("saveSessions", "false", out _);
        // The improvement cycle has its own tests; here it would eat the scripted engine's answers.
        return new ChatSession(config, _root, streaming: false, provider, engine, true) { NamesTasks = false, UsesPdsa = false };
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task ATurnJudgedWorthKeepingIsDistilledAndStoredWithTheEngineRationale()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"src/Program.cs","content":"// entry"}}""",
            """{"tool":"final","args":{"text":"added the entry point"}}""",
            "fix | Entry point added | src/Program.cs holds the top-level statements the SDK needs.");
        // Route, then — with no reasoning model there is no scope or escalation
        // question, and an empty graph is never consulted — "worth saving?".
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace, 0.9),
            Choose(SmartRouter.SaveKnowledge, 0.77));
        using var session = Session(provider, engine);

        IReadOnlyList<Distilled>? learned = null;
        session.Learned += items => learned = items;

        await session.SubmitAsync("add the missing entry point to the api", CancellationToken.None);
        Assert.True(await WaitForAsync(() => learned is not null), "the turn was not distilled");

        Assert.Equal("Entry point added", learned![0].Title);
        var stats = session.Stats().Graph!.Value;
        Assert.Equal(1, stats.Knowledge);
        Assert.Contains(session.Stats().Describe(), l => l.StartsWith("graph") && l.Contains("1 items"));

        // Kùzu allows one open handle per database: let the session go before looking inside.
        var dir = Path.Combine(session.Workspace.Dir, "graph");
        session.Dispose();
        using var graph = KnowledgeGraph.Open(dir)!;
        var rows = graph.Query("MATCH (k:Knowledge)-[:JUSTIFIED_BY]->(r:Rationale) RETURN k.kind, r.choice, r.basis", 3);
        Assert.Equal("fix", rows[0][0]);
        Assert.Equal("save", rows[0][1]);
        Assert.Contains("write_file", rows[0][2]);
        Assert.Contains("src/Program.cs", graph.KnownPaths().Select(p => p.Path));
    }

    [Fact]
    public async Task WhenTheWordsMissTheNewestItemsAreHandedOverAnyway()
    {
        using (var seed = KnowledgeGraph.Open(Path.Combine(new WorkspaceStore(_root).Ensure().Dir, "graph"))!)
        {
            seed.RememberTurn("old-1", "py -3.99", "fixed");
            seed.Learn("old-1", "Python versioning", "py -X.Y needs that runtime installed.", "constraint",
                new Rationale("q", "save", 0.9, "b"), []);
        }

        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"answered"}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly, 0.9),
            Choose(SmartRouter.ConsultGraph, 0.7),
            Choose(SmartRouter.ByKeywords, 0.7),
            Choose(SmartRouter.SkipKnowledge, 0.9));
        using var session = Session(provider, engine);

        var notes = new List<SmartNote>();
        session.Decided += notes.Add;

        // Not one word in common with the item — the measured Korean-vs-English miss.
        await session.SubmitAsync("이 프로젝트에서 특정 파이썬 버전을 지정하려면 뭘 주의해야 하지?", CancellationToken.None);

        Assert.Contains(notes, n => n.Kind == "graph" && n.Verdict.Contains("recent (fallback)") && n.Verdict.Contains("1 item"));
        Assert.Contains(provider.Calls[0], m => m.Role == "user" && m.Content.Contains("[graph memory]") && m.Content.Contains("Python versioning"));
    }

    [Fact]
    public async Task AnUnsureSkipStillKeepsTheTurn()
    {
        // Measured live: a turn that wrote a run script and a README came back
        // "skip" at 0.16. Forgetting needs a confident skip; this one keeps.
        var provider = new ScriptedChatProvider(
            """{"tool":"write_file","args":{"path":"run.ps1","content":"python hello.py"}}""",
            """{"tool":"final","args":{"text":"run.ps1 runs hello.py"}}""",
            "procedure | Run script | run.ps1 runs hello.py with python.");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace, 0.9),
            Choose(SmartRouter.SkipKnowledge, 0.16));
        using var session = Session(provider, engine);

        var notes = new List<SmartNote>();
        session.Decided += notes.Add;
        IReadOnlyList<Distilled>? learned = null;
        session.Learned += items => learned = items;

        await session.SubmitAsync("make a run.ps1 that runs hello.py", CancellationToken.None);
        Assert.True(await WaitForAsync(() => learned is not null), "an unsure skip was treated as a confident one");

        Assert.Equal(1, session.Stats().Graph!.Value.Knowledge);
        Assert.Contains(notes, n => n.Kind == "knowledge" && n.Verdict.StartsWith("kept 1 item") && n.Verdict.Contains("not sure"));
    }

    [Fact]
    public async Task ATurnJudgedNotWorthKeepingStoresNothing()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"hello!"}}""", "fact | should not | be stored");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly, 0.9), Choose(SmartRouter.SkipKnowledge, 0.9));
        using var session = Session(provider, engine);

        var notes = new List<SmartNote>();
        session.Decided += notes.Add;

        await session.SubmitAsync("say hello to me please", CancellationToken.None);
        Assert.True(await WaitForAsync(() => notes.Any(n => n.Kind == "knowledge")), "the verdict was never shown");

        Assert.Contains(notes, n => n.Kind == "knowledge" && n.Verdict == "nothing worth keeping");
        Assert.Equal(0, session.Stats().Graph!.Value.Knowledge);
        Assert.Equal(1, session.Stats().Graph!.Value.Turns);      // the turn itself is remembered
    }

    [Fact]
    public async Task WithKnowledgeInTheGraphTheEngineIsAskedAndTheMaterialReachesTheModel()
    {
        // Seed the graph as an earlier session would have.
        using (var seed = KnowledgeGraph.Open(Path.Combine(new WorkspaceStore(_root).Ensure().Dir, "graph"))!)
        {
            seed.RememberTurn("old-1", "build", "ok");
            seed.Learn("old-1", "Build command", "Build with dotnet build src/BoardApi/BoardApi.csproj.", "procedure",
                new Rationale("q", "save", 0.9, "b"), ["src/BoardApi/BoardApi.csproj"]);
        }

        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"built"}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace, 0.9),
            Choose(SmartRouter.ConsultGraph, 0.8),
            Choose(SmartRouter.ByKeywords, 0.7),
            Choose(SmartRouter.KeepDraft, 0.9),
            Choose(SmartRouter.SkipKnowledge, 0.9));
        using var session = Session(provider, engine);

        var notes = new List<SmartNote>();
        session.Decided += notes.Add;

        await session.SubmitAsync("run the build command for the board api", CancellationToken.None);

        Assert.Contains(notes, n => n.Kind == "graph" && n.Verdict.Contains("by_keywords") && n.Verdict.Contains("1 item"));
        Assert.Contains(provider.Calls[0], m => m.Role == "user" && m.Content.Contains("[graph memory]") && m.Content.Contains("Build command"));
        Assert.Contains(engine.Questions, q => q == SmartRouter.GraphHelpsQuestion);
        Assert.Contains(engine.Questions, q => q == SmartRouter.GraphStrategyQuestion);
        Assert.Equal(1, session.Stats().Graph!.Value.Helped);        // the item is now linked to this turn
    }

    [Fact]
    public async Task WhenTheEngineSaysTheGraphWillNotHelpNothingIsInjected()
    {
        using (var seed = KnowledgeGraph.Open(Path.Combine(new WorkspaceStore(_root).Ensure().Dir, "graph"))!)
        {
            seed.RememberTurn("old-1", "x", "y");
            seed.Learn("old-1", "Something", "Some fact.", "fact", new Rationale("q", "save", 0.9, "b"), []);
        }

        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"ok"}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly, 0.9),
            Choose(SmartRouter.SkipGraph, 0.8),
            Choose(SmartRouter.KeepDraft, 0.9),
            Choose(SmartRouter.SkipKnowledge, 0.9));
        using var session = Session(provider, engine);

        await session.SubmitAsync("what is the capital of france", CancellationToken.None);

        Assert.DoesNotContain(provider.Calls[0], m => m.Content.Contains("[graph memory]"));
        Assert.DoesNotContain(engine.Questions, q => q == SmartRouter.GraphStrategyQuestion);
    }
}
