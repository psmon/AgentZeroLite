using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// The chat window's line editor and bars. Takes real ConsoleKeyInfo values, so
/// the key map itself is what is tested.
/// </summary>
public class ChatTuiModelTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool ctrl = false) =>
        new('\0', key, shift, false, ctrl);

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ChatTuiModel Type(ChatTuiModel model, string text)
    {
        foreach (var c in text) model.HandleKey(Ch(c));
        return model;
    }

    [Fact]
    public void TypingBuildsTheLineAndMovesTheCursor()
    {
        var model = Type(new ChatTuiModel(false, true), "안녕 hi");

        Assert.Equal("안녕 hi", model.Input);
        Assert.Equal(5, model.Cursor);
    }

    [Fact]
    public void EnterSubmitsANonEmptyLineAndHandsItOver()
    {
        var model = Type(new ChatTuiModel(false, true), "hello");

        Assert.Equal(ChatEffect.Submit, model.HandleKey(Key(ConsoleKey.Enter)));
        Assert.Equal("hello", model.TakeInput());
        Assert.Equal("", model.Input);
        Assert.Equal(0, model.Cursor);
    }

    [Fact]
    public void EnterOnAnEmptyLineDoesNothing()
    {
        Assert.Equal(ChatEffect.None, new ChatTuiModel(false, true).HandleKey(Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void AnEmptyEnterIsAllowedWhenAPersonIsBeingWaitedFor()
    {
        // Empty means "accept the engine's pick" on an unsure pause.
        var model = new ChatTuiModel(true, true);
        model.SetAwaitingPerson(true);

        Assert.Equal(ChatEffect.Submit, model.HandleKey(Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void EnterIsRefusedWhileATurnIsRunning()
    {
        var model = Type(new ChatTuiModel(false, true), "next question");
        model.SetBusy(true);

        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.Enter)));
        Assert.Contains("still working", model.Status);
        Assert.Equal("next question", model.Input);         // kept, not lost
    }

    [Fact]
    public void ShiftTabTogglesAndPlainTabDoesNothing()
    {
        var model = new ChatTuiModel(false, true);

        Assert.Equal(ChatEffect.ToggleMode, model.HandleKey(Key(ConsoleKey.Tab, shift: true)));
        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.Tab)));
    }

    [Fact]
    public void EditingKeysWorkWithinTheLine()
    {
        var model = Type(new ChatTuiModel(false, true), "abcd");

        model.HandleKey(Key(ConsoleKey.LeftArrow));
        model.HandleKey(Key(ConsoleKey.LeftArrow));
        model.HandleKey(Key(ConsoleKey.Backspace));           // removes 'b'
        Assert.Equal("acd", model.Input);

        model.HandleKey(Key(ConsoleKey.Delete));              // removes 'c'
        Assert.Equal("ad", model.Input);

        model.HandleKey(Key(ConsoleKey.Home));
        Type(model, "x");
        model.HandleKey(Key(ConsoleKey.End));
        Type(model, "z");
        Assert.Equal("xadz", model.Input);
    }

    [Fact]
    public void EscapeClearsALineAndOnlyThenArmsQuit()
    {
        var model = Type(new ChatTuiModel(false, true), "oops");

        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.Escape)));
        Assert.Equal("", model.Input);
        Assert.False(model.QuitArmed);

        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.Escape)));
        Assert.True(model.QuitArmed);

        Assert.Equal(ChatEffect.Quit, model.HandleKey(Key(ConsoleKey.Escape)));
    }

    [Fact]
    public void TypingDisarmsAPendingQuit()
    {
        var model = new ChatTuiModel(false, true);
        model.HandleKey(Key(ConsoleKey.Escape));
        Assert.True(model.QuitArmed);

        Type(model, "a");
        Assert.False(model.QuitArmed);
    }

    [Fact]
    public void CtrlDArmsThenQuitsAndCtrlCQuitsAtOnce()
    {
        var model = new ChatTuiModel(false, true);

        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.D, ctrl: true)));
        Assert.Equal(ChatEffect.Quit, model.HandleKey(Key(ConsoleKey.D, ctrl: true)));

        Assert.Equal(ChatEffect.Quit, new ChatTuiModel(false, true).HandleKey(Key(ConsoleKey.C, ctrl: true)));
    }

    [Fact]
    public void PageKeysScroll()
    {
        var model = new ChatTuiModel(false, true);
        Assert.Equal(ChatEffect.ScrollUp, model.HandleKey(Key(ConsoleKey.PageUp)));
        Assert.Equal(ChatEffect.ScrollDown, model.HandleKey(Key(ConsoleKey.PageDown)));
    }

    [Fact]
    public void ControlCharactersNeverEnterTheLine()
    {
        var model = new ChatTuiModel(false, true);
        model.HandleKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        model.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.F5, false, false, false));

        Assert.Equal("", model.Input);
    }

    [Fact]
    public void TheHelpLineSaysWhenSmartIsUnavailable()
    {
        Assert.Contains("smart unavailable", new ChatTuiModel(false, false).Status);
        Assert.Contains("Shift+Tab", new ChatTuiModel(false, true).Status);
    }
}

