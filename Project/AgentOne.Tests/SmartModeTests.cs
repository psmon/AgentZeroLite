using AgentOne.Agent;
using AgentOne.Llm.Decision;

namespace AgentOne.Tests;

/// <summary>A decision engine that answers from a script, and counts its calls.</summary>
internal sealed class ScriptedDecisionEngine(Decision answer) : IDecisionEngine
{
    public int Calls { get; private set; }
    public IReadOnlyList<DecisionOption>? LastOptions { get; private set; }
    public string? LastState { get; private set; }

    public string Name => "scripted";

    public Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<DecisionOption> options, CancellationToken ct)
    {
        Calls++;
        LastState = state;
        LastOptions = options;
        return Task.FromResult(answer);
    }
}

public class PlannerTests
{
    [Fact]
    public void APlanEnvelopeBecomesOptions()
    {
        var options = Planner.Parse(
            """{"tool":"plan","args":{"read_local":"Read the files here.","search_web":"Search the web."}}""", 4);

        Assert.Equal(2, options.Count);
        Assert.Equal("read_local", options[0].Name);
        Assert.Equal("Read the files here.", options[0].Description);
    }

    [Fact]
    public void FencedAndPrefacedRepliesStillParse()
    {
        var options = Planner.Parse(
            """
            Here is the plan:
            ```json
            {"tool":"plan","args":{"a":"Do A.","b":"Do B."}}
            ```
            """, 4);

        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void MoreThanTheLimitIsTrimmed()
    {
        var options = Planner.Parse(
            """{"tool":"plan","args":{"a":"A.","b":"B.","c":"C.","d":"D.","e":"E.","f":"F."}}""", 4);

        Assert.Equal(4, options.Count);
    }

    [Fact]
    public void EmptyDescriptionsAreDropped()
    {
        var options = Planner.Parse("""{"tool":"plan","args":{"a":"A.","b":"   "}}""", 4);

        Assert.Single(options);
    }

    [Theory]
    [InlineData("""{"tool":"final","args":{"text":"I'll just answer."}}""")]   // answered instead of planning
    [InlineData("I think we should read the files.")]                          // prose
    [InlineData("")]
    public void AnythingThatIsNotAPlanYieldsNoOptions(string raw)
    {
        Assert.Empty(Planner.Parse(raw, 4));
    }

    [Fact]
    public void ThePromptInsistsTheApproachesDiffer()
    {
        var prompt = Planner.Prompt("files, web");

        // Low confidence comes from indistinguishable options, so this is the
        // instruction that keeps the decision worth making.
        Assert.Contains("DIFFERENT", prompt);
        Assert.Contains("files, web", prompt);
    }
}

public class SmartTurnTests
{
    private static Decision Confident(string choice) =>
        new(true, choice, 0.92, new Dictionary<string, double> { [choice] = 0.95 }, "ok", 100);

    private static Decision Unsure(string choice) =>
        new(true, choice, 0.31, new Dictionary<string, double> { [choice] = 0.55 }, "ok", 100);

    private static SmartTurn Turn(ScriptedChatProvider provider, IDecisionEngine engine, double floor = 0.60) =>
        new(provider, engine, floor);

    private const string TwoApproaches =
        """{"tool":"plan","args":{"read_local":"Read the files here.","search_web":"Search the web."}}""";

