using AgentOne.Actors;
using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// Esc holds a running turn at its next step; the line typed next means
/// resume, stop or refine — judged by the engine when there is one, by a
/// word list otherwise — and a refinement goes in front of the model.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public sealed class PauseTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;

    public PauseTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "agent-one-pause-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, null);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 10);

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine? engine = null)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "off", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine ?? new ScriptedDecisionEngine(), engine is not null)
            { NamesTasks = false, UsesGraph = false };
    }

    /// <summary>Two steps, 300 ms each: paused during the first, the loop waits before the second.</summary>
    private static ScriptedChatProvider TwoSteps() => new(
        """{"tool":"list_files","args":{"path":"."}}""",
        """{"tool":"final","args":{"text":"done"}}""") { Delay = TimeSpan.FromMilliseconds(300) };

    private static async Task<Task<AgentRun?>> StartAndPauseAsync(ChatSession session, string request = "look around")
    {
        var turn = session.SubmitAsync(request, CancellationToken.None);
        await Task.Delay(100);
        session.Pause();
        await Task.Delay(500);                                  // step 1 ends, step 2 is held
        Assert.True(session.Paused);
        return turn;
    }

    [Fact]
    public async Task PauseHoldsTheLoopBetweenStepsAndAnEmptyLineResumesIt()
    {
        var provider = TwoSteps();
        using var session = Session(provider);
        var notes = new List<string>();
        session.Noted += notes.Add;

        var turn = await StartAndPauseAsync(session);
        Assert.Equal(1, provider.CallCount);                    // the second model call has not happened
        Assert.Contains(notes, n => n.Contains("pausing"));

        var outcome = await session.ResumeAsync("", CancellationToken.None);
        Assert.Equal(PauseVerdict.Resume, outcome.Verdict);

        var run = await turn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run!.Succeeded);
        Assert.Equal(2, provider.CallCount);
        Assert.False(session.Paused);
    }

    [Fact]
    public async Task ARefinementGoesInFrontOfTheModelBeforeItThinksAgain()
    {
        var provider = TwoSteps();
        using var session = Session(provider);

        var turn = await StartAndPauseAsync(session);
        var outcome = await session.ResumeAsync("also count the lines", CancellationToken.None);
        Assert.Equal(PauseVerdict.Refine, outcome.Verdict);
        Assert.StartsWith("refining", outcome.Message);

        var run = await turn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run!.Succeeded);
        var last = provider.Calls[1].Last(m => m.Role == "user");
        Assert.Contains("[the user, mid-turn] also count the lines", last.Content);
    }

    [Fact]
    public async Task StopAbandonsTheTurnAsCancelled()
    {
        var provider = TwoSteps();
        using var session = Session(provider);

        var turn = await StartAndPauseAsync(session);
        var outcome = await session.ResumeAsync("그만", CancellationToken.None);
        Assert.Equal(PauseVerdict.Stop, outcome.Verdict);

        var run = await turn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopReason.Cancelled, run!.Reason);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task WithAnEngineTheLineIsJudgedNotMatched()
    {
        var provider = TwoSteps();
        // "ok" would be a resume by the word list; the engine reads it as a refinement here.
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.RefineOption, 0.5));
        using var session = Session(provider, engine);

        var turn = await StartAndPauseAsync(session);
        var outcome = await session.ResumeAsync("ok but in json", CancellationToken.None);

        Assert.Equal(PauseVerdict.Refine, outcome.Verdict);
        Assert.NotNull(outcome.Decision);
        Assert.Contains(SmartRouter.PauseQuestion, engine.Questions);
        Assert.Contains("look around", engine.LastState);
        Assert.Contains("list_files", engine.LastState);       // what was done so far
        await turn.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ResumingWhenNothingIsPausedIsANoOp()
    {
        using var session = Session(new ScriptedChatProvider("""{"tool":"final","args":{"text":"hi"}}"""));
        session.Pause();                                         // nothing runs: ignored
        Assert.False(session.Paused);
        var outcome = await session.ResumeAsync("continue", CancellationToken.None);
        Assert.Equal("nothing is paused", outcome.Message);
    }

    [Theory]
    [InlineData("", PauseVerdict.Resume)]
    [InlineData("continue", PauseVerdict.Resume)]
    [InlineData("계속", PauseVerdict.Resume)]
    [InlineData("stop", PauseVerdict.Stop)]
    [InlineData("중단", PauseVerdict.Stop)]
    [InlineData("never mind", PauseVerdict.Stop)]
    [InlineData("use tabs instead of spaces", PauseVerdict.Refine)]
    public void TheWordListReadsTheObviousLines(string line, PauseVerdict expected) =>
        Assert.Equal(expected, ChatSession.JudgePauseLine(line));

    [Fact]
    public void EscWhileBusyPausesAndEnterWhilePausedSubmits()
    {
        var model = new ChatTuiModel(false, false);
        static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

        model.SetBusy(true, "… thinking");
        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.Enter)));      // still working
        Assert.Equal(ChatEffect.Pause, model.HandleKey(Key(ConsoleKey.Escape)));

        model.SetPaused(true);
        Assert.Contains("paused", model.Status);
        Assert.Equal(ChatEffect.Submit, model.HandleKey(Key(ConsoleKey.Enter)));    // an empty line: go on
        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.Escape)));     // Esc again: clears the (empty) line, arms quit at most

        model.SetBusy(false, "done");
        Assert.False(model.Paused);
    }

    [Fact]
    public async Task ThePauseWorksThroughTheActors()
    {
        var provider = TwoSteps();
        using var gateway = AgentGateway.Start(new AgentLoopBindings(() => Session(provider)));

        var turn = gateway.SubmitAsync("look around", CancellationToken.None);
        await Task.Delay(150);
        gateway.Pause();
        await Task.Delay(500);
        Assert.True(gateway.Paused);
        Assert.Equal(1, provider.CallCount);

        var outcome = await gateway.ResumeAsync("also count the lines", CancellationToken.None);
        Assert.Equal(PauseVerdict.Refine, outcome.Verdict);
        Assert.False(gateway.Paused);

        var run = await turn.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run!.Succeeded);
        Assert.Contains(provider.Calls[1], m => m.Role == "user" && m.Content.Contains("mid-turn"));
    }
}
