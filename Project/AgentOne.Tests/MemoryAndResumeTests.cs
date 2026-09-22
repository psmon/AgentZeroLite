using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>The per-workspace folder: memory that rolls, sessions that list.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class WorkspaceStoreTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public WorkspaceStoreTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-wsstore-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "proj", "api");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheFolderIsNamedAfterTheRootAndHashedOnItsFullPath()
    {
        var dir = AppPaths.WorkspaceDir(_root);
        var other = AppPaths.WorkspaceDir(Path.Combine(_home, "elsewhere", "api"));

        Assert.StartsWith(Path.Combine(AppPaths.WorkspacesDir, "api-"), dir);
        Assert.NotEqual(dir, other);                                   // two folders called "api" do not share a memory
        Assert.Equal(dir, AppPaths.WorkspaceDir(_root + Path.DirectorySeparatorChar));   // a trailing slash is the same place
    }

    [Fact]
    public void EnsureWritesWhereTheFolderCameFrom()
    {
        var store = new WorkspaceStore(_root).Ensure();

        Assert.True(Directory.Exists(store.SessionsDir));
        Assert.Contains(Path.GetFileName(_root), File.ReadAllText(Path.Combine(store.Dir, "workspace.json")));
    }

    [Fact]
    public void MemoryAppendsAndTheOldestEntriesFallOffPastTheCap()
    {
        var store = new WorkspaceStore(_root);
        Assert.Equal("", store.ReadMemory());

        store.Remember("## first\n- did: a");
        store.Remember("## second\n- did: b");
        Assert.StartsWith("## first", store.ReadMemory());
        Assert.Contains("## second", store.ReadMemory());

        // Blow past the cap with big entries; the newest must survive whole and the file stay under the cap.
        for (var i = 0; i < 12; i++) store.Remember($"## big{i}\n" + new string('x', 6000));
        var all = store.ReadMemory();
        Assert.True(all.Length <= WorkspaceStore.MemoryCapChars, all.Length.ToString());
        Assert.DoesNotContain("## first", all);
        Assert.Contains("## big11", all);
        Assert.StartsWith("## ", all);                                  // cut at an entry boundary, never mid-entry
    }

    [Fact]
    public void ThePromptGetsTheNewestPartCutAtAnEntry()
    {
        var store = new WorkspaceStore(_root);
        for (var i = 0; i < 5; i++) store.Remember($"## entry{i}\n" + new string('y', 2000));

        var prompt = store.MemoryForPrompt();

        Assert.True(prompt.Length <= WorkspaceStore.MemoryPromptChars);
        Assert.StartsWith("## entry", prompt);
        Assert.Contains("## entry4", prompt);
    }

    [Fact]
    public void SessionsAreListedNewestFirstWithTheirTitleAndTurnCount()
    {
        var store = new WorkspaceStore(_root).Ensure();

        var older = SessionStore.Create("chat", store.SessionsDir);
        older.Prompt("first thing");
        older.Result(new AgentRun(StopReason.Final, "done", [], TimeSpan.Zero));
        older.Title("첫 번째 작업");
        Thread.Sleep(1100);                                             // ids are second-resolution
        var newer = SessionStore.Create("chat", store.SessionsDir);
        newer.Prompt("second thing");
        newer.Prompt("and more");
        var empty = SessionStore.Create("chat", store.SessionsDir);     // never written: not listed
        _ = empty;

        var list = store.ListSessions();

        Assert.Equal(2, list.Count);
        Assert.Equal(newer.Path, list[0].Path);
        Assert.Equal(2, list[0].Turns);
        Assert.Equal("second thing", list[0].Title);                   // no title entry: the first prompt stands in
        Assert.Equal("첫 번째 작업", list[1].Title);
        Assert.Equal(1, list[1].Turns);
    }
}

