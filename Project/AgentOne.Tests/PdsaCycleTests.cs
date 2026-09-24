using AgentOne.Agent;
using AgentOne.Graph;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>
/// The improvement cycle: Plan · Do · Study · Act recorded in the workspace
/// graph, and — the reason it is in that graph at all — wired to the knowledge
/// it produced when it closes.
/// </summary>
public class PdsaCycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-one-pdsa-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly KnowledgeGraph _graph;

    public PdsaCycleTests()
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

    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 100);

    private static SmartRouter Router(IDecisionEngine engine, double floor = 0.60) =>
        new(engine, floor, "small-model", "big-model");

    // --- the graph ------------------------------------------------------------

    [Fact]
    public void ACycleHoldsItsPhasesInPlanDoStudyActOrderHoweverTheyWereRecorded()
    {
        var id = _graph.StartCycle("build the board API");

        _graph.RememberTurn("t2", "build it", "built");
        _graph.RecordPhase(id, KnowledgeGraph.DoPhase, "t2", "build it", "write_file, run_command");
        _graph.RememberTurn("t1", "design it", "designed");
        _graph.RecordPhase(id, KnowledgeGraph.PlanPhase, "t1", "design it", "design", new Dictionary<string, string> { ["expected"] = "dotnet build succeeds" });

        var cycle = _graph.OpenCycle()!;
        Assert.Equal(id, cycle.Id);
        Assert.Equal([KnowledgeGraph.PlanPhase, KnowledgeGraph.DoPhase], cycle.Phases.Select(p => p.Kind));
        Assert.Equal("dotnet build succeeds", cycle.Expected);
        Assert.Equal("t1", cycle.Phase(KnowledgeGraph.PlanPhase)!.TurnId);
        Assert.False(cycle.Closed);
    }

    [Fact]
    public void RecordingAPhaseTwiceReplacesItRatherThanDoublingTheEdges()
    {
        var id = _graph.StartCycle("a plan that got corrected");
        _graph.RememberTurn("t1", "plan v1", "v1");

        _graph.RecordPhase(id, KnowledgeGraph.PlanPhase, "t1", "plan v1", "first");
        _graph.RecordPhase(id, KnowledgeGraph.PlanPhase, "t1", "plan v2", "second");

        var cycle = _graph.OpenCycle()!;
        Assert.Single(cycle.Phases);
        Assert.Equal("second", cycle.Phases[0].Note);
        Assert.Equal(1, _graph.CycleStats().Phases);
    }

    [Fact]
    public void ClosingWiresTheCycleToWhatItTaughtAndToWhatItBuiltOn()
    {
        // Knowledge from an earlier cycle, which this one is handed.
        _graph.RememberTurn("old", "how does the build work?", "explained");
        _graph.Learn("old", "Build command", "Build with `dotnet build`.", "procedure", Why, ["src/Api.csproj"]);
        var existing = _graph.Recent(1)[0];

        var id = _graph.StartCycle("add the endpoint");

        _graph.RememberTurn("t1", "plan it", "planned");
        _graph.RecordPhase(id, KnowledgeGraph.PlanPhase, "t1", "plan it", "design");

        _graph.RememberTurn("t2", "build it", "built");
        _graph.MarkHelped([existing.Id], "t2", "by_keywords");             // the cycle stood on what was already known
        _graph.RecordPhase(id, KnowledgeGraph.DoPhase, "t2", "build it", "write_file");
        _graph.Learn("t2", "Endpoint route", "The endpoint lives at /api/posts.", "fact", Why, ["src/Api.csproj"]);

        _graph.RememberTurn("t3", "ship it", "shipped");
        _graph.RecordPhase(id, KnowledgeGraph.ActPhase, "t3", "ship it", "run_command");

        var closed = _graph.CloseCycle(id, SmartRouter.MetVerdict);

        Assert.Equal(2, closed.Edges);
        Assert.Equal(["Endpoint route"], _graph.CycleKnowledge(id, "TAUGHT").Select(k => k.Title));
        Assert.Equal(["Build command"], _graph.CycleKnowledge(id, "BUILT_ON").Select(k => k.Title));

        Assert.Null(_graph.OpenCycle());
        Assert.Equal(SmartRouter.MetVerdict, _graph.Cycle(id)!.Verdict);
    }

    [Fact]
    public void KnowledgeLearnedInsideTheCycleCountsAsTaughtOnlyEvenWhenALaterPhaseUsesIt()
    {
        var id = _graph.StartCycle("one cycle");
        _graph.RememberTurn("t1", "plan", "planned");
        _graph.RecordPhase(id, KnowledgeGraph.PlanPhase, "t1", "plan", "design");
        _graph.Learn("t1", "A thing", "learned in this very cycle", "fact", Why, []);

        var learned = _graph.Recent(1)[0];
        _graph.RememberTurn("t2", "act", "acted");
        _graph.MarkHelped([learned.Id], "t2", "recent");
        _graph.RecordPhase(id, KnowledgeGraph.ActPhase, "t2", "act", "none");

        var closed = _graph.CloseCycle(id, SmartRouter.MetVerdict);

        Assert.Single(closed.Taught);
        Assert.Empty(closed.BuiltOn);
    }

    [Fact]
    public void OnlyOneCycleIsOpenAtATimeAndTheOrderIsKept()
    {
        var first = _graph.StartCycle("first");
        _graph.RememberTurn("t1", "plan", "planned");
        _graph.RecordPhase(first, KnowledgeGraph.PlanPhase, "t1", "plan", "design");

        var second = _graph.StartCycle("second");

        Assert.Equal(first + 1, second);
        Assert.Equal(second, _graph.OpenCycle()!.Id);
        Assert.Equal(KnowledgeGraph.AbandonedStatus, _graph.Cycle(first)!.Status);

        var order = _graph.Query("MATCH (a:Cycle)-[:NEXT_CYCLE]->(b:Cycle) RETURN a.id, b.id", 2);
        Assert.Single(order);
        Assert.Equal([first.ToString(), second.ToString()], order[0]);
    }

    // --- the session's use ----------------------------------------------------

    [Fact]
    public async Task ADesignOpensACycleAtPlanWithoutAskingTheEngine()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.DoStep, 0.9));
        var pdsa = new PdsaMemory(_graph);

        var turn = await pdsa.BeforeAsync(Router(engine), "build me a board API", designed: true, CancellationToken.None);

        Assert.NotNull(turn);
        Assert.True(turn.Opened);
        Assert.Equal(SmartRouter.PlanStep, turn.Step);
        Assert.Equal(0, engine.Calls);                    // the strong model already planned; nothing to ask
        Assert.False(turn.Decision.Called);
    }

    [Fact]
    public async Task WithNoCycleRunningOnlyAConfidentPlanOpensOne()
    {
        var pdsa = new PdsaMemory(_graph);

        var unsure = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.31));
        Assert.Null(await pdsa.BeforeAsync(Router(unsure), "how should we lay this out?", false, CancellationToken.None));
        Assert.Null(_graph.OpenCycle());

        var building = new ScriptedDecisionEngine(Choose(SmartRouter.DoStep, 0.95));
        Assert.Null(await pdsa.BeforeAsync(Router(building), "run the build please", false, CancellationToken.None));
        Assert.Null(_graph.OpenCycle());

        var planning = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.88));
        var turn = await pdsa.BeforeAsync(Router(planning), "how should we lay this out?", false, CancellationToken.None);

        Assert.NotNull(turn);
        Assert.True(turn.Opened);
        Assert.Equal(SmartRouter.PlanStep, turn.Step);
    }

    [Fact]
    public async Task InsideARunningCycleTheStepIsFollowedWithoutTheFloor()
    {
        var pdsa = new PdsaMemory(_graph);
        var opening = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.9));
        var plan = (await pdsa.BeforeAsync(Router(opening), "design the API", false, CancellationToken.None))!;
        await pdsa.RecordAsync(Router(opening), plan, "t1", "design the API", "design", "a plan", "expected: it builds", CancellationToken.None);

        // 0.28 is well under the floor; inside an open cycle it is still taken.
        var weak = new ScriptedDecisionEngine(Choose(SmartRouter.DoStep, 0.28));
        var turn = (await pdsa.BeforeAsync(Router(weak), "now build it", false, CancellationToken.None))!;

        Assert.False(turn.Opened);
        Assert.Equal(plan.Cycle, turn.Cycle);
        Assert.Equal(SmartRouter.DoStep, turn.Step);

        // The engine was told what the cycle has done and what it predicted.
        Assert.Contains("Steps already done: plan", weak.LastState);
        Assert.Contains("expected: it builds", weak.LastState);
    }

    [Fact]
    public async Task StudyJudgesTheOutcomeAgainstThePlanAndActClosesTheCycle()
    {
        var pdsa = new PdsaMemory(_graph);
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.9));
        var router = Router(engine);

        var plan = (await pdsa.BeforeAsync(router, "design the API", false, CancellationToken.None))!;
        await pdsa.RecordAsync(router, plan, "t1", "design the API", "design", "designed", "the build succeeds and /api/posts answers", CancellationToken.None);

        var study = new ScriptedDecisionEngine(Choose(SmartRouter.StudyStep, 0.9), Choose(SmartRouter.PartialVerdict, 0.7));
        var studyRouter = Router(study);
        var studyTurn = (await pdsa.BeforeAsync(studyRouter, "run the tests and see", false, CancellationToken.None))!;
        var record = await pdsa.RecordAsync(studyRouter, studyTurn, "t2", "run the tests and see", "run_command", "builds, but one test fails", null, CancellationToken.None);

        Assert.Equal(SmartRouter.PartialVerdict, record.VerdictChoice);
        Assert.Contains("the build succeeds", study.States[^1]);      // judged against the plan, not in a vacuum

        var act = new ScriptedDecisionEngine(Choose(SmartRouter.ActStep, 0.9));
        var actTurn = (await pdsa.BeforeAsync(Router(act), "commit what works and note the gap", false, CancellationToken.None))!;
        Assert.True(actTurn.Closes);

        await pdsa.RecordAsync(Router(act), actTurn, "t3", "commit it", "run_command", "committed", null, CancellationToken.None);
        var closed = pdsa.Close(actTurn.Cycle);

        Assert.Equal(SmartRouter.PartialVerdict, closed.Verdict);      // Study's verdict, carried onto the cycle
        Assert.Null(_graph.OpenCycle());
    }

    [Fact]
    public async Task APlanAfterACycleThatFellShortReinforcesIt()
    {
        var pdsa = new PdsaMemory(_graph);
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.9), Choose(SmartRouter.UnmetVerdict, 0.8));
        var router = Router(engine);

        var first = (await pdsa.BeforeAsync(router, "design the API", false, CancellationToken.None))!;
        await pdsa.RecordAsync(router, first, "t1", "design the API", "design", "designed", "it builds", CancellationToken.None);
        await pdsa.RecordAsync(router, first with { Step = SmartRouter.StudyStep }, "t2", "does it build?", "run_command", "no: two errors", null, CancellationToken.None);

        var again = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.9));
        var second = (await pdsa.BeforeAsync(Router(again), "let us plan the fix properly", false, CancellationToken.None))!;

        Assert.True(second.Opened);
        Assert.Equal(first.Cycle, second.Reinforces);

        var edges = _graph.Query("MATCH (a:Cycle)-[:REINFORCES]->(b:Cycle) RETURN a.id, b.id", 2);
        Assert.Single(edges);
        Assert.Equal([second.Cycle.ToString(), first.Cycle.ToString()], edges[0]);
    }

    [Fact]
    public async Task ACycleThatActedWithoutStudyingIsClosedUnjudgedRatherThanCountedAsASuccess()
    {
        var pdsa = new PdsaMemory(_graph);
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.PlanStep, 0.9));
        var router = Router(engine);

        var plan = (await pdsa.BeforeAsync(router, "design the API", false, CancellationToken.None))!;
        await pdsa.RecordAsync(router, plan, "t1", "design the API", "design", "designed", "it builds", CancellationToken.None);

        var closed = pdsa.Close(plan.Cycle);

        Assert.Equal("unjudged", closed.Verdict);
        Assert.Equal(0, _graph.CycleStats().Judged);
    }

    [Fact]
    public void TheStatusLineSaysNothingUntilACycleHasRun()
    {
        var pdsa = new PdsaMemory(_graph);
        Assert.Null(pdsa.StatusLine());

        var id = _graph.StartCycle("something");
        _graph.RememberTurn("t1", "plan", "planned");
        _graph.RecordPhase(id, KnowledgeGraph.PlanPhase, "t1", "plan", "design");

        var line = pdsa.StatusLine()!;
        Assert.Contains("1 cycles", line);
        Assert.Contains("running #" + id, line);
    }
}

