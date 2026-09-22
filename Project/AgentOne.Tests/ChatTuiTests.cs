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
    public void PageKeysScrollAndCtrlEndFollowsAgain()
    {
        var model = Type(new ChatTuiModel(false, true), "abc");
        Assert.Equal(ChatEffect.ScrollUp, model.HandleKey(Key(ConsoleKey.PageUp)));
        Assert.Equal(ChatEffect.ScrollDown, model.HandleKey(Key(ConsoleKey.PageDown)));
        Assert.Equal(ChatEffect.ScrollToBottom, model.HandleKey(Key(ConsoleKey.End, ctrl: true)));

        // Plain End still belongs to the line editor.
        model.HandleKey(Key(ConsoleKey.Home));
        Assert.Equal(ChatEffect.None, model.HandleKey(Key(ConsoleKey.End)));
        Assert.Equal(3, model.Cursor);
    }

    [Fact]
    public void ThePageReportsWhereTheTranscriptIsAndOnlyChangesCount()
    {
        var model = new ChatTuiModel(false, true);

        Assert.True(model.SetScrolled(true, 20));
        Assert.True(model.ScrolledUp);
        Assert.Equal(20, model.ScrollOffset);
        Assert.False(model.SetScrolled(true, 20));     // nothing moved: no repaint needed
        Assert.True(model.SetScrolled(false, 0));
        Assert.False(model.ScrolledUp);
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
/// it: one line in, a run out. Scripted provider and engine, so a
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

    private const string Final = """{"tool":"final","args":{"text":"hi there"}}""";

    private static Decision Choose(string choice, double confidence) =>
        new(true, choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "ok", 10);

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine engine, bool smart,
        bool available = true, AgentOne.Llm.IChatProvider? reasoning = null)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", smart ? "on" : "off", out _);
        config.TrySet("saveSessions", "false", out _);
        if (reasoning is not null) config.TrySet("reasoningModel", "big-model", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine, available, reasoning);
    }

    [Fact]
    public async Task ABasicTurnRunsTheLoopAndReturnsTheRun()
    {
        using var session = Session(new ScriptedChatProvider(Final), new ScriptedDecisionEngine(Choose("x", 1)), smart: false);

        var run = await session.SubmitAsync("hello", CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal("hi there", run!.Text);
    }

    [Fact]
    public async Task AnEmptyLineIsIgnored()
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
    public async Task AShortRequestSkipsTheEngineEvenInSmartMode()
    {
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.SearchWeb, 0.9));
        using var session = Session(new ScriptedChatProvider(Final), engine, smart: true);

        var run = await session.SubmitAsync("hi!", CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task AConfidentRouteSteersAndRulesOutTheOtherFamily()
    {
        // Routed to files, the model tries the web anyway: the call is refused,
        // not run, and the model is told so — then it answers.
        var provider = new ScriptedChatProvider(
            """{"tool":"web_search","args":{"query":"x"}}""",
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"it says hello"}}""");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.ReadWorkspace, 0.9));
        using var session = Session(provider, engine, smart: true);

        var notes = new List<SmartNote>();
        session.Decided += notes.Add;

        var run = await session.SubmitAsync("what does the readme say?", CancellationToken.None);

        Assert.Equal("it says hello", run!.Text);
        Assert.Contains(provider.Calls[0], m => m.Role == "user" && m.Content.Contains("[route: files]"));
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("not available on this turn"));
        Assert.Contains(provider.Calls[2], m => m.Content.StartsWith("[tool:read_file]"));
        Assert.Equal("route", notes[0].Kind);
        Assert.Contains("files", notes[0].Verdict);
        Assert.Equal(1, engine.Calls);                       // no reasoning model: no escalation question
    }

    [Fact]
    public async Task AnUnsureRouteLeavesEveryToolAvailable()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"hello"}}""");
        using var session = Session(provider, new ScriptedDecisionEngine(Choose(SmartRouter.SearchWeb, 0.3)), smart: true);

        var run = await session.SubmitAsync("what does the readme say?", CancellationToken.None);

        Assert.Equal("hello", run!.Text);
        Assert.DoesNotContain(provider.Calls[0], m => m.Content.Contains("[route:"));
        Assert.Contains(provider.Calls[1], m => m.Content.StartsWith("[tool:read_file]"));
    }

    [Fact]
    public async Task EscalationHandsTheMaterialToTheStrongModelAndTheEverydayModelAnswersFromIt()
    {
        var basic = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"a shallow draft"}}""",
            """{"tool":"final","args":{"text":"the refined answer"}}""");
        var strong = new ScriptedChatProvider("deep thoughts about hello");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.ReadWorkspace, 0.9),
            Choose(SmartRouter.EscalateOption, 0.85));
        using var session = Session(basic, engine, smart: true, reasoning: strong);

        var notes = new List<SmartNote>();
        var steps = new List<AgentStep>();
        session.Decided += notes.Add;
        session.StepCompleted += steps.Add;

        var run = await session.SubmitAsync("explain what the readme implies", CancellationToken.None);

        Assert.Equal("the refined answer", run!.Text);

        // The engine saw the tool result and the draft when judging.
        Assert.Contains("[tool:read_file]", engine.States[1]);
        Assert.Contains("a shallow draft", engine.States[1]);
        Assert.Contains("big-model", engine.States[1]);

        // The strong model got the same material, and its answer went back as a tagged line.
        Assert.Single(strong.Calls);
        Assert.Contains(strong.Calls[0], m => m.Content.Contains("[tool:read_file]") && m.Content.Contains("a shallow draft"));
        Assert.Contains(basic.Calls[2], m => m.Content.StartsWith("[reasoning:big-model] deep thoughts about hello"));

        Assert.Contains(steps, s => s.Tool == ReasoningSubtask.Tag && s.Ok);
        Assert.Equal(["route", "escalation"], notes.Select(n => n.Kind));
        Assert.Contains("escalating to big-model", notes[1].Verdict);
    }

    [Fact]
    public async Task KeepingTheDraftReturnsItUntouched()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"good enough"}}""");
        var strong = new ScriptedChatProvider("never asked");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly, 0.9),
            Choose(SmartRouter.KeepDraft, 0.8));
        using var session = Session(basic, engine, smart: true, reasoning: strong);

        var run = await session.SubmitAsync("a simple question here", CancellationToken.None);

        Assert.Equal("good enough", run!.Text);
        Assert.Empty(strong.Calls);
        Assert.Single(basic.Calls);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task AStrongModelThatFailsLeavesTheDraftStanding()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"the draft"}}""");
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.AnswerDirectly, 0.9),
            Choose(SmartRouter.EscalateOption, 0.9));
        using var session = Session(basic, engine, smart: true, reasoning: new FailingChatProvider("cannot reach the strong model"));

        var steps = new List<AgentStep>();
        session.StepCompleted += steps.Add;

        var run = await session.SubmitAsync("a hard question here", CancellationToken.None);

        Assert.Equal("the draft", run!.Text);
        Assert.Contains(steps, s => s.Tool == ReasoningSubtask.Tag && !s.Ok && s.Detail.Contains("keeping the draft"));
    }

    [Fact]
    public async Task ADraftThatDidNotSucceedIsNotJudged()
    {
        var basic = new ScriptedChatProvider("not json, not long enough");
        var engine = new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly, 0.9), Choose(SmartRouter.EscalateOption, 0.9));
        using var session = Session(basic, engine, smart: true, reasoning: new ScriptedChatProvider("x"));

        var run = await session.SubmitAsync("a question that fails", CancellationToken.None);

        Assert.False(run!.Succeeded);
        Assert.Equal(1, engine.Calls);
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

/// <summary>
/// The transcript's scrolling rests on Termina's buffer behaving in three
/// specific ways; the first chat window got two of them wrong (see
/// ChatTuiPage). Pin them here so an upgrade that changes them shows up.
/// </summary>
public class TranscriptScrollTests
{
    private const int Width = 80;

    private static Termina.Components.Streaming.PersistedStreamBuffer Filled(int lines)
    {
        var buffer = new Termina.Components.Streaming.PersistedStreamBuffer { AutoScroll = true };
        for (var i = 0; i < lines; i++) buffer.AppendLine($"line {i}");
        return buffer;
    }

    [Fact]
    public void PageUpMovesAwayFromTheLiveEnd()
    {
        var buffer = Filled(100);

        buffer.ScrollUp(20, Width);

        Assert.True(buffer.IsScrolledUp);
        Assert.Equal(20, buffer.ScrollOffset);
    }

    [Fact]
    public void NewTextDoesNotPullAScrolledReaderBackDown()
    {
        var buffer = Filled(100);
        buffer.ScrollUp(20, Width);

        buffer.AppendLine("more");
        buffer.Append("and a fragment");

        Assert.True(buffer.IsScrolledUp);
    }

    [Fact]
    public void ScrollToBottomFollowsAgain()
    {
        var buffer = Filled(100);
        buffer.ScrollUp(20, Width);

        buffer.ScrollToBottom();
        buffer.AppendLine("live");

        Assert.False(buffer.IsScrolledUp);
        Assert.Equal(0, buffer.ScrollOffset);
    }

    [Fact]
    public void ScrollingDownPastTheEndStopsAtTheEnd()
    {
        var buffer = Filled(100);
        buffer.ScrollUp(5, Width);

        buffer.ScrollDown(50);

        Assert.False(buffer.IsScrolledUp);
    }
}

/// <summary>Folding streamed text to the window width before the buffer sees it.</summary>
public class SoftWrapTests
{
    [Fact]
    public void BreaksBeforeTheWordThatWouldOverflow()
    {
        var wrap = new SoftWrap();

        var folded = wrap.Fold("aaaa bbbb cccc dddd", 10);

        Assert.Equal("aaaa bbbb\ncccc dddd", folded);
        Assert.Equal(9, wrap.Column);
    }

    [Fact]
    public void TheColumnCarriesAcrossFragments()
    {
        var wrap = new SoftWrap();

        var first = wrap.Fold("aaaa bbbb", 10);
        var second = wrap.Fold(" cccc", 10);

        Assert.Equal("aaaa bbbb", first);
        Assert.Equal("\ncccc", second);     // the space at the fold is dropped
        Assert.Equal(4, wrap.Column);
    }

    [Fact]
    public void ANewlineInTheTextResetsTheColumn()
    {
        var wrap = new SoftWrap();

        wrap.Fold("aaaa\nbb", 10);

        Assert.Equal(2, wrap.Column);
    }

    [Fact]
    public void NewLineStartsTheNextFragmentAtColumnZero()
    {
        var wrap = new SoftWrap();
        wrap.Fold("aaaaaaaa", 10);

        wrap.NewLine();

        Assert.Equal(0, wrap.Column);
        Assert.Equal("bbbbbbbb", wrap.Fold("bbbbbbbb", 10));
    }

    [Fact]
    public void WideCharactersCountTwoColumns()
    {
        var wrap = new SoftWrap();

        // Five Hangul syllables are ten columns: the sixth must fold.
        var folded = wrap.Fold("가나다라마 바사", 11);

        Assert.Equal("가나다라마\n바사", folded);
    }

    [Fact]
    public void AWordWiderThanTheLineIsBrokenByColumns()
    {
        var wrap = new SoftWrap();

        var folded = wrap.Fold("https://example.com/a/very/long/path", 10);

        Assert.All(folded.Split('\n'), part => Assert.True(part.Length <= 10));
        Assert.Equal("https://example.com/a/very/long/path", folded.Replace("\n", ""));
    }

    [Fact]
    public void ALongAnswerNeverProducesALineWiderThanTheWindow()
    {
        var wrap = new SoftWrap();
        var text = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"word{i}"));

        var folded = wrap.Fold(text, 79);

        Assert.All(folded.Split('\n'), line => Assert.True(line.Length <= 79));
        Assert.Equal(text.Replace(" ", ""), folded.Replace("\n", "").Replace(" ", ""));
    }

    [Fact]
    public void TheBufferTreatsAnEmbeddedNewlineAsALineBreak()
    {
        var buffer = new Termina.Components.Streaming.PersistedStreamBuffer();

        buffer.Append("aaaa\nbbbb");

        // The fold relies on this: a newline inside Append() must start a new buffer line.
        Assert.Equal(2, buffer.GetWrappedLineCount(80));
    }
}

/// <summary>The input line shows a window around the cursor once the text is wider than it.</summary>
public class InputViewportTests
{
    private static int Columns(string s) => Termina.Terminal.DisplayWidth.GetColumnCount(s);

    [Fact]
    public void ShortTextIsShownWholeWithTheCursorInPlace()
    {
        Assert.Equal("ab▌cd", InputViewport.Render("abcd", 2, 40));
    }

    [Fact]
    public void TypingAtTheEndOfALongLineKeepsTheEndVisible()
    {
        var text = string.Concat(Enumerable.Repeat("0123456789", 10));

        var shown = InputViewport.Render(text, text.Length, 30);

        Assert.StartsWith("…", shown);
        Assert.EndsWith("89▌", shown);
        Assert.True(Columns(shown) <= 30, shown);
    }

    [Fact]
    public void ACursorAtTheStartShowsTheStartAndCutsTheEnd()
    {
        var text = string.Concat(Enumerable.Repeat("0123456789", 10));

        var shown = InputViewport.Render(text, 0, 30);

        Assert.StartsWith("▌0123", shown);
        Assert.EndsWith("…", shown);
        Assert.True(Columns(shown) <= 30, shown);
    }

    [Fact]
    public void ACursorInTheMiddleShowsBothSides()
    {
        var text = string.Concat(Enumerable.Repeat("0123456789", 10));

        var shown = InputViewport.Render(text, 50, 30);

        Assert.StartsWith("…", shown);
        Assert.EndsWith("…", shown);
        Assert.Contains("9▌0", shown);
        Assert.True(Columns(shown) <= 30, shown);
    }

    [Fact]
    public void WideCharactersAreMeasuredInColumnsNotChars()
    {
        var text = string.Concat(Enumerable.Repeat("가나다라마", 10));   // 100 columns

        var shown = InputViewport.Render(text, text.Length, 30);

        Assert.True(Columns(shown) <= 30, shown);
        Assert.EndsWith("마▌", shown);
    }
}
