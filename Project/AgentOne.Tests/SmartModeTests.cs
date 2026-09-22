using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>A decision engine that answers from a script, in order, and records what it was asked.</summary>
internal sealed class ScriptedDecisionEngine(params Decision[] answers) : IDecisionEngine
{
    private int _index;

    public int Calls { get; private set; }
    public List<string> States { get; } = [];
    public List<string> Questions { get; } = [];
    public IReadOnlyList<DecisionOption>? LastOptions { get; private set; }
    public string? LastState => States.Count == 0 ? null : States[^1];

    public string Name => "scripted";

    public Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<DecisionOption> options, CancellationToken ct)
    {
        Calls++;
        States.Add(state);
        Questions.Add(question);
        LastOptions = options;
        var answer = answers[Math.Min(_index, answers.Length - 1)];
        _index++;
        return Task.FromResult(answer);
    }
}

/// <summary>
/// Smart mode's two questions. Both have fixed options, so the only things
/// that vary are the state the engine is shown and what its answer is taken
/// to mean.
/// </summary>
public class SmartRouterTests
{
    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 100);

    private static SmartRouter Router(IDecisionEngine engine, double floor = 0.60, string? reasoning = "big-model") =>
        new(engine, floor, "small-model", reasoning);

    // --- the gate -------------------------------------------------------------

    [Theory]
    [InlineData("hi", false)]
    [InlineData("안녕", false)]
    [InlineData("123456789", false)]      // nine: one short
    [InlineData("1234567890", true)]      // ten: the threshold
    [InlineData("  what is MSA?  ", true)]
    public void OnlyARequestOfTenCharactersOrMoreIsRouted(string request, bool expected)
    {
        Assert.Equal(expected, SmartRouter.Applies(request));
    }

    // --- the route ------------------------------------------------------------

    [Fact]
    public async Task TheRouteQuestionOffersExactlyTheThreeResources()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.SearchWeb, 0.9));

        await Router(engine).RouteAsync("what is the weather in Seoul today?", "", CancellationToken.None);

        Assert.Equal(SmartRouter.RouteQuestion, engine.Questions[0]);
        Assert.Equal([SmartRouter.SearchWeb, SmartRouter.ReadWorkspace, SmartRouter.AnswerDirectly],
                     engine.LastOptions!.Select(o => o.Name));
    }

    [Fact]
    public async Task TheEngineIsToldTheRequestTheModelsAndWhatIsAlreadyKnown()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly, 0.9));

        await Router(engine).RouteAsync("and the trade-offs?", "[tool:web_read] MSA guide", CancellationToken.None);

        var state = engine.LastState!;
        Assert.Contains("and the trade-offs?", state);
        Assert.Contains("small-model", state);
        Assert.Contains("big-model", state);
        Assert.Contains("[tool:web_read] MSA guide", state);
    }

    [Theory]
    [InlineData(SmartRouter.SearchWeb, Route.Web, ToolCatalog.WebFamily)]
    [InlineData(SmartRouter.ReadWorkspace, Route.Files, ToolCatalog.FilesFamily)]
    public async Task AConfidentRouteRestrictsTheLoopToOneFamily(string choice, Route expected, string family)
    {
        var route = await Router(new ScriptedDecisionEngine(Choose(choice, 0.9)))
            .RouteAsync("a request long enough", "", CancellationToken.None);

        Assert.True(route.Steers);
        Assert.Equal(expected, route.Route);
        Assert.Equal([family], route.Families!);
        Assert.Contains($"[route: {expected.ToString().ToLowerInvariant()}]", route.Guidance("a request long enough"));
    }

    [Fact]
    public async Task AnswerDirectlyLeavesNoToolAtAll()
    {
        var route = await Router(new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly, 0.95)))
            .RouteAsync("explain the actor model", "", CancellationToken.None);

        Assert.Equal(Route.Answer, route.Route);
        Assert.Empty(route.Families!);
    }

    [Theory]
    [InlineData(0.60, true)]      // the floor is inclusive
    [InlineData(0.59, false)]
    public async Task OnlyAChoiceAtOrAboveTheFloorSteers(double confidence, bool steers)
    {
        var route = await Router(new ScriptedDecisionEngine(Choose(SmartRouter.SearchWeb, confidence)))
            .RouteAsync("a request long enough", "", CancellationToken.None);

        Assert.Equal(steers, route.Steers);
        if (!steers)
        {
            Assert.Null(route.Families);                                        // every tool stays available
            Assert.Equal("a request long enough", route.Guidance("a request long enough"));
        }
    }

    [Fact]
    public async Task AFailedEngineDoesNotSteer()
    {
        var route = await Router(new ScriptedDecisionEngine(Decision.Failed("service down")))
            .RouteAsync("a request long enough", "", CancellationToken.None);

        Assert.Null(route.Route);
        Assert.False(route.Steers);
    }

    // --- the escalation -------------------------------------------------------

    [Fact]
    public async Task WithoutAReasoningModelTheEngineIsNeverAsked()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.EscalateOption, 0.99));

        var judged = await Router(engine, reasoning: null)
            .EscalateAsync("request", "material", "draft", CancellationToken.None);

        Assert.False(judged.Escalate);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task TheEngineSeesTheRequestTheMaterialTheDraftAndBothModels()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.KeepDraft, 0.8));

        await Router(engine).EscalateAsync(
            "why is the build slow?",
            "[tool:read_file] Directory.Build.props …",
            "Because of X.",
            CancellationToken.None);

        var state = engine.LastState!;
        Assert.Equal(SmartRouter.EscalationQuestion, engine.Questions[0]);
        Assert.Contains("why is the build slow?", state);
        Assert.Contains("Directory.Build.props", state);
        Assert.Contains("Because of X.", state);
        Assert.Contains("small-model", state);
        Assert.Contains("big-model", state);
        Assert.Equal([SmartRouter.KeepDraft, SmartRouter.EscalateOption], engine.LastOptions!.Select(o => o.Name));
    }

    [Fact]
    public async Task NoMaterialIsSaidSoRatherThanLeftBlank()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.KeepDraft, 0.8));

        await Router(engine).EscalateAsync("request", "", "draft", CancellationToken.None);

        Assert.Contains("no tool was used", engine.LastState!);
    }

    [Fact]
    public async Task LongMaterialIsClippedWithACount()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.KeepDraft, 0.8));
        var material = new string('m', SmartRouter.MaterialChars + 500);

        await Router(engine).EscalateAsync("request", material, "draft", CancellationToken.None);

        Assert.Contains("500 more characters", engine.LastState!);
        Assert.True(engine.LastState!.Length < material.Length);
    }

    [Theory]
    [InlineData(SmartRouter.EscalateOption, 0.9, true)]
    [InlineData(SmartRouter.EscalateOption, 0.25, true)]     // the floor guards steering, not a slower second look
    [InlineData(SmartRouter.KeepDraft, 0.9, false)]
    [InlineData(SmartRouter.KeepDraft, 0.2, false)]
    public async Task EscalationFollowsTheChoiceNotTheFloor(string choice, double confidence, bool expected)
    {
        var judged = await Router(new ScriptedDecisionEngine(Choose(choice, confidence)))
            .EscalateAsync("request", "", "draft", CancellationToken.None);

        Assert.Equal(expected, judged.Escalate);
    }

    [Fact]
    public async Task BothStagesAreAnnouncedSoNeitherLooksLikeAHang()
    {
        var router = Router(new ScriptedDecisionEngine(Choose(SmartRouter.SearchWeb, 0.9), Choose(SmartRouter.KeepDraft, 0.9)));
        var activities = new List<string>();
        router.ActivityStarted += activities.Add;

        await router.RouteAsync("a request long enough", "", CancellationToken.None);
        await router.EscalateAsync("a request long enough", "", "draft", CancellationToken.None);

        Assert.Contains(activities, a => a.Contains("deciding what this needs"));
        Assert.Contains(activities, a => a.Contains("judging the draft"));
    }
}

