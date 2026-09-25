using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>
/// The background session over its pipe: a real NamedPipeServerStream on a
/// private name, a scripted session behind it, the client in the same process.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class SessionPipeTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;
    private readonly string _pipe = "agent-one-test-" + Guid.NewGuid().ToString("N")[..8];

    public SessionPipeTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-pipe-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ChatSession Session(ScriptedChatProvider provider, IDecisionEngine? engine = null)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "off", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: true, provider,
            engine ?? new ScriptedDecisionEngine(Choose("x", 1)), engine is not null) { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
    }

    private static async Task<(SessionServer Server, Task Running)> StartAsync(ChatSession session, string pipe, CancellationToken ct)
    {
        var server = new SessionServer(session, pipe);
        var running = server.RunAsync(ct);
        await server.Listening.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        return (server, running);
    }

    [Fact]
    public async Task ATurnGoesInAndItsEventsAndResultComeBack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var session = Session(new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"there is no readme"}}"""));
        var (server, running) = await StartAsync(session, _pipe, cts.Token);

        var events = new List<PipeEvent>();
        var result = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "what does the readme say?" },
            e => { events.Add(e); return Task.CompletedTask; }, null, cts.Token);

        Assert.Equal("result", result.Event);
        Assert.Equal("there is no readme", result.Text);
        Assert.Equal("Final", result.Kind);
        Assert.Contains(events, e => e.Event == "step" && e.Tool == "read_file" && e.Ok == false);   // no README here
        Assert.Contains(events, e => e.Event == "delta");
        Assert.True(result.Streamed > 0);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.True(server.StopRequested);
    }

    [Fact]
    public async Task ACommandNeedingApprovalIsAskedBackOverThePipeAndTheAnswerCounts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var session = Session(new ScriptedChatProvider(
            """{"tool":"run_command","args":{"command":"echo piped-approval"}}""",
            """{"tool":"final","args":{"text":"ran it"}}"""));            // no engine: every command asks
        var (_, running) = await StartAsync(session, _pipe, cts.Token);

        PipeEvent? asked = null;
        var result = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "say something" },
            null, e => { asked = e; return Task.FromResult("y"); }, cts.Token);

        Assert.NotNull(asked);
        Assert.Equal("ask", asked!.Event);
        Assert.Equal("echo piped-approval", asked.Text);
        Assert.Equal("ran it", result.Text);
        Assert.Equal(1, session.Stats().Counters.ApprovalsGranted);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task YesApprovesWithoutAskingBack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var session = Session(new ScriptedChatProvider(
            """{"tool":"run_command","args":{"command":"echo unasked"}}""",
            """{"tool":"final","args":{"text":"done"}}"""));
        var (_, running) = await StartAsync(session, _pipe, cts.Token);

        var askedBack = false;
        var notes = new List<string>();
        var result = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "run it", Yes = true },
            e => { if (e.Event == "note") notes.Add(e.Text); return Task.CompletedTask; },
            _ => { askedBack = true; return Task.FromResult("n"); }, cts.Token);

        Assert.False(askedBack);
        Assert.Contains(notes, n => n.StartsWith("running (yes)"));
        Assert.Equal("done", result.Text);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task StatusAndSlashCommandsWorkThroughThePipe()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var session = Session(new ScriptedChatProvider("""{"tool":"final","args":{"text":"ok"}}"""));
        var (_, running) = await StartAsync(session, _pipe, cts.Token);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "first" }, null, null, cts.Token);
        var status = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "status" }, null, null, cts.Token);
        Assert.Contains("turns 1", status.Text);

        var reset = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "/new" }, null, null, cts.Token);
        Assert.Equal("Reset", reset.Kind);
        Assert.Contains("turns 0", (await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "status" }, null, null, cts.Token)).Text);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task ADetachedTurnRunsOnItsOwnAndWaitCollectsIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{}}""",
            """{"tool":"final","args":{"text":"two files"}}""") { Delay = TimeSpan.FromMilliseconds(400) };
        using var session = Session(provider);
        var (server, running) = await StartAsync(session, _pipe, cts.Token);

        var accepted = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "what is here?", Detach = true }, null, null, cts.Token);
        Assert.Equal("Accepted", accepted.Kind);
        Assert.True(server.Busy);

        // While it runs: status answers at once and says what is going on; a second ask is refused, not queued.
        var status = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "status" }, null, null, cts.Token);
        Assert.Contains("state     working", status.Text);
        Assert.Contains("what is here?", status.Text);
        var busy = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "another" }, null, null, cts.Token);
        Assert.Equal("error", busy.Event);
        Assert.Equal("Busy", busy.Kind);

        var events = new List<PipeEvent>();
        var result = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "wait" },
            e => { events.Add(e); return Task.CompletedTask; }, null, cts.Token);
        Assert.Equal("attached", events[0].Event);
        Assert.Equal("what is here?", events[0].Text);
        Assert.Equal("two files", result.Text);
        Assert.Equal(1, result.Turn);
        Assert.Equal(1, result.Steps);
        Assert.Equal("what is here?", result.Request);

        // Idle: wait hands back the last result, and status says how it ended.
        var again = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "wait" }, null, null, cts.Token);
        Assert.Equal("two files", again.Text);
        Assert.Contains("state     idle", (await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "status" }, null, null, cts.Token)).Text);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task ACallerThatLeavesMidTurnDoesNotStopItAndTheNextAskCarriesOn()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"the answer is 42"}}""") { Delay = TimeSpan.FromMilliseconds(600) };
        using var session = Session(provider);
        var (server, running) = await StartAsync(session, _pipe, cts.Token);

        // The caller gives up after 150 ms — a timeout, a Ctrl+C.
        using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "what is the answer?" }, null, null, impatient.Token));
        }

        var first = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "wait" }, null, null, cts.Token);
        Assert.Equal("the answer is 42", first.Text);
        Assert.Equal(true, first.Ok);

        // The conversation carried on: the next turn is turn 2 and the model sees the first exchange.
        var second = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "say it again" }, null, null, cts.Token);
        Assert.Equal(2, second.Turn);
        Assert.Contains(provider.Calls[^1], m => m.Content.Contains("the answer is 42"));
        Assert.False(server.Busy);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task ACommandWithNobodyAttachedIsRefusedNotHung()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var session = Session(new ScriptedChatProvider(
            """{"tool":"run_command","args":{"command":"echo nobody-here"}}""",
            """{"tool":"final","args":{"text":"could not run it"}}"""));
        var (_, running) = await StartAsync(session, _pipe, cts.Token);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "run it", Detach = true }, null, null, cts.Token);
        var result = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "wait" }, null, null, cts.Token);

        Assert.Equal("could not run it", result.Text);
        Assert.Equal(0, session.Stats().Counters.ApprovalsGranted);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task CancelEndsTheRunningTurnAndTheSessionCarriesOn()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"slow answer"}}""") { Delay = TimeSpan.FromSeconds(3) };
        using var session = Session(provider);
        var (server, running) = await StartAsync(session, _pipe, cts.Token);

        Assert.Equal("Idle", (await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "cancel" }, null, null, cts.Token)).Kind);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "take your time", Detach = true }, null, null, cts.Token);
        var cancel = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "cancel" }, null, null, cts.Token);
        Assert.Equal("Cancelling", cancel.Kind);

        var ended = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "wait" }, null, null, cts.Token);
        Assert.NotEqual(true, ended.Ok);
        Assert.Contains("Cancel", ended.Kind);
        Assert.False(server.Busy);

        provider.Delay = TimeSpan.Zero;
        var next = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "now quickly" }, null, null, cts.Token);
        Assert.Equal("slow answer", next.Text);

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task ThePreviousTurnsLateNotesComeAsAfterEventsNotAsTheNewTurns()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var session = new LateNoteSession();
        var server = new SessionServer(session, _pipe);
        var running = server.RunAsync(cts.Token);
        await server.Listening.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var events = new List<PipeEvent>();
        var result = await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "ask", Text = "second request" },
            e => { events.Add(e); return Task.CompletedTask; }, null, cts.Token);

        Assert.Equal("ok", result.Text);
        Assert.Contains(events, e => e.Event == "after" && e.Kind == "knowledge" && e.Text == "nothing worth keeping");
        Assert.Contains(events, e => e.Event == "after" && e.Kind == "note" && e.Text.Contains("cycle #1 closed"));
        Assert.Contains(events, e => e.Event == "decided" && e.Kind == "route");
        Assert.DoesNotContain(events, e => e.Event == "decided" && e.Kind == "knowledge");

        await SessionClient.SendAsync(_pipe, new PipeRequest { Op = "stop" }, null, null, cts.Token);
        await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    /// <summary>
    /// A session whose previous turn's after-work reports in while the new
    /// turn runs — the race the flag exists for, made deterministic.
    /// </summary>
    private sealed class LateNoteSession : IAgentSession
    {
        private bool _after;
        private static Decision D(string c) => new(true, c, 0.9, new Dictionary<string, double> { [c] = 0.9 }, "ok", 1);

        public Task<AgentRun?> SubmitAsync(string line, CancellationToken ct)
        {
            ActivityStarted?.Invoke("thinking");
            _after = true;
            Decided?.Invoke(new SmartNote("knowledge", D("skip"), "nothing worth keeping"));
            Noted?.Invoke("  ↳ cycle #1 closed (met)");
            _after = false;
            Decided?.Invoke(new SmartNote("route", D("answer"), "→ answer"));
            return Task.FromResult<AgentRun?>(new AgentRun(StopReason.Final, "ok", [], TimeSpan.Zero));
        }

        public bool RaisingAfterTurn => _after;

        public event Action<string>? ActivityStarted;
        public event Action<AgentStep>? StepCompleted { add { } remove { } }
        public event Action<string>? AnswerDelta { add { } remove { } }
        public event Action<SmartNote>? Decided;
        public event Action<string>? Noted;
        public event Action<string>? TitleChanged { add { } remove { } }
        public event Action<IReadOnlyList<string>>? DesignMade { add { } remove { } }
        public event Action<IReadOnlyList<Distilled>>? Learned { add { } remove { } }
        public Func<ApprovalRequest, CancellationToken, Task<bool>>? Approver { get; set; }
        public Func<ChoiceRequest, CancellationToken, Task<string>>? Chooser { get; set; }
        public string Root => "";
        public string ProviderName => "fake";
        public string Model => "fake";
        public string? ReasoningModel => null;
        public string ToolScope => "";
        public string Shell => "";
        public string? LogPath => null;
        public bool SmartAvailable => false;
        public bool Smart => false;
        public string? Title => null;
        public WorkspaceStore Workspace => throw new NotSupportedException();
        public bool Paused => false;
        public void Pause() { }
        public Task<PauseOutcome> ResumeAsync(string line, CancellationToken ct) => throw new NotSupportedException();
        public SessionStats Stats() => throw new InvalidOperationException("no stats in the fake");
        public bool TryToggleSmart(out string message) { message = ""; return false; }
        public void Reset() { }
        public void NewSession() { }
        public IReadOnlyList<SessionSummary> ListSessions() => [];
        public IReadOnlyList<SessionEntry> Resume(string path) => [];
        public void Dispose() { }
    }

    [Fact]
    public async Task NoServerIsAnErrorEventNotAnException()
    {
        SessionClient.ConnectTimeoutMs = 300;
        try
        {
            var reply = await SessionClient.SendAsync("agent-one-nobody-" + Guid.NewGuid().ToString("N")[..6],
                new PipeRequest { Op = "status" }, null, null, CancellationToken.None);

            Assert.Equal("error", reply.Event);
            Assert.Contains("no background session", reply.Text);
        }
        finally
        {
            SessionClient.ConnectTimeoutMs = 3000;
        }
    }

    [Fact]
    public void TheRegistryForgetsADeadProcess()
    {
        SessionRegistry.Save(new SessionRecord { Pid = 999_999_999, Pipe = "x", Root = _root, Started = "now" });

        Assert.Null(SessionRegistry.LoadAlive());
        Assert.False(File.Exists(SessionRegistry.Path));
    }
}

public sealed class DetachedProcessTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has space", "\"has space\"")]
    [InlineData(@"C:\dir with space\", @"""C:\dir with space\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData("", "\"\"")]
    public void QuotesTheWayCommandLineToArgvUnquotes(string arg, string expected)
    {
        var sb = new System.Text.StringBuilder();
        DetachedProcess.Quote(sb, arg);
        Assert.Equal(expected, sb.ToString());
    }
}