public class TaskTitlerTests
{
    [Theory]
    [InlineData("게시판 API 빌드 오류 수정", "게시판 API 빌드 오류 수정")]
    [InlineData("\"Board API build fix\"", "Board API build fix")]
    [InlineData("Title: Fix the build.", "Fix the build")]
    [InlineData("{\"tool\":\"final\",\"args\":{\"text\":\"Scaffold hello.py\"}}", "Scaffold hello.py")]
    [InlineData("first line\nsecond line", "first line")]
    public void TheNameIsTakenFromWhateverShapeTheModelReturned(string reply, string expected)
    {
        Assert.Equal(expected, TaskTitler.Clean(reply));
    }

    [Fact]
    public void AnOverlongNameIsCut()
    {
        var name = TaskTitler.Clean(new string('a', 100));
        Assert.True(name.Length <= TaskTitler.MaxChars + 1);
        Assert.EndsWith("…", name);
    }

    [Fact]
    public void ThePromptCarriesTheOldNameSoARenameIsARename()
    {
        var request = TaskTitler.Request("now the tests", "ran 12 tests", "Board API build");
        Assert.Contains("Board API build", request);
        Assert.Contains("now the tests", request);
    }
}

public class LoopRestoreTests
{
    [Fact]
    public void ARestoredExchangeIsInTheConversationAsQuestionAndFinalAnswer()
    {
        var loop = new AgentLoop(new ScriptedChatProvider("x"), new AgentOne.Tools.LocalFileToolbelt(Path.GetTempPath()));
        loop.Reset("## earlier\n- did: things");

        loop.Restore("what is this?", "a CLI agent");

        Assert.Equal(3, loop.Messages.Count);
        Assert.Contains("Earlier work in this workspace", loop.Messages[0].Content);
        Assert.Contains("## earlier", loop.Messages[0].Content);
        Assert.Equal("what is this?", loop.Messages[1].Content);
        Assert.Contains("\"final\"", loop.Messages[2].Content);
        Assert.Contains("a CLI agent", loop.Messages[2].Content);
    }

    [Fact]
    public void WithoutMemoryTheSystemPromptHasNoMemorySection()
    {
        Assert.DoesNotContain("Earlier work", SystemPrompt.Build("/ws"));
        Assert.DoesNotContain("Earlier work", SystemPrompt.Build("/ws", "   "));
    }
}

