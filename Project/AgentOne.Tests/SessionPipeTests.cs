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
            engine ?? new ScriptedDecisionEngine(Choose("x", 1)), engine is not null) { NamesTasks = false };
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