/// <summary>The hand-off to the stronger model and the line that brings its answer back.</summary>
public class ReasoningSubtaskTests
{
    [Fact]
    public async Task TheStrongModelGetsRequestMaterialAndDraftAndNoTools()
    {
        var strong = new ScriptedChatProvider("  a careful answer  ");

        var reply = await ReasoningSubtask.RunAsync(strong, "small-model", "the request", "[tool:grep] hits", "the draft", CancellationToken.None);

        Assert.Equal("a careful answer", reply);
        var call = strong.Calls[0];
        Assert.Equal("system", call[0].Role);
        Assert.Contains("no JSON, no tool calls", call[0].Content);
        Assert.Contains("the request", call[1].Content);
        Assert.Contains("[tool:grep] hits", call[1].Content);
        Assert.Contains("the draft", call[1].Content);
    }

    [Fact]
    public async Task TheCallIsStreamedAndProgressIsCounted()
    {
        var strong = new ScriptedChatProvider("twenty-one characters") { ChunkSize = 5 };
        var seen = new List<int>();

        await ReasoningSubtask.RunAsync(strong, "small", "r", "", "d", CancellationToken.None, seen.Add);

        Assert.Equal([5, 10, 15, 20, 21], seen);
    }

    [Theory]
    [InlineData("<think>hmm</think>\nthe answer", "the answer")]
    [InlineData("<THINK>a\nb</THINK>the answer", "the answer")]
    [InlineData("the answer <think>cut off", "the answer")]
    [InlineData("no tags at all", "no tags at all")]
    public void TheThinkingBlockIsStrippedBeforeTheHandBack(string reply, string expected)
    {
        Assert.Equal(expected, ReasoningSubtask.StripThinking(reply).Trim());
    }