/// <summary>The session remembers, resumes and names its task.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class MemoryAndResumeSessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public MemoryAndResumeSessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-mem-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine? engine = null, bool smart = false, bool save = true)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", smart ? "on" : "off", out _);
        config.TrySet("saveSessions", save ? "true" : "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine ?? new ScriptedDecisionEngine(Choose("x", 1)), engine is not null);
    }

    private static async Task<string?> WaitForTitleAsync(ChatSession session, string? other = null)
    {
        for (var i = 0; i < 200; i++)
        {
            if (session.Title is { } t && t != other) return t;
            await Task.Delay(10);
        }
        return session.Title;
    }

    [Fact]
    public async Task ATurnIsWrittenToTheWorkspaceMemoryAndTheNextSessionOpensWithIt()
    {
        using (var first = Session(new ScriptedChatProvider(
                   """{"tool":"write_file","args":{"path":"hello.py","content":"print(1)"}}""",
                   """{"tool":"final","args":{"text":"made hello.py"}}""")))
        {
            await first.SubmitAsync("make hello.py please", CancellationToken.None);
        }

        var memory = new WorkspaceStore(_root).ReadMemory();
        Assert.Contains("- asked: make hello.py please", memory);
        Assert.Contains("write_file: path=hello.py", memory);
        Assert.Contains("- outcome: made hello.py", memory);

        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"yes, hello.py is here"}}""");
        using var second = Session(provider);
        await second.SubmitAsync("what did we do here?", CancellationToken.None);

        var system = provider.Calls[0][0];
        Assert.Equal("system", system.Role);
        Assert.Contains("Earlier work in this workspace", system.Content);
        Assert.Contains("make hello.py please", system.Content);
    }

    [Fact]
    public async Task TheLogLivesUnderTheWorkspaceAndListsForResume()
    {
        using var session = Session(new ScriptedChatProvider("""{"tool":"final","args":{"text":"ok"}}"""));
        await session.SubmitAsync("a question", CancellationToken.None);

        Assert.StartsWith(session.Workspace.SessionsDir, session.LogPath!);
        var list = session.ListSessions();
        Assert.Single(list);
        Assert.Equal(session.LogPath, list[0].Path);
    }

    [Fact]
    public async Task ResumeRebuildsTheContextRestoresTheTitleAndKeepsAppendingToTheSameFile()
    {
        string path;
        using (var first = Session(new ScriptedChatProvider(
                   """{"tool":"final","args":{"text":"the board API has one endpoint"}}""",
                   "게시판 API 만들기")))                                 // the second reply names the task
        {
            await first.SubmitAsync("tell me about the board api", CancellationToken.None);
            Assert.Equal("게시판 API 만들기", await WaitForTitleAsync(first));
            path = first.LogPath!;
        }

        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"continuing"}}""");
        using var second = Session(provider);

        string? announced = null;
        second.TitleChanged += t => announced = t;
        var entries = second.Resume(path);

        Assert.Equal("게시판 API 만들기", second.Title);
        Assert.Equal("게시판 API 만들기", announced);
        Assert.Contains(entries, e => e.Kind == "prompt" && e.Text == "tell me about the board api");
        Assert.Equal(path, second.LogPath);
        Assert.Equal(1, second.Stats().Counters.Turns);

        await second.SubmitAsync("and now?", CancellationToken.None);

        var seen = provider.Calls[0];
        Assert.Contains(seen, m => m.Role == "user" && m.Content == "tell me about the board api");
        Assert.Contains(seen, m => m.Role == "assistant" && m.Content.Contains("one endpoint"));
        Assert.Contains(WorkspaceStore.ReadEntries(path), e => e.Kind == "resumed");
        Assert.Contains(WorkspaceStore.ReadEntries(path), e => e.Kind == "prompt" && e.Text == "and now?");
    }

    [Fact]
    public async Task WithAnEngineTheTitleChangesOnlyWhenTheTaskDoes()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"built"}}""", "Board API build",       // turn 1 + its name
            """{"tool":"final","args":{"text":"fixed"}}""",                          // turn 2 (same task: no naming call)
            """{"tool":"final","args":{"text":"deployed"}}""", "Deploy to staging"); // turn 3 + its new name
        var engine = new ScriptedDecisionEngine(
            Choose(SmartRouter.SameTask, 0.9),
            Choose(SmartRouter.NewTask, 0.8));
        using var session = Session(provider, engine, smart: false);

        await session.SubmitAsync("build the board api", CancellationToken.None);
        Assert.Equal("Board API build", await WaitForTitleAsync(session));

        await session.SubmitAsync("fix the error", CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal("Board API build", session.Title);                 // the engine said same_task
        Assert.Equal(1, engine.Calls);

        await session.SubmitAsync("now deploy it", CancellationToken.None);
        Assert.Equal("Deploy to staging", await WaitForTitleAsync(session, "Board API build"));
        Assert.Equal(2, engine.Calls);
        Assert.Contains(WorkspaceStore.ReadEntries(session.LogPath!), e => e.Kind == "title" && e.Text == "Deploy to staging");
    }

    [Fact]
    public async Task WithoutAnEngineTheTaskIsNamedOnceAndNewSessionForgetsIt()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"a"}}""", "First name",
            """{"tool":"final","args":{"text":"b"}}""");
        using var session = Session(provider);

        await session.SubmitAsync("first request here", CancellationToken.None);
        Assert.Equal("First name", await WaitForTitleAsync(session));

        await session.SubmitAsync("second request here", CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal("First name", session.Title);
        Assert.Equal(3, provider.Calls.Count);                          // two turns, one naming call

        session.NewSession();
        Assert.Null(session.Title);
        Assert.Contains("(not named yet)", session.Stats().Describe()[0]);
    }
}
