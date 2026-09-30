using System.Net;
using System.Text;
using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// A stream that stops sending. Measured (2026-09-29): a design call to the
/// reasoning model got its headers and then nothing, and the chat sat for 36
/// minutes — HttpClient.Timeout does not cover a body read after the headers.
/// The provider now ends such a read as a stall, and the callers offer the
/// same call again instead of quietly falling back.
/// </summary>
public class StreamStallTests
{
    private static OpenAiCompatChatProvider Provider(Func<Stream> body, TimeSpan idle)
    {
        var config = new AgentConfig();
        config.TrySet("baseUrl", "http://stall.test/v1", out _);
        return new OpenAiCompatChatProvider(config, new StreamHandler(body)) { StreamIdleTimeout = idle };
    }

    private static string Chunk(string text) =>
        "data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}\n\n";

    [Fact]
    public async Task AStreamThatGoesSilentEndsAsAStallWithWhatHadArrived()
    {
        using var provider = Provider(() => new ScriptedStream([(TimeSpan.Zero, Chunk("hello"))], hangAtEnd: true),
            TimeSpan.FromMilliseconds(300));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stall = await Assert.ThrowsAsync<ChatProviderStalledException>(() =>
            provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None, _ => { }));

        Assert.Equal(5, stall.ReceivedChars);
        Assert.Contains("stopped sending", stall.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AStreamThatKeepsTalkingIsNotCutEvenPastTheIdleLimitInTotal()
    {
        // Six chunks 120 ms apart: 720 ms in all, never 300 ms of silence.
        var parts = Enumerable.Range(0, 6).Select(i => (TimeSpan.FromMilliseconds(120), Chunk("p" + i))).ToList();
        parts.Add((TimeSpan.Zero, "data: [DONE]\n\n"));
        using var provider = Provider(() => new ScriptedStream(parts, hangAtEnd: false), TimeSpan.FromMilliseconds(300));

        var reply = await provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None, _ => { });

        Assert.Equal("p0p1p2p3p4p5", reply);
    }

    [Fact]
    public async Task TheCallersOwnCancelIsACancelNotAStall()
    {
        using var provider = Provider(() => new ScriptedStream([], hangAtEnd: true), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteAsync([ChatMessage.User("hi")], cts.Token, _ => { }));
    }

    private sealed class StreamHandler(Func<Stream> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) });
    }

    /// <summary>Hands out each part after its delay, then either ends or never answers again.</summary>
    private sealed class ScriptedStream(IReadOnlyList<(TimeSpan Delay, string Text)> parts, bool hangAtEnd) : Stream
    {
        private int _next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_next >= parts.Count)
            {
                if (!hangAtEnd) return 0;
                await Task.Delay(Timeout.Infinite, ct);
            }

            var (delay, text) = parts[_next++];
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            var bytes = Encoding.UTF8.GetBytes(text);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>What the loop and the session do with a stall: ask, and resend the same call.</summary>
[Collection(AgentOneHomeCollection.Name)]
public class StallRetrySessionTests : IDisposable
{
    private readonly string _home;
    private readonly string _root;
    private readonly string? _previous;

    public StallRetrySessionTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-stall-" + Guid.NewGuid().ToString("N")[..8]);
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

    private ChatSession Session(IChatProvider provider, IDecisionEngine engine, IChatProvider? reasoning = null)
    {
        var config = new AgentConfig();
        config.TrySet("smartMode", "on", out _);
        config.TrySet("saveSessions", "false", out _);
        if (reasoning is not null) config.TrySet("reasoningModel", "big-model", out _);
        return new ChatSession(config, _root, streaming: false, provider, engine, true, reasoning) { NamesTasks = false, UsesGraph = false, UsesPdsa = false, ChecksClaims = false };
    }

    private static ScriptedDecisionEngine Designing() =>
        new(Choose(SmartRouter.WorkInWorkspace, 0.9), Choose(SmartRouter.NeedsDesign, 0.9));

    [Fact]
    public async Task TheLoopResendsTheSameMessagesWhenTheStallIsRetried()
    {
        var inner = new ScriptedChatProvider("""{"tool":"final","args":{"text":"done"}}""");
        var provider = new StallingProvider(inner, stalls: 1);
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 4);
        var asked = new List<int>();
        loop.OnStall = (_, retries, _) => { asked.Add(retries); return Task.FromResult(true); };

        var run = await loop.RunAsync("hello");