/// <summary>
/// The conversation itself, driven the way both the window and the REPL drive
/// it: one line in, a run or a pause out. Scripted provider and engine, so a
/// pause-and-resume can be walked deterministically.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class ChatSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public ChatSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-chat-" + Guid.NewGuid().ToString("N")[..8]);
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

    private const string TwoApproaches =
        """{"tool":"plan","args":{"read_local":"Read the files here.","search_web":"Search the web."}}""";

    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 10);

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine engine, bool smart, bool available = true)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", smart ? "on" : "off", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine, available);
    }

    [Fact]
    public async Task ABasicTurnRunsTheLoopAndReturnsTheRun()
    {
        using var session = Session(
            new ScriptedChatProvider("""{"tool":"final","args":{"text":"hi there"}}"""),
            new ScriptedDecisionEngine(Choose("x", 1)), smart: false);

        var run = await session.SubmitAsync("hello", CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal("hi there", run!.Text);
        Assert.Null(session.Pending);
    }

    [Fact]
    public async Task AnEmptyLineWithNothingPendingIsIgnored()
    {
        using var session = Session(new ScriptedChatProvider("x"), new ScriptedDecisionEngine(Choose("x", 1)), false);
        Assert.Null(await session.SubmitAsync("   ", CancellationToken.None));
    }

    [Fact]
    public void SmartStartsOffWhenNoKeyEvenIfConfigSaysOn()
    {
        using var session = Session(new ScriptedChatProvider("x"), new ScriptedDecisionEngine(Choose("x", 1)),
            smart: true, available: false);

        Assert.False(session.Smart);
        Assert.False(session.TryToggleSmart(out var why));
        Assert.Contains("no TypeSafe key", why);
    }

    [Fact]
    public void TheToggleFlipsAndExplainsItself()
    {
        using var session = Session(new ScriptedChatProvider("x"), new ScriptedDecisionEngine(Choose("x", 1)), false);

        Assert.True(session.TryToggleSmart(out var on));
        Assert.True(session.Smart);
        Assert.Contains("smart mode on", on);

        Assert.True(session.TryToggleSmart(out var off));
        Assert.False(session.Smart);
        Assert.Contains("basic", off);
    }

    [Fact]
    public async Task AConfidentPlanSteersTheTurn()
    {
        var provider = new ScriptedChatProvider(TwoApproaches, """{"tool":"final","args":{"text":"done"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose("search_web", 0.9)), smart: true);

        SmartPlan? made = null;
        session.PlanMade += p => made = p;

        var run = await session.SubmitAsync("what is new?", CancellationToken.None);

        Assert.NotNull(run);
        Assert.True(made!.Confident);
        // The loop's prompt carried the guidance line.
        Assert.Contains(provider.Calls[1], m => m.Role == "user" && m.Content.Contains("[plan] Take this approach: search_web"));
    }

    [Fact]
    public async Task NeedingAPersonPausesAndTheNextLineResumesAsApproval()
    {
        var provider = new ScriptedChatProvider(TwoApproaches, """{"tool":"final","args":{"text":"listed"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose(SmartTurn.ReviewOption, 0.8)), smart: true);

        var first = await session.SubmitAsync("clean everything up", CancellationToken.None);

        Assert.Null(first);
        Assert.NotNull(session.Pending);
        Assert.Equal(PauseReason.NeedsPerson, session.Pending!.Reason);

        var second = await session.SubmitAsync("only list, never delete", CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal("listed", second!.Text);
        Assert.Null(session.Pending);
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("[approved] only list, never delete"));
    }

    [Fact]
    public async Task SilenceIsNotApproval()
    {
        var provider = new ScriptedChatProvider(TwoApproaches, """{"tool":"final","args":{"text":"x"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose(SmartTurn.ReviewOption, 0.8)), smart: true);
        await session.SubmitAsync("risky thing", CancellationToken.None);

        var again = await session.SubmitAsync("", CancellationToken.None);

        Assert.Null(again);
        Assert.NotNull(session.Pending);            // still parked
        Assert.Single(provider.Calls);              // nothing was run
    }

    [Fact]
    public async Task ANumberPicksAnApproachByHand()
    {
        var provider = new ScriptedChatProvider(TwoApproaches, """{"tool":"final","args":{"text":"read"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose(SmartTurn.ReviewOption, 0.8)), smart: true);
        await session.SubmitAsync("do something", CancellationToken.None);

        var run = await session.SubmitAsync("1", CancellationToken.None);       // read_local

        Assert.NotNull(run);
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("[plan] Take this approach: read_local"));
    }

    [Fact]
    public async Task AnUnsureDecisionPausesAndAnEmptyLineAcceptsThePick()
    {
        var provider = new ScriptedChatProvider(TwoApproaches, """{"tool":"final","args":{"text":"ok"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose("read_local", 0.4)), smart: true);

        var first = await session.SubmitAsync("hmm", CancellationToken.None);
        Assert.Null(first);
        Assert.Equal(PauseReason.Unsure, session.Pending!.Reason);

        var second = await session.SubmitAsync("", CancellationToken.None);

        Assert.NotNull(second);
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("[plan] Take this approach: read_local"));
    }

    [Fact]
    public async Task ResetClearsAParkedTurn()
    {
        var provider = new ScriptedChatProvider(TwoApproaches);
        using var session = Session(provider, new ScriptedDecisionEngine(Choose(SmartTurn.ReviewOption, 0.8)), smart: true);
        await session.SubmitAsync("risky", CancellationToken.None);
        Assert.NotNull(session.Pending);

        session.Reset();

        Assert.Null(session.Pending);
    }

    [Fact]
    public async Task EventsFireForTheRenderers()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"it says hello"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose("x", 1)), false);

        var activities = new List<string>();
        var steps = new List<AgentStep>();
        session.ActivityStarted += activities.Add;
        session.StepCompleted += steps.Add;

        await session.SubmitAsync("what does it say?", CancellationToken.None);

        Assert.Contains(activities, a => a.Contains("reading README.md"));
        Assert.Contains(steps, s => s.Tool == "read_file" && s.Ok);
    }
}
