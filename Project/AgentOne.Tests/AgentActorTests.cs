using AgentOne.Actors;
using AgentOne.Agent;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using Akka.Actor;
using Akka.TestKit.Xunit2;

namespace AgentOne.Tests;

/// <summary>
/// The Bot / Loop pair, driven the way AgentZero's tests drive its pair: the
/// test is the parent, so what the loop tells its parent lands in the probe.
/// The session under the loop is a scripted ChatSession — no network, no keys.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public sealed class AgentLoopActorTests : TestKit, IDisposable
{
    private readonly string _home;
    private readonly string _root;

    public AgentLoopActorTests() : base("akka.loglevel = WARNING")
    {
        _home = Path.Combine(Path.GetTempPath(), "agent-one-actor-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public new void Dispose()
    {
        base.Dispose();
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, null);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private AgentLoopBindings Bindings(ScriptedChatProvider provider, IDecisionEngine? engine = null, bool smart = false) =>
        new(() =>
        {
            var config = new AgentConfig();
            config.TrySet("smartMode", smart ? "on" : "off", out _);
            config.TrySet("saveSessions", "false", out _);
            return new ChatSession(config, _root, streaming: false, provider, engine ?? new ScriptedDecisionEngine(), engine is not null)
                { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
        });

    /// <summary>The loop as a child of the probe, so Context.Parent is the probe.</summary>
    private IActorRef Loop(AgentLoopBindings bindings) =>
        ActorOfAsTestActorRef<AgentLoopActor>(Props.Create(() => new AgentLoopActor(bindings)), TestActor);

    [Fact]
    public void ATurnGoesThinkingActingDoneAndEndsInOneResult()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"."}}""",
            """{"tool":"final","args":{"text":"two files"}}""");
        var loop = Loop(Bindings(provider));

        loop.Tell(new StartAgentLoop("what is here?"));

        var first = ExpectMsg<AgentLoopProgress>();
        Assert.Equal(AgentLoopPhase.Thinking, first.Phase);

        var seen = new List<object>();
        AgentLoopResult? result = null;
        while (result is null)
        {
            var msg = ExpectMsg<object>(TimeSpan.FromSeconds(10));
            seen.Add(msg);
            result = msg as AgentLoopResult;
        }

        Assert.True(result.Success);
        Assert.Equal("two files", result.FinalMessage);
        Assert.NotNull(result.Run);
        Assert.Contains(seen, m => m is AgentLoopProgress { Phase: AgentLoopPhase.Acting, Step.Tool: "list_files", Round: 1 });
        Assert.Contains(seen, m => m is AgentLoopProgress { Phase: AgentLoopPhase.Done });
        Assert.Single(seen.OfType<AgentLoopResult>());
    }

    [Fact]
    public void ACancelledTurnStillEndsInExactlyOneResultMarkedCancelled()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"late"}}""") { Delay = TimeSpan.FromSeconds(5) };
        var loop = Loop(Bindings(provider));

        loop.Tell(new StartAgentLoop("take your time"));
        ExpectMsg<AgentLoopProgress>(p => p.Phase == AgentLoopPhase.Thinking);
        loop.Tell(CancelAgentLoop.Instance);

        var result = FishForMessage<AgentLoopResult>(_ => true, TimeSpan.FromSeconds(10));
        Assert.False(result.Success);
        Assert.Equal(AgentLoopResult.Cancelled, result.FailureReason);

        // Back to Idle: a new turn is taken.
        provider.Delay = TimeSpan.Zero;
        loop.Tell(new StartAgentLoop("again"));
        ExpectMsg<AgentLoopProgress>(p => p.Phase == AgentLoopPhase.Thinking);
    }

    [Fact]
    public void AStartWhileRunningIsRefusedWithAResultAndTheTurnGoesOn()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"first"}}""") { Delay = TimeSpan.FromMilliseconds(500) };
        var loop = Loop(Bindings(provider));

        loop.Tell(new StartAgentLoop("one"));
        ExpectMsg<AgentLoopProgress>(p => p.Phase == AgentLoopPhase.Thinking);
        loop.Tell(new StartAgentLoop("two"));

        // The session's own "thinking" tick may land first; the refusal is a result.
        var refused = FishForMessage<AgentLoopResult>(_ => true, TimeSpan.FromSeconds(5));
        Assert.Contains("already running", refused.FailureReason);

        var real = FishForMessage<AgentLoopResult>(r => r.Success, TimeSpan.FromSeconds(10));
        Assert.Equal("first", real.FinalMessage);
    }

    [Fact]
    public void ACommandThatNeedsApprovalPausesTheTurnUntilResolvePause()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"run_command","args":{"command":"echo paused-run"}}""",
            """{"tool":"final","args":{"text":"ran"}}""");
        // Basic mode, no engine: every command is put to the person.
        var loop = Loop(Bindings(provider));

        loop.Tell(new StartAgentLoop("run it"));

        var pause = FishForMessage<ApprovalNeeded>(_ => true, TimeSpan.FromSeconds(10));
        Assert.Equal("echo paused-run", pause.Request.Command);

        loop.Tell(new ResolvePause(pause.PauseId, "y"));

        var result = FishForMessage<AgentLoopResult>(_ => true, TimeSpan.FromSeconds(30));
        Assert.True(result.Success);
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("paused-run"));
    }

    [Fact]
    public void SessionCommandsAnswerTheSenderAndWorkWhileIdle()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"hi"}}""");
        var loop = Loop(Bindings(provider));

        loop.Tell(QueryAgentInfo.Instance);
        var info = ExpectMsg<AgentSessionInfo>();
        Assert.Equal("scripted", info.ProviderName);
        Assert.False(info.SmartAvailable);

        loop.Tell(ToggleSmartMode.Instance);
        var toggled = ExpectMsg<SmartModeToggled>();
        Assert.False(toggled.Changed);                       // no key
        Assert.Contains("TypeSafe", toggled.Message);

        loop.Tell(QueryAgentStats.Instance);
        Assert.Equal(0, ExpectMsg<SessionStats>().Counters.Turns);

        loop.Tell(ResetAgentLoopMemory.Instance);
        ExpectMsg<AgentSessionReset>();
    }

    [Fact]
    public void ASessionThatCannotBeBuiltIsAMessageNotACrash()
    {
        var bindings = new AgentLoopBindings(() => throw new AgentOne.Llm.ChatProviderException("no key for provider x"));
        var loop = Loop(bindings);

        loop.Tell(QueryAgentInfo.Instance);
        Assert.Contains("no key", ExpectMsg<AgentSessionFailed>().Message);

        loop.Tell(new StartAgentLoop("hello"));
        var result = ExpectMsg<AgentLoopResult>();
        Assert.False(result.Success);
        Assert.Contains("no key", result.FailureReason);
    }
}

[Collection(AgentOneHomeCollection.Name)]
public sealed class AgentBotActorTests : TestKit, IDisposable
{
    private readonly string _home;
    private readonly string _root;

    public AgentBotActorTests() : base("akka.loglevel = WARNING")
    {
        _home = Path.Combine(Path.GetTempPath(), "agent-one-bot-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public new void Dispose()
    {
        base.Dispose();
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, null);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private AgentLoopBindings Bindings(ScriptedChatProvider provider) => new(() =>
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "off", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, new ScriptedDecisionEngine(), false)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
    });

    [Fact]
    public async Task TheBotSpawnsTheLoopLazilyRefusesASecondTurnAndCallsBack()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"answer"}}""") { Delay = TimeSpan.FromMilliseconds(300) };
        var bot = Sys.ActorOf(Props.Create(() => new AgentBotActor(Bindings(provider))), "bot");

        // Nothing under the bot yet.
        Sys.ActorSelection("/user/bot/loop").Tell(new Identify(1), TestActor);
        Assert.Null(ExpectMsg<ActorIdentity>().Subject);

        var progress = new List<AgentLoopProgress>();
        var result = new TaskCompletionSource<AgentLoopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bot.Tell(new SetAgentLoopCallbacks(progress.Add, r => result.TrySetResult(r), _ => { }, _ => { }));

        Assert.IsType<TurnAccepted>(await bot.Ask<object>(new StartAgentLoop("first"), TimeSpan.FromSeconds(5)));
        var refused = Assert.IsType<TurnRefused>(await bot.Ask<object>(new StartAgentLoop("second"), TimeSpan.FromSeconds(5)));
        Assert.Contains("already running", refused.Reason);

        var r = await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(r.Success);
        Assert.Equal("answer", r.FinalMessage);
        Assert.Contains(progress, p => p.Phase == AgentLoopPhase.Thinking);

        // The loop now exists, and the bot takes a turn again.
        Sys.ActorSelection("/user/bot/loop").Tell(new Identify(2), TestActor);
        Assert.NotNull(ExpectMsg<ActorIdentity>().Subject);
        Assert.IsType<TurnAccepted>(await bot.Ask<object>(new StartAgentLoop("third"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ACallbackThatThrowsIsDroppedNotFatal()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"still here"}}""");
        var bot = Sys.ActorOf(Props.Create(() => new AgentBotActor(Bindings(provider))));

        var result = new TaskCompletionSource<AgentLoopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bot.Tell(new SetAgentLoopCallbacks(_ => throw new InvalidOperationException("renderer bug"), r => result.TrySetResult(r), _ => { }, _ => { }));

        await bot.Ask<object>(new StartAgentLoop("go"), TimeSpan.FromSeconds(5));
        var r = await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("still here", r.FinalMessage);
    }
}

/// <summary>The renderers' handle, end to end: a scripted session under the actors, seen through the ChatSession-shaped surface.</summary>
[Collection(AgentOneHomeCollection.Name)]
public sealed class AgentGatewayTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;

    public AgentGatewayTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "agent-one-gw-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        _root = Path.Combine(_home, "ws");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, null);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private AgentGateway Gateway(ScriptedChatProvider provider) => AgentGateway.Start(new AgentLoopBindings(() =>
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "off", out _);
        config.TrySet("saveSessions", "false", out _);
        return new ChatSession(config, _root, streaming: false, provider, new ScriptedDecisionEngine(), false)
            { NamesTasks = false, UsesGraph = false, UsesPdsa = false };
    }));

    [Fact]
    public async Task ATurnRaisesTheSameEventsTheSessionWouldAndReturnsTheRun()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"list_files","args":{"path":"."}}""",
            """{"tool":"final","args":{"text":"done"}}""");
        using var gateway = Gateway(provider);

        var activities = new List<string>();
        var steps = new List<AgentStep>();
        gateway.ActivityStarted += activities.Add;
        gateway.StepCompleted += steps.Add;

        var run = await gateway.SubmitAsync("what is here?", CancellationToken.None);

        Assert.NotNull(run);
        Assert.True(run.Succeeded);
        Assert.Equal("done", run.Text);
        Assert.Contains(steps, s => s.Tool == "list_files");
        Assert.NotEmpty(activities);
        Assert.Equal(1, gateway.Stats().Counters.Turns);
        Assert.Equal("scripted", gateway.ProviderName);
        Assert.Null(await gateway.SubmitAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task TheApproverIsAskedOverTheActorsAndItsAnswerRunsTheCommand()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"run_command","args":{"command":"echo gateway-run"}}""",
            """{"tool":"final","args":{"text":"ran"}}""");
        using var gateway = Gateway(provider);

        ApprovalRequest? asked = null;
        gateway.Approver = (request, _) => { asked = request; return Task.FromResult(true); };

        var run = await gateway.SubmitAsync("run it", CancellationToken.None);

        Assert.NotNull(asked);
        Assert.Equal("echo gateway-run", asked.Command);
        Assert.True(run!.Succeeded);
        Assert.Contains(provider.Calls[1], m => m.Content.Contains("gateway-run"));
        Assert.Equal((1, 1), (gateway.Stats().Counters.ApprovalsAsked, gateway.Stats().Counters.ApprovalsGranted));
    }

    [Fact]
    public async Task CancellingTheTokenEndsTheTurnAsACancelledRun()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"late"}}""") { Delay = TimeSpan.FromSeconds(5) };
        using var gateway = Gateway(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // The same shape the session returns: a run that says it was cancelled.
        var cancelled = await gateway.SubmitAsync("slow", cts.Token);
        Assert.NotNull(cancelled);
        Assert.Equal(StopReason.Cancelled, cancelled.Reason);

        // And the next turn runs.
        provider.Delay = TimeSpan.Zero;
        var run = await gateway.SubmitAsync("fast", CancellationToken.None);
        Assert.Equal("late", run!.Text);
    }

    [Fact]
    public void SessionCommandsRoundTripAndKeepTheCachedInfoCurrent()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"hi"}}""");
        using var gateway = Gateway(provider);

        Assert.False(gateway.SmartAvailable);
        Assert.False(gateway.TryToggleSmart(out var message));
        Assert.Contains("TypeSafe", message);
        Assert.Empty(gateway.ListSessions());
        gateway.NewSession();
        Assert.Null(gateway.LogPath);                        // saveSessions off
        Assert.Contains(gateway.Stats().Describe(), l => l.StartsWith("mode      basic"));
    }

    [Fact]
    public void ASessionThatCannotBeBuiltThrowsFromStartAndLeavesNoSystemBehind()
    {
        var ex = Assert.Throws<AgentOne.Llm.ChatProviderException>(() =>
            AgentGateway.Start(new AgentLoopBindings(() => throw new AgentOne.Llm.ChatProviderException("no key"))));
        Assert.Contains("no key", ex.Message);
    }
}