    [Fact]
    public void TheFeedBackLineIsTaggedLikeMaterialAndAsksForTheFinalAnswer()
    {
        var line = ReasoningSubtask.FeedBack("big-model", "deep thoughts");

        Assert.StartsWith("[reasoning:big-model] deep thoughts", line);
        Assert.Contains("final answer", line);
    }
}

/// <summary>
/// The session's own state. Actor-shaped rather than an actor framework — one
/// owner, serialised mutations, snapshot reads — because a Native AOT single
/// binary cannot afford an actor runtime for a REPL turn.
/// </summary>
public class SessionStateTests
{
    [Fact]
    public void ItStartsWhereItWasTold()
    {
        Assert.True(new SessionState(smart: true).Read().Smart);
        Assert.False(new SessionState(smart: false).Read().Smart);
    }

    [Fact]
    public void TogglingReturnsWhatItBecame()
    {
        var state = new SessionState(false);

        Assert.True(state.ToggleSmart());
        Assert.True(state.Read().Smart);
        Assert.False(state.ToggleSmart());
    }

    [Fact]
    public void ResetForgetsTheTurnsButNotTheMode()
    {
        var state = new SessionState(smart: true);
        state.CountTurn();

        state.Reset();

        var snapshot = state.Read();
        Assert.True(snapshot.Smart);          // the mode is the operator's choice, not turn state
        Assert.Equal(0, snapshot.Turns);
    }

    [Fact]
    public void ASnapshotDoesNotChangeUnderneathTheReader()
    {
        var state = new SessionState(false);
        var before = state.Read();

        state.ToggleSmart();
        state.CountTurn();

        Assert.False(before.Smart);
        Assert.Equal(0, before.Turns);
    }

    [Fact]
    public async Task ConcurrentTurnsAreAllCounted()
    {
        var state = new SessionState(true);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(state.CountTurn)));

        Assert.Equal(200, state.Read().Turns);
    }
}