        Assert.Equal(StopReason.Final, run.Reason);
        Assert.Equal("done", run.Text);
        Assert.Equal([0], asked);
        Assert.Equal(provider.Seen[0].Select(m => m.Content), provider.Seen[1].Select(m => m.Content));
        Assert.Contains(run.Steps, s => s.Tool == "stalled" && !s.Ok);
    }

    [Fact]
    public async Task ALoopWithNobodyToAskEndsOnAStallAsBefore()
    {
        var provider = new StallingProvider(new ScriptedChatProvider("unused"), stalls: 1);
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root), maxSteps: 4);

        var run = await loop.RunAsync("hello");

        Assert.Equal(StopReason.ProviderError, run.Reason);
        Assert.Single(provider.Seen);
    }

    [Fact]
    public async Task AStalledDesignIsRetriedWhenThePersonSaysSo()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"built"}}""");
        var strong = new StallingProvider(new ScriptedChatProvider("## Files\n1. src/a.cs"), stalls: 1);
        using var session = Session(basic, Designing(), strong);
        ChoiceRequest? asked = null;
        session.Chooser = (choice, _) => { asked = choice; return Task.FromResult("1"); };

        var run = await session.SubmitAsync("scaffold a board api with storage", CancellationToken.None);

        Assert.Equal("built", run!.Text);
        Assert.Equal(ChatSession.RetryOption, asked!.Options[0]);
        Assert.Equal(0, asked.Recommended);
        Assert.Equal(2, strong.Seen.Count);
        Assert.Contains(basic.Calls[0], m => m.Content.Contains("src/a.cs"));
    }

    [Fact]
    public async Task StoppingAtAStalledDesignEndsTheTurnWithNothingBuilt()
    {
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"should not run"}}""");
        var strong = new StallingProvider(new ScriptedChatProvider("plan"), stalls: 1);
        using var session = Session(basic, Designing(), strong);
        session.Chooser = (_, _) => Task.FromResult("3");   // retry · go on without · stop

        var run = await session.SubmitAsync("scaffold a board api with storage", CancellationToken.None);

        Assert.Equal(StopReason.Cancelled, run!.Reason);
        Assert.Equal(0, basic.CallCount);
    }

    [Fact]
    public async Task UnattendedTheDesignIsRetriedTwiceAndThenTheTurnStops()
    {
        // No chooser (`run`, or a background session with nobody attached) takes
        // the recommendation: retry while it is cheap, then stop rather than
        // loop forever against a dead endpoint — and never build without the
        // design silently.
        var basic = new ScriptedChatProvider("""{"tool":"final","args":{"text":"should not run"}}""");
        var strong = new StallingProvider(new ScriptedChatProvider("plan"), stalls: 99);
        using var session = Session(basic, Designing(), strong);

        var run = await session.SubmitAsync("scaffold a board api with storage", CancellationToken.None);

        Assert.Equal(1 + ChatSession.UnattendedStallRetries, strong.Seen.Count);
        Assert.Equal(StopReason.Cancelled, run!.Reason);
        Assert.Equal(0, basic.CallCount);
    }

    [Fact]
    public async Task AStalledTurnStepIsRetriedThroughTheChooser()
    {
        var inner = new ScriptedChatProvider("""{"tool":"final","args":{"text":"answered"}}""");
        var basic = new StallingProvider(inner, stalls: 1);
        using var session = Session(basic, new ScriptedDecisionEngine(Choose(SmartRouter.AnswerDirectly, 0.9)));
        ChoiceRequest? asked = null;
        session.Chooser = (choice, _) => { asked = choice; return Task.FromResult(""); };   // Enter: the recommendation

        var run = await session.SubmitAsync("what does this project do?", CancellationToken.None);

        Assert.Equal("answered", run!.Text);
        Assert.Equal([ChatSession.RetryOption, ChatSession.StopTurnOption], asked!.Options);
    }

    /// <summary>Stalls the first <paramref name="stalls"/> calls, then answers from <paramref name="inner"/>.</summary>
    private sealed class StallingProvider(IChatProvider inner, int stalls) : IChatProvider
    {
        public string Name => "stalling";
        public List<IReadOnlyList<ChatMessage>> Seen { get; } = [];

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null)
        {
            Seen.Add([.. messages]);
            if (Seen.Count <= stalls)
                throw new ChatProviderStalledException("stopped sending for 120s (0 chars received)");
            return inner.CompleteAsync(messages, ct, onDelta);
        }
    }
}
