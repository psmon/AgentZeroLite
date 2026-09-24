using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>The choice at the top of a design, and the design's on-screen summary.</summary>
public class DesignDecisionTests
{
    private const string WithChoice = """
        DECISION NEEDED:
        1. EF Core + SQLite (recommended)
        2. Dapper + PostgreSQL
        3. In-memory only

        ## Files
        1. src/Api/Program.cs — host
        2. src/Api/Data/Db.cs — context
        """;

    [Fact]
    public void TheOptionsAreLiftedOutAndTheRecommendationMarked()
    {
        var decision = ReasoningSubtask.ExtractDecision(WithChoice, out var rest);

        Assert.NotNull(decision);
        Assert.Equal(["EF Core + SQLite", "Dapper + PostgreSQL", "In-memory only"], decision!.Options);
        Assert.Equal(0, decision.Recommended);
        Assert.StartsWith("## Files", rest);
        Assert.DoesNotContain("DECISION", rest);
    }

    [Fact]
    public void ADesignWithoutTheMarkerHasNothingToDecide()
    {
        Assert.Null(ReasoningSubtask.ExtractDecision("## Files\n1. a\n2. b", out var rest));
        Assert.Equal("## Files\n1. a\n2. b", rest);
    }

    [Fact]
    public void ASingleOptionIsNotAChoice()
    {
        Assert.Null(ReasoningSubtask.ExtractDecision("DECISION NEEDED:\n1. only this\n\nrest", out _));
    }

    [Fact]
    public void AnAnswerIsANumberTheRecommendationOrItsOwnWords()
    {
        var choice = new ChoiceRequest("q", ["EF Core", "Dapper"], 1);

        Assert.Equal("EF Core", choice.Resolve("1"));
        Assert.Equal("Dapper", choice.Resolve(""));
        Assert.Equal("Dapper", choice.Resolve("2"));
        Assert.Equal("use MongoDB", choice.Resolve("use MongoDB"));
        Assert.Equal("Dapper", choice.Resolve("9"));                    // out of range: the recommendation
    }

    [Fact]
    public void TheSummaryIsTheFirstLinesWithACountOfTheRest()
    {
        var design = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"{i}. step {i}"));

        var summary = ReasoningSubtask.Summary(design, maxLines: 5);

        Assert.Equal(6, summary.Count);
        Assert.Equal("1. step 1", summary[0]);
        Assert.Contains("15 more lines", summary[5]);
    }

    [Fact]
    public void TheDesignPromptAsksForTheMarkerAndTheFeedBackCarriesTheChoice()
    {
        Assert.Contains(ReasoningSubtask.DecisionMarker, ReasoningSubtask.DesignSystemPrompt("small", "bash"));
        Assert.Contains("The user decided: Dapper", ReasoningSubtask.DesignFeedBack("big", "the design", "Dapper"));
        Assert.DoesNotContain("The user decided", ReasoningSubtask.DesignFeedBack("big", "the design"));
    }
}

/// <summary>The session asks the person to choose, shows the design, and sums up a turn that ran out of steps.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class DesignAndWrapUpSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public DesignAndWrapUpSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-design-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 10);

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine engine, ScriptedChatProvider? reasoning = null, int maxSteps = 50)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        config.TrySet("maxSteps", maxSteps.ToString(), out _);
        if (reasoning is not null) config.TrySet("reasoningModel", "big-model", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine, true, reasoning) { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
    }

    [Fact]
    public async Task ADesignWithAChoiceAsksThePersonAndBuildsWhatTheyPicked()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"built with dapper"}}""");
        var strong = new ScriptedChatProvider("DECISION NEEDED:\n1. EF Core (recommended)\n2. Dapper\n\n## Files\n1. src/a.cs");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.WorkInWorkspace, 0.9), Choose(SmartRouter.NeedsDesign, 0.9));
        using var session = Session(basic, engine, strong);

        ChoiceRequest? asked = null;
        IReadOnlyList<string>? shown = null;
        var notes = new List<string>();
        session.Chooser = (choice, _) => { asked = choice; return Task.FromResult("2"); };
        session.DesignMade += lines => shown = lines;
        session.Noted += notes.Add;

        var run = await session.SubmitAsync("scaffold a board api with storage", CancellationToken.None);

        Assert.Equal("built with dapper", run!.Text);
        Assert.Equal(["EF Core", "Dapper"], asked!.Options);
        Assert.Equal(0, asked.Recommended);
        Assert.Contains(shown!, l => l.Contains("## Files"));
        Assert.DoesNotContain(shown!, l => l.Contains("DECISION"));
        Assert.Contains("decided: Dapper", notes);
        Assert.Contains(basic.Calls[0], m => m.Content.Contains("The user decided: Dapper"));
    }

    [Fact]
    public async Task WithNoChooserTheRecommendationStands()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"ok"}}""");
        var strong = new ScriptedChatProvider("DECISION NEEDED:\n1. A\n2. B (recommended)\n\nplan");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.WorkInWorkspace, 0.9), Choose(SmartRouter.NeedsDesign, 0.9));
        using var session = Session(basic, engine, strong);

        await session.SubmitAsync("scaffold something large here", CancellationToken.None);

        Assert.Contains(basic.Calls[0], m => m.Content.Contains("The user decided: B"));
    }

    [Fact]
    public async Task ATurnThatRunsOutOfStepsEndsWithASummaryAndKeepsItsStopReason()
    {
        // Two steps of budget, both spent on tool calls; the wrap-up call then
        // gets the budget again and answers without tools.
        var basic = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"list_files","args":{"path":"."}}""",
            """{"tool":"final","args":{"text":"Done so far: read README, listed files. Next: 1. write Program.cs"}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.WorkInWorkspace, 0.9), Choose(SmartRouter.SmallTask, 0.9));
        using var session = Session(basic, engine, maxSteps: 2);

        var notes = new List<string>();
        session.Noted += notes.Add;

        var run = await session.SubmitAsync("build the whole thing now", CancellationToken.None);

        Assert.Equal(StopReason.MaxSteps, run!.Reason);
        Assert.StartsWith("Done so far", run.Text);
        Assert.Contains(notes, n => n.Contains("stopped early (MaxSteps)"));
        Assert.Contains(basic.Calls[2], m => m.Content.Contains("[wrap-up]"));
    }

    [Fact]
    public async Task AStopWithNoToolWorkIsNotSummarized()
    {
        var basic = new ScriptedChatProvider("not json", "still not json", "nope");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly, 0.9));
        using var session = Session(basic, engine, maxSteps: 3);

        var run = await session.SubmitAsync("a question that fails", CancellationToken.None);

        Assert.False(run!.Succeeded);
        Assert.DoesNotContain(basic.Calls.SelectMany(c => c), m => m.Content.Contains("[wrap-up]"));
    }
}