/// <summary>The cycle inside a real session: opened by a plan, closed by an act, wired to what the turn taught.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class PdsaSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public PdsaSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-pdsa-s-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine engine)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine, true) { NamesTasks = false };
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
    public async Task APlanTurnOpensACycleAndAnActTurnClosesItOntoWhatTheTurnTaught()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"here is the layout: src/Api with Program.cs"}}""",
            """{"tool":"write_file","args":{"path":"src/Api/Program.cs","content":"// entry"}}""",
            """{"tool":"final","args":{"text":"committed the entry point"}}""",
            "fact | Entry point | src/Api/Program.cs holds the top-level statements.");

        // Turn 1: route, pdsa(plan), worth-saving(skip).
        // Turn 2: route, pdsa(act), worth-saving(save) — the scripted engine
        // repeats its last answer, so the order is what matters, not the count.
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace, 0.9),
            Choose(SmartRouter.PlanStep, 0.9),
            Choose(SmartRouter.SkipKnowledge, 0.9),
            Choose(SmartRouter.WorkInWorkspace, 0.9),
            Choose(SmartRouter.ActStep, 0.9),
            Choose(SmartRouter.SaveKnowledge, 0.85));

        using var session = Session(provider, engine);

        var notes = new List<SmartNote>();
        session.Decided += notes.Add;
        PdsaClosing? closed = null;
        session.CycleClosed += c => closed = c;

        await session.SubmitAsync("how should we lay out the new api project?", CancellationToken.None);

        Assert.Contains(notes, n => n.Kind == "pdsa" && n.Verdict.Contains("opened at plan"));
        Assert.Contains(session.Stats().Describe(), l => l.StartsWith("pdsa") && l.Contains("running #1"));

        await session.SubmitAsync("good — commit that and write it down", CancellationToken.None);

        Assert.True(await WaitForAsync(() => closed is not null), "the act turn did not close the cycle");
        Assert.Equal(1, closed!.Cycle);
        Assert.Single(closed.Taught);                 // closed behind the distillation, so the lesson is there
        Assert.Equal("unjudged", closed.Verdict);     // it acted without a study turn

        var dir = Path.Combine(session.Workspace.Dir, "graph");
        session.Dispose();

        using var graph = KnowledgeGraph.Open(dir)!;
        var rows = graph.Query("MATCH (c:Cycle)-[:TAUGHT]->(k:Knowledge) RETURN c.id, c.status, k.title", 3);
        Assert.Single(rows);
        Assert.Equal(["1", KnowledgeGraph.ClosedStatus, "Entry point"], rows[0]);

        var cycle = graph.Cycle(1)!;
        Assert.Equal([KnowledgeGraph.PlanPhase, KnowledgeGraph.ActPhase], cycle.Phases.Select(p => p.Kind));
        Assert.Contains("src/Api", cycle.Expected);   // the plan turn's answer is what the cycle predicted
    }

    [Fact]
    public async Task ARequestThatIsNotAPlanLeavesNoCycleBehind()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"the build passed"}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.WorkInWorkspace, 0.9),
            Choose(SmartRouter.DoStep, 0.95),
            Choose(SmartRouter.SkipKnowledge, 0.9));

        using var session = Session(provider, engine);
        await session.SubmitAsync("run the build and tell me if it passes", CancellationToken.None);

        Assert.Contains(session.Stats().Describe(), l => l.StartsWith("pdsa") && l.Contains("no improvement cycle yet"));
    }
}
