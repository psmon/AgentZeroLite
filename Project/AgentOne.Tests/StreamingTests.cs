using AgentOne.Agent;
using AgentOne.Llm;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// Showing the answer as it is written. The model streams JSON, so the job is
/// to show the prose inside it and nothing else — never the braces, and never a
/// tool call's arguments dressed up as an answer.
/// </summary>
public class FinalAnswerStreamerTests
{
    /// <summary>Pushes a reply through in fragments of <paramref name="size"/>, as a provider would.</summary>
    private static (string Visible, FinalAnswerStreamer Streamer) Stream(string raw, int size)
    {
        var streamer = new FinalAnswerStreamer();
        var seen = "";

        for (int i = 0; i < raw.Length; i += size)
            seen += streamer.Push(raw[i..Math.Min(i + size, raw.Length)]);

        return (seen, streamer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public void TheAnswerAppearsWhateverTheFragmentsLookLike(int size)
    {
        var raw = """{"tool":"final","args":{"text":"Hello there."}}""";

        var (visible, streamer) = Stream(raw, size);

        Assert.Equal("Hello there.", visible);
        Assert.Equal("Hello there.", streamer.Visible);
    }

    [Fact]
    public void NothingLeaksBeforeTheTextBegins()
    {
        var streamer = new FinalAnswerStreamer();

        Assert.Equal("", streamer.Push("""{"tool":"fin"""));
        Assert.Equal("", streamer.Push("""al","args":{"te"""));
        Assert.Equal("", streamer.Push("""xt":"""));
        Assert.Equal("Hi", streamer.Push("""  "Hi"""));
    }

    [Fact]
    public void ATooolCallStreamsNothingAtAll()
    {
        // grep has a "text" argument too — printing it as the answer would be a
        // lie with a very plausible shape.
        var raw = """{"tool":"grep","args":{"text":"ConfigureServices"}}""";

        var (visible, streamer) = Stream(raw, 5);

        Assert.Equal("", visible);
        Assert.True(streamer.IsToolCall);
    }

    [Fact]
    public void EscapesAreDecodedEvenWhenSplitAcrossFragments()
    {
        var raw = """{"tool":"final","args":{"text":"line one\nline \"two\"\ttabbed"}}""";

        var (visible, _) = Stream(raw, 1);        // every character its own fragment

        Assert.Equal("line one\nline \"two\"\ttabbed", visible);
    }

    [Fact]
    public void UnicodeEscapesSurviveBeingSplit()
    {
        var raw = """{"tool":"final","args":{"text":"한글"}}""";

        var (visible, _) = Stream(raw, 2);

        Assert.Equal("한글", visible);
    }

    [Fact]
    public void RealMultibyteTextPassesThroughIntact()
    {
        var raw = """{"tool":"final","args":{"text":"안녕하세요 — こんにちは"}}""";

        var (visible, _) = Stream(raw, 3);

        Assert.Equal("안녕하세요 — こんにちは", visible);
    }

    [Fact]
    public void StreamingStopsAtTheClosingQuoteNotAtTheBrace()
    {
        var raw = """{"tool":"final","args":{"text":"done"},"extra":"ignored"}""";

        var (visible, _) = Stream(raw, 4);

        Assert.Equal("done", visible);
    }

    [Fact]
    public void ATruncatedStreamShowsWhatArrived()
    {
        var streamer = new FinalAnswerStreamer();
        streamer.Push("""{"tool":"final","args":{"text":"half an ans""");

        Assert.Equal("half an ans", streamer.Visible);
    }

    [Fact]
    public void ProseBeforeTheEnvelopeDoesNotConfuseIt()
    {
        var raw = """Sure! {"tool":"final","args":{"text":"ok"}}""";

        var (visible, _) = Stream(raw, 6);

        Assert.Equal("ok", visible);
    }
}

public class LoopStreamingTests : IDisposable
{
    private readonly string _root;

    public LoopStreamingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-one-stream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentLoop Loop(ScriptedChatProvider provider) =>
        new(provider, new LocalFileToolbelt(_root), maxSteps: 8) { Streaming = true };

    [Fact]
    public async Task TheAnswerArrivesInPiecesAndTheRunAgrees()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"streamed answer"}}""");
        var loop = Loop(provider);

        var pieces = new List<string>();
        loop.AnswerDelta += pieces.Add;

        var run = await loop.RunAsync("hi");

        Assert.True(pieces.Count > 1, "the answer should arrive in more than one piece");
        Assert.Equal("streamed answer", string.Concat(pieces));
        Assert.Equal("streamed answer", run.Text);
        Assert.Equal("streamed answer", run.Streamed);
        Assert.Equal("", run.Unstreamed);          // nothing left to print
    }

    [Fact]
    public async Task AToolCallStreamsNothingButStillReportsActivity()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"read_file","args":{"path":"README.md"}}""",
            """{"tool":"final","args":{"text":"it says hello"}}""");

        var loop = Loop(provider);

        var streamed = new List<string>();
        var activities = new List<string>();
        loop.AnswerDelta += streamed.Add;
        loop.ActivityStarted += activities.Add;

        var run = await loop.RunAsync("what does it say?");

        Assert.Equal("it says hello", string.Concat(streamed));   // only the answer
        Assert.Contains(activities, a => a.Contains("thinking"));
        Assert.Contains(activities, a => a.Contains("reading README.md"));
        Assert.Equal("it says hello", run.Text);
    }

    [Fact]
    public async Task ActivityNamesTheToolInWordsNotItsVerb()
    {
        var provider = new ScriptedChatProvider(
            """{"tool":"grep","args":{"text":"needle"}}""",
            """{"tool":"final","args":{"text":"done"}}""");

        var loop = Loop(provider);
        var activities = new List<string>();
        loop.ActivityStarted += activities.Add;

        await loop.RunAsync("find it");

        Assert.Contains(activities, a => a.Contains("searching files for \"needle\""));
    }

    [Fact]
    public async Task WithoutStreamingNothingIsPreviewedAndTheWholeAnswerRemains()
    {
        var provider = new ScriptedChatProvider("""{"tool":"final","args":{"text":"quiet answer"}}""");
        var loop = new AgentLoop(provider, new LocalFileToolbelt(_root)) { Streaming = false };

        var pieces = new List<string>();
        loop.AnswerDelta += pieces.Add;

        var run = await loop.RunAsync("hi");

        Assert.Empty(pieces);
        Assert.Equal("", run.Streamed);
        Assert.Equal("quiet answer", run.Unstreamed);   // the caller prints it all
    }

    [Fact]
    public async Task UnstreamedIsTheRemainderWhenTheStreamWasCutShort()
    {
        // A provider whose stream stops early but whose return value is whole.
        var run = new AgentRun(StopReason.Final, "the whole answer", [], TimeSpan.Zero, "the whole");

        Assert.Equal(" answer", run.Unstreamed);
        await Task.CompletedTask;
    }

    [Fact]
    public void UnstreamedFallsBackToEverythingWhenThePreviewDoesNotMatch()
    {
        var run = new AgentRun(StopReason.Final, "actual", [], TimeSpan.Zero, "something else");

        Assert.Equal("actual", run.Unstreamed);
    }
}

public class SseParsingTests
{
    [Fact]
    public void ContentIsPulledOutOfAChunk()
    {
        var payload = """{"choices":[{"delta":{"content":"Hel"}}]}""";
        Assert.Equal("Hel", OpenAiCompatChatProvider.ChunkContent(payload));
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{}}]}""")]                      // role-only opener
    [InlineData("""{"choices":[]}""")]                                  // metadata frame
    [InlineData("""{"id":"x","object":"chat.completion.chunk"}""")]      // keep-alive
    [InlineData("not json at all")]                                      // provider noise
    [InlineData("")]
    public void AnythingWithoutContentIsSkippedRatherThanFatal(string payload)
    {
        Assert.Equal("", OpenAiCompatChatProvider.ChunkContent(payload));
    }
}
