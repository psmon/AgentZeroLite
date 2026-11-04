using System.Diagnostics;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// The escalation path — draft → escalate → reasoning → final write-up — driven
/// entirely by scripted providers and a scripted engine, so the loop is exercised
/// with no network and no model. This is the code-level reproduction of the hang
/// reported on the real on-device models: it proves the loop itself completes, and
/// that a model that never answers on any leg of the path ends the turn
/// deterministically (keeps the draft, or ends on a provider error) instead of
/// hanging forever.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class EscalationWatchdogTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public EscalationWatchdogTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-esc-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ChatSession Session(IChatProvider provider, IDecisionEngine engine, IChatProvider? reasoning, int maxCallSeconds = 0)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        if (reasoning is not null) config.TrySet("reasoningModel", "big-model", out _);
        config.MaxCallSeconds = maxCallSeconds;
        return new ChatSession(config, _root, streaming: false, provider, engine, true, reasoning)
        { NamesTasks = false, UsesGraph = false, UsesPdsa = false, ChecksClaims = false };
    }

    /// <summary>Route to answer_directly, then escalate — the two engine answers a smart turn needs here.</summary>
    private static ScriptedDecisionEngine RouteThenEscalate() =>
        new(Choose(SmartRouter.AnswerDirectly, 0.9), Choose(SmartRouter.EscalateOption, 0.9));

    private const string LongRequest = "please work out this problem carefully and explain the reasoning";

    // --- the path completes, end to end, with no network -------------------------

    [Fact]
    public async Task TheEscalationPathCompletesEndToEndWithoutTheNetwork()
    {
        var basic = new ScriptedChatProvider(
            """{"tool":"final","args":{"text":"draft answer"}}""",
            """{"tool":"final","args":{"text":"final answer after reasoning"}}""");
        var strong = new ScriptedChatProvider("deep reasoning result");
        using var session = Session(basic, RouteThenEscalate(), strong);

        var run = await session.SubmitAsync(LongRequest, CancellationToken.None);

        Assert.Equal("final answer after reasoning", run!.Text);
        Assert.Equal(2, basic.CallCount);                 // draft, then the final write-up
        Assert.Single(strong.Calls);                      // the reasoning model, once
        Assert.Contains(basic.Calls[1], m => m.Content.Contains("deep reasoning result"));   // the final saw the reasoning
    }

    // --- no leg of the path can hang the turn ------------------------------------

    [Fact]
    public async Task AReasoningModelThatNeverAnswersKeepsTheDraftUnattended()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"draft stands"}}""");
        var strong = new StallForeverProvider();
        using var session = Session(basic, RouteThenEscalate(), strong);   // no chooser: unattended

        var run = await session.SubmitAsync(LongRequest, CancellationToken.None);

        Assert.Equal("draft stands", run!.Text);
        Assert.Equal(1 + ChatSession.UnattendedStallRetries, strong.Calls);   // tried, retried, then kept the draft
        Assert.Equal(1, basic.CallCount);                                     // the final write-up never ran
    }

    [Fact]
    public async Task AFinalWriteUpThatNeverCompletesKeepsTheReasonedDraft()
    {
        // The exact hang that was reported: the draft and the reasoning both come
        // back, and the final everyday-model call never completes. The loop's stall
        // recovery retries and gives up, and the reasoned draft stands — the turn
        // ends with an answer, not a hang and not an error shown to the person.
        var basic = new StallFromProvider("""{"tool":"final","args":{"text":"draft answer"}}""", stallFromCall: 1);
        var strong = new ScriptedChatProvider("deep reasoning result");
        using var session = Session(basic, RouteThenEscalate(), strong);     // unattended

        var clock = Stopwatch.StartNew();
        var run = await session.SubmitAsync(LongRequest, CancellationToken.None);

        Assert.Equal("draft answer", run!.Text);
        Assert.True(run.Succeeded);
        Assert.Equal(1 + (1 + ChatSession.UnattendedStallRetries), basic.Calls);  // 1 draft + (attempt + 2 retries)
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    // --- the per-call watchdog ---------------------------------------------------

    [Fact]
    public async Task TheWatchdogEndsACallThatOutlastsItsDeadline()
    {
        var slow = new ScriptedChatProvider("too late") { Delay = TimeSpan.FromMinutes(10) };
        using var guarded = new WatchdogChatProvider(slow, TimeSpan.FromMilliseconds(150));

        var clock = Stopwatch.StartNew();
        var stall = await Assert.ThrowsAsync<ChatProviderStalledException>(() =>
            guarded.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None));

        Assert.Contains("watchdog", stall.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task TheWatchdogPassesAPromptAnswerThrough()
    {
        var fast = new ScriptedChatProvider("here you go");
        using var guarded = new WatchdogChatProvider(fast, TimeSpan.FromSeconds(30));

        var reply = await guarded.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.Equal("here you go", reply);
    }

    [Fact]
    public void WrapIsANoOpWhenTheCeilingIsDisabled()
    {
        var inner = new ScriptedChatProvider("x");
        Assert.Same(inner, WatchdogChatProvider.Wrap(inner, 0));
        Assert.Same(inner, WatchdogChatProvider.Wrap(inner, -5));
        Assert.IsType<WatchdogChatProvider>(WatchdogChatProvider.Wrap(inner, 10));
        Assert.Null(WatchdogChatProvider.Wrap(null, 10));
    }

    [Fact]
    public async Task TheCallersOwnCancelPassesThroughTheWatchdogAsACancel()
    {
        var slow = new ScriptedChatProvider("x") { Delay = TimeSpan.FromMinutes(10) };
        using var guarded = new WatchdogChatProvider(slow, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guarded.CompleteAsync([ChatMessage.User("hi")], cts.Token));

        Assert.IsNotType<ChatProviderStalledException>(ex);   // a cancel, not a stall
    }

    [Fact]
    public async Task AProviderThatNeverAnswersCannotHangTheLoop()
    {
        // The whole point, at the loop level: a model that hangs forever is turned
        // into a stall by the watchdog, and a loop with nobody to ask ends on it.
        var slow = new ScriptedChatProvider("""{"tool":"final","args":{"text":"never"}}""") { Delay = TimeSpan.FromMinutes(10) };
        using var guarded = new WatchdogChatProvider(slow, TimeSpan.FromMilliseconds(150));
        var loop = new AgentLoop(guarded, new LocalFileToolbelt(_root), maxSteps: 4);

        var clock = Stopwatch.StartNew();
        var run = await loop.RunAsync("hello");

        Assert.Equal(StopReason.ProviderError, run.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    /// <summary>Stalls on every call — a model that never answers.</summary>
    private sealed class StallForeverProvider : IChatProvider
    {
        public string Name => "stall-forever";
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null)
        {
            Calls++;
            throw new ChatProviderStalledException("stopped sending for 120s (0 chars received)");
        }
    }

    /// <summary>Answers the first calls, then stalls from <paramref name="stallFromCall"/> on.</summary>
    private sealed class StallFromProvider(string reply, int stallFromCall) : IChatProvider
    {
        public string Name => "stall-from";
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null)
        {
            var n = Calls++;
            if (n >= stallFromCall)
                throw new ChatProviderStalledException("stopped sending for 120s (0 chars received)");
            return Task.FromResult(reply);
        }
    }
}
