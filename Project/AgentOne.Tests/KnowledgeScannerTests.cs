using AgentOne.Agent;
using AgentOne.Graph;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>
/// An engine that answers from the section itself — "must" / "never" /
/// "반드시" reads as a guideline — so parallel calls get deterministic answers
/// in any order. Counts the calls, which is what "only changed sections are
/// judged" is measured by.
/// </summary>
internal sealed class ContentEngine : IDecisionEngine
{
    private int _calls;
    public int Calls => _calls;
    public string Name => "content";

    public Task<Decision> ChooseAsync(string state, string question, IReadOnlyList<DecisionOption> options, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var rule = state.Contains("must", StringComparison.OrdinalIgnoreCase) || state.Contains("never", StringComparison.OrdinalIgnoreCase)
                   || state.Contains("반드시", StringComparison.Ordinal);
        var choice = rule ? SmartRouter.GuidelineOption : SmartRouter.KnowledgeOption;
        return Task.FromResult(new Decision(true, choice, 0.9, new Dictionary<string, double> { [choice] = 0.9 }, "ok", 1));
    }
}

/// <summary>`knowledge init` / `update`: parsing, classification onto edges, and the incremental rules — on a real Kùzu graph.</summary>
public class KnowledgeScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-one-kscan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly KnowledgeGraph _graph;

    public KnowledgeScannerTests()
    {
        _root = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        _graph = KnowledgeGraph.Open(Path.Combine(_dir, "graph")) ?? throw new InvalidOperationException("Kùzu is not available in the test output");

        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), """
            # Rules

            ## Commits
            Commit messages must be in English. Never push to main directly.

            ## Build
            The build takes about ten seconds and writes to bin/Debug.
            """.Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(_root, "docs", "arch.md"), """
            # Architecture
            Three actors: the stage, the bot gateway and the loop that runs the agent.
            """.Replace("\r\n", "\n"));
    }

    public void Dispose()
    {
        _graph.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private KnowledgeScanner Scanner(IDecisionEngine? engine) =>
        new(_graph, _root, engine is null ? null : new SmartRouter(engine, 0.6, "everyday", null));

    [Fact]
    public void HeadingsSplitTheFileButAHashInsideACodeFenceDoesNot()
    {
        var sections = KnowledgeScanner.Parse("""
            intro text before any heading
            # A
            text of a
            ```bash
            # not a heading, a shell comment
            ```
            ## B
            text of b
            # A
            second a
            """.Replace("\r\n", "\n"), "file.md");

        Assert.Equal(["file.md", "A", "A › B", "A"], sections.Select(s => s.Heading));
        Assert.Contains("# not a heading", sections[1].Body);
        Assert.Equal(1, sections[3].Ordinal);                     // the repeated heading is the second "A"
        Assert.NotEqual(KnowledgeScanner.SectionId("f.md", sections[1]), KnowledgeScanner.SectionId("f.md", sections[3]));
    }

    [Fact]
    public async Task InitPutsEachSectionOnTheEdgeItsClassificationNames()
    {
        var engine = new ContentEngine();
        var report = await Scanner(engine).ScanAsync(rebuild: false, scope: null, progress: null, CancellationToken.None);

        Assert.Equal(2, report.NewDocs);
        Assert.Equal(3, report.Added);
        Assert.Equal(3, engine.Calls);
        Assert.Equal(new DocStats(2, 1, 2), _graph.DocCounts());

        var guides = _graph.QueryTable("MATCH (d:Doc)-[:GUIDES]->(k:Knowledge)-[:JUSTIFIED_BY]->(r:Rationale) RETURN d.path, k.title, k.kind, r.choice").Rows;
        Assert.Equal(["CLAUDE.md", "Rules › Commits", "guideline", "guideline"], Assert.Single(guides));

        // They are ordinary knowledge to the recall that runs before a turn.
        Assert.NotEmpty(_graph.ByKeywords(["actors"]));
        Assert.NotEmpty(_graph.ByPath("CLAUDE.md"));
    }

    [Fact]
    public async Task UpdateJudgesOnlyWhatChangedAndRemovesWhatIsGone()
    {
        await Scanner(new ContentEngine()).ScanAsync(false, null, null, CancellationToken.None);
        var commitsId = _graph.QueryTable("MATCH (k:Knowledge) WHERE k.title = 'Rules › Commits' RETURN k.id").Rows[0][0];
        _graph.RememberTurn("t1", "how do I commit?", "answered");
        _graph.MarkHelped([commitsId], "t1", "graph");

        // One section edited, one section deleted, one file deleted.
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), """
            # Rules

            ## Commits
            Commit messages must be in English and must reference an issue.
            """.Replace("\r\n", "\n"));
        File.Delete(Path.Combine(_root, "docs", "arch.md"));

        var engine = new ContentEngine();
        var report = await Scanner(engine).ScanAsync(false, null, null, CancellationToken.None);

        Assert.Equal(1, engine.Calls);                           // only the edited section
        Assert.Equal(1, report.Updated);
        Assert.Equal(2, report.Removed);                         // "Build", and the architecture section with its file
        Assert.Equal(1, report.RemovedDocs);
        Assert.Equal(new DocStats(1, 1, 0), _graph.DocCounts());

        // Updated in place: same node, new text, the use count kept.
        var row = _graph.QueryTable($"MATCH (k:Knowledge {{id: '{commitsId}'}}) RETURN k.text, k.uses").Rows.Single();
        Assert.Contains("reference an issue", row[0]);
        Assert.Equal("1", row[1]);
        Assert.Equal("1", _graph.QueryTable($"MATCH (k:Knowledge {{id: '{commitsId}'}})-[:JUSTIFIED_BY]->(r) RETURN count(r)").Rows[0][0]);

        // Nothing changed: nothing judged.
        var idle = new ContentEngine();
        var again = await Scanner(idle).ScanAsync(false, null, null, CancellationToken.None);
        Assert.Equal(0, idle.Calls);
        Assert.Equal(1, again.Unchanged);
    }

    [Fact]
    public async Task AScopedScanLeavesTheRestAloneAndRebuildJudgesEverythingAgain()
    {
        await Scanner(new ContentEngine()).ScanAsync(false, null, null, CancellationToken.None);
        File.Delete(Path.Combine(_root, "CLAUDE.md"));

        var scoped = await Scanner(new ContentEngine()).ScanAsync(false, "docs", null, CancellationToken.None);
        Assert.Equal(0, scoped.RemovedDocs);                     // CLAUDE.md is outside the scope
        Assert.Equal(2, _graph.DocCounts().Docs);

        var engine = new ContentEngine();
        await Scanner(engine).ScanAsync(rebuild: true, scope: "docs", progress: null, CancellationToken.None);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task WithoutAnEngineTheWordRuleClassifiesAndSaysSo()
    {
        var report = await Scanner(null).ScanAsync(false, null, null, CancellationToken.None);

        Assert.Equal(3, report.ByRule);
        Assert.Equal(0, report.EngineCalls);
        var rows = _graph.QueryTable("MATCH (d:Doc)-[:GUIDES]->(k:Knowledge)-[:JUSTIFIED_BY]->(r:Rationale) RETURN k.title, r.question").Rows;
        Assert.Contains(rows, r => r[0] == "Rules › Commits" && r[1].StartsWith("rule:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Conventions", "Use tabs.", "guideline")]
    [InlineData("Setup", "You must install .NET 10.\nNever commit secrets.\nAlways run the tests.", "guideline")]
    [InlineData("커밋", "커밋 메시지는 반드시 영어로 작성한다.\nmain 에 직접 push 하지 말 것.", "guideline")]
    [InlineData("Actors", "The stage supervises the workspaces and the bot.\nThe loop runs the agent.", "knowledge")]
    public void TheWordRuleReadsImperatives(string heading, string body, string expected) =>
        Assert.Equal(expected, KnowledgeScanner.ByRule(heading, body).Kind);

    [Theory]
    [InlineData("C:/Program Files/Git/knowledge update", "MINGW64", "/knowledge update")]
    [InlineData("C:/Program Files/Git/cypher MATCH (n) RETURN n", "MINGW64", "/cypher MATCH (n) RETURN n")]
    [InlineData("C:/Program Files/Git/knowledge update", null, "C:/Program Files/Git/knowledge update")]   // not MSYS: a real path
    [InlineData("C:/work/notes/knowledge update", "MINGW64", "C:/work/notes/knowledge update")]            // not under Git
    [InlineData("요약해줘", "MINGW64", "요약해줘")]
    public void AGitBashConvertedSlashCommandIsPutBack(string arrived, string? msystem, string expected) =>
        Assert.Equal(expected, AgentOne.Commands.AskCommand.UndoMsysPath(arrived, msystem));

    [Fact]
    public void CypherResultsCarryTheirColumnNames()
    {
        _graph.RememberTurn("t1", "a", "b");
        var (columns, rows) = _graph.QueryTable("MATCH (t:Turn) RETURN t.id AS id, t.asked");
        Assert.Equal(["id", "t.asked"], columns);
        Assert.Equal(["t1", "a"], Assert.Single(rows));
    }
}

/// <summary>/knowledge and /cypher through the session: not a turn, the model never called.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class KnowledgeChatCommandTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public KnowledgeChatCommandTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-kchat-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Guide\n\n## Rules\nYou must run the tests before every commit.\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task KnowledgeInitAndCypherRunInTheSessionWithoutAModelCall()
    {
        var config = new AgentConfig();
        config.TrySet("saveSessions", "false", out _);
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"model"}}""");
        using var session = new ChatSession(config, _root, streaming: false, provider, new ContentEngine(), smartAvailable: true)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };

        var activity = new List<string>();
        session.ActivityStarted += a => activity.Add(a);

        var init = await session.SubmitAsync("/knowledge init", CancellationToken.None);
        Assert.True(init!.Succeeded);
        Assert.Contains("1 guideline sections (GUIDES)", init.Text);
        Assert.Contains(activity, a => a.Contains("README.md"));

        var cypher = await session.SubmitAsync("/cypher MATCH (d:Doc)-[:GUIDES]->(k:Knowledge) RETURN d.path, k.title", CancellationToken.None);
        Assert.StartsWith("d.path\tk.title\nREADME.md\tGuide › Rules", cypher!.Text);

        var bad = await session.SubmitAsync("/cypher MATCH (x:Nope) RETURN x", CancellationToken.None);
        Assert.False(bad!.Succeeded);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, session.Stats().Counters.Turns);
    }
}