    [Fact]
    public async Task AConfidentDecisionIsMarkedConfident()
    {
        var engine = new ScriptedDecisionEngine(Confident("search_web"));
        var plan = await Turn(new ScriptedChatProvider(TwoApproaches), engine)
            .PrepareAsync("what is the weather?", "files, web", CancellationToken.None);

        Assert.True(plan.Confident);
        Assert.True(plan.HasChoice);
        Assert.Equal("search_web", plan.Chosen?.Name);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task ADecisionBelowTheFloorIsNotConfidentButStillHasAChoice()
    {
        var plan = await Turn(new ScriptedChatProvider(TwoApproaches), new ScriptedDecisionEngine(Unsure("read_local")))
            .PrepareAsync("do the thing", "files, web", CancellationToken.None);

        Assert.False(plan.Confident);
        Assert.True(plan.HasChoice);          // the caller decides what to do about it
        Assert.Equal("read_local", plan.Decision!.Choice);
    }

    [Fact]
    public async Task OnePlannedApproachIsStillWeighedAgainstAskingAPerson()
    {
        var engine = new ScriptedDecisionEngine(Confident("only"));
        var provider = new ScriptedChatProvider("""{"tool":"plan","args":{"only":"The single way."}}""");

        var plan = await Turn(provider, engine).PrepareAsync("hi", "files", CancellationToken.None);

        // Before review existed this short-circuited. Now "one way forward, or a
        // person?" is a question worth asking, so it reaches the engine.
        Assert.Equal(1, engine.Calls);
        Assert.Equal(2, plan.Options.Count);
    }

    [Fact]
    public async Task AFailedPlanNeverReachesTheEngineEither()
    {
        var engine = new ScriptedDecisionEngine(Confident("x"));
        var provider = new ScriptedChatProvider("sorry, I can't plan that");

        var plan = await Turn(provider, engine).PrepareAsync("hi", "files", CancellationToken.None);

        Assert.Equal(0, engine.Calls);
        Assert.Empty(plan.Options);
        Assert.False(plan.HasChoice);
    }

    [Fact]
    public async Task TheOptionsAndTheRequestAreWhatTheEngineIsAskedAbout()
    {
        var engine = new ScriptedDecisionEngine(Confident("search_web"));

        await Turn(new ScriptedChatProvider(TwoApproaches), engine)
            .PrepareAsync("what is the weather in Seoul?", "files, web", CancellationToken.None);

        Assert.Equal("what is the weather in Seoul?", engine.LastState);
        Assert.Equal(["read_local", "search_web", SmartTurn.ReviewOption],
                     engine.LastOptions!.Select(o => o.Name));
    }

    [Fact]
    public async Task BothStagesAreAnnouncedSoNeitherLooksLikeAHang()
    {
        var turn = Turn(new ScriptedChatProvider(TwoApproaches), new ScriptedDecisionEngine(Confident("search_web")));
        var activities = new List<string>();
        turn.ActivityStarted += activities.Add;

        await turn.PrepareAsync("hi", "files, web", CancellationToken.None);

        Assert.Contains(activities, a => a.Contains("planning"));
        Assert.Contains(activities, a => a.Contains("deciding between 3"));   // two planned, plus review
    }

    [Fact]
    public async Task TheChosenApproachIsAppendedToTheRequestAsAUserLine()
    {
        var plan = await Turn(new ScriptedChatProvider(TwoApproaches), new ScriptedDecisionEngine(Confident("search_web")))
            .PrepareAsync("what is the weather?", "files, web", CancellationToken.None);

        var guided = plan.Guidance("what is the weather?");

        Assert.StartsWith("what is the weather?", guided);
        Assert.Contains("[plan] Take this approach: search_web", guided);
        Assert.Contains("Search the web.", guided);
    }

    [Fact]
    public void AnOverriddenApproachIsTheOneThatGetsUsed()
    {
        // A person picking option 2 must not be silently handed option 1.
        var chosen = new DecisionOption("read_local", "Read the files here.");

        var guided = SmartPlan.GuidanceFor("the request", chosen);

        Assert.Contains("read_local", guided);
        Assert.DoesNotContain("search_web", guided);
    }

    [Fact]
    public async Task AFailedDecisionLeavesTheRequestUntouched()
    {
        var engine = new ScriptedDecisionEngine(Decision.Failed("service down"));

        var plan = await Turn(new ScriptedChatProvider(TwoApproaches), engine)
            .PrepareAsync("the request", "files, web", CancellationToken.None);

        Assert.False(plan.HasChoice);
        Assert.Equal("the request", plan.Guidance("the request"));
    }

    // --- the option that asks a person -----------------------------------

    [Fact]
    public async Task ReviewIsAlwaysOnTheBallotAndAlwaysLast()
    {
        var engine = new ScriptedDecisionEngine(Confident("search_web"));

        await Turn(new ScriptedChatProvider(TwoApproaches), engine)
            .PrepareAsync("hi", "files, web", CancellationToken.None);

        var offered = engine.LastOptions!;
        Assert.Equal(3, offered.Count);                       // the planner's two, plus review
        Assert.Equal(SmartTurn.ReviewOption, offered[^1].Name);
        Assert.Equal(SmartTurn.ReviewDescription, offered[^1].Description);
    }

    [Fact]
    public async Task ThePlannerCannotSupplyItsOwnWordingForReview()
    {
        // Otherwise the criterion changes every turn, and a criterion that keeps
        // changing is one the engine cannot judge consistently.
        var provider = new ScriptedChatProvider(
            """{"tool":"plan","args":{"needs_review":"ask the user i guess","search_web":"Search."}}""");
        var engine = new ScriptedDecisionEngine(Confident("search_web"));

        await Turn(provider, engine).PrepareAsync("hi", "files, web", CancellationToken.None);

        var review = engine.LastOptions!.Single(o => o.Name == SmartTurn.ReviewOption);
        Assert.Equal(SmartTurn.ReviewDescription, review.Description);
        Assert.Equal(2, engine.LastOptions!.Count);           // the duplicate was dropped
    }

    [Fact]
    public async Task ChoosingReviewIsNeverConfidentHoweverSureTheEngineIs()
    {
        var certain = new Decision(true, SmartTurn.ReviewOption, 1.0,
            new Dictionary<string, double> { [SmartTurn.ReviewOption] = 1.0 }, "ok", 10);

        var plan = await Turn(new ScriptedChatProvider(TwoApproaches), new ScriptedDecisionEngine(certain))
            .PrepareAsync("delete everything", "files, web", CancellationToken.None);

        Assert.True(plan.NeedsReview);
        Assert.False(plan.Confident);     // confident means "act unasked", which is the opposite
    }

    [Fact]
    public async Task ASingleApproachStillGetsDecidedAgainstReview()
    {
        // One real option plus review is exactly the "should a person look at
        // this?" question, so it must still reach the engine.
        var provider = new ScriptedChatProvider("""{"tool":"plan","args":{"delete_all":"Remove every file."}}""");
        var engine = new ScriptedDecisionEngine(Confident("delete_all"));

        var plan = await Turn(provider, engine).PrepareAsync("clean up", "files", CancellationToken.None);

        Assert.Equal(1, engine.Calls);
        Assert.Equal(2, plan.Options.Count);
    }

    [Fact]
    public async Task NoPlanAtAllStillNeverCallsTheEngine()
    {
        var engine = new ScriptedDecisionEngine(Confident("x"));

        await Turn(new ScriptedChatProvider("not a plan"), engine)
            .PrepareAsync("hi", "files", CancellationToken.None);

        Assert.Equal(0, engine.Calls);
    }

    [Theory]
    [InlineData(0.92, 0.60, true)]
    [InlineData(0.60, 0.60, true)]     // the floor is inclusive
    [InlineData(0.59, 0.60, false)]
    [InlineData(0.92, 0.95, false)]
    public async Task TheFloorIsWhatDecidesConfident(double confidence, double floor, bool expected)
    {
        var decision = new Decision(true, "search_web", confidence,
            new Dictionary<string, double> { ["search_web"] = confidence }, "ok", 10);

        var plan = await Turn(new ScriptedChatProvider(TwoApproaches), new ScriptedDecisionEngine(decision), floor)
            .PrepareAsync("hi", "files, web", CancellationToken.None);

        Assert.Equal(expected, plan.Confident);
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
    public void AParkedTurnComesBackExactlyOnce()
    {
        var state = new SessionState(true);
        var review = new PendingReview("the request", SmartPlan.None);

        state.AwaitReview(review);

        Assert.Equal(review, state.TakeReview());
        Assert.Null(state.TakeReview());      // taken means taken
    }

    [Fact]
    public void NothingIsWaitingByDefault()
    {
        Assert.Null(new SessionState(true).TakeReview());
    }

    [Fact]
    public void ResetForgetsTheTurnsAndTheParkedReviewButNotTheMode()
    {
        var state = new SessionState(smart: true);
        state.CountTurn();
        state.AwaitReview(new PendingReview("x", SmartPlan.None));

        state.Reset();

        var snapshot = state.Read();
        Assert.True(snapshot.Smart);          // the mode is the operator's choice, not turn state
        Assert.Equal(0, snapshot.Turns);
        Assert.Null(snapshot.Pending);
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
