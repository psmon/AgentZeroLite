// ───────────────────────────────────────────────────────────
// SentenceChunkerStage tests — pure logic, headless.
// Verifies the chunking rules: sentence terminators, hard newlines,
// MaxChunkChars cap, MinChunkChars floor, and tail flush on completion.
// ───────────────────────────────────────────────────────────

using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit.Xunit2;
using Agent.Common.Voice.Streams;

namespace ZeroCommon.Tests.Voice.Streams;

public sealed class SentenceChunkerStageTests : TestKit
{
    [Fact]
    public void Single_long_sentence_emits_on_terminator()
    {
        var (src, sink) = CreateProbedFlow(minChunk: 8, maxChunk: 100);
        sink.Request(2);

        // 22 chars then terminator + space.
        src.SendNext("This is a long sentence");
        src.SendNext(". ");
        var chunk = sink.ExpectNext();
        Assert.Equal("This is a long sentence.", chunk);

        src.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void Tiny_sentence_below_min_does_not_split_until_min_passed()
    {
        var (src, sink) = CreateProbedFlow(minChunk: 16, maxChunk: 100);
        sink.Request(2);

        // 4 chars + terminator — under min — keep buffering.
        src.SendNext("Yes. ");
        src.SendNext("And then more text follows. ");
        // First chunk should be the COMBINED text since "Yes." was below min.
        var chunk = sink.ExpectNext();
        Assert.Contains("Yes.", chunk);
        Assert.Contains("And then more", chunk);

        src.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void Hard_newline_emits_regardless_of_length()
    {
        var (src, sink) = CreateProbedFlow(minChunk: 32, maxChunk: 100);
        sink.Request(2);

        src.SendNext("Hi\n");
        var chunk = sink.ExpectNext();
        Assert.Equal("Hi", chunk);

        src.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void Tail_flushes_on_upstream_complete_even_without_terminator()
    {
        var (src, sink) = CreateProbedFlow(minChunk: 8, maxChunk: 100);
        sink.Request(2);

        src.SendNext("No terminator here");
        src.SendComplete();
        var chunk = sink.ExpectNext();
        Assert.Equal("No terminator here", chunk);
        sink.ExpectComplete();
    }

    [Fact]
    public void Multi_sentence_input_emits_separate_chunks()
    {
        var (src, sink) = CreateProbedFlow(minChunk: 8, maxChunk: 200);
        sink.Request(4);

        src.SendNext("First sentence here. ");
        var c1 = sink.ExpectNext();
        Assert.Equal("First sentence here.", c1);

        src.SendNext("Second sentence here? ");
        var c2 = sink.ExpectNext();
        Assert.Equal("Second sentence here?", c2);

        src.SendNext("Third sentence here! ");
        var c3 = sink.ExpectNext();
        Assert.Equal("Third sentence here!", c3);

        src.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void MaxChunk_caps_runaway_sentence_at_whitespace()
    {
        var (src, sink) = CreateProbedFlow(minChunk: 8, maxChunk: 30);
        sink.Request(2);

        // 50-char sentence with no period — split at last whitespace under 30.
        src.SendNext("aaaa bbbb cccc dddd eeee ffff gggg");
        // The cap fires at the next OnPush after buffer ≥ 30; our
        // FindChunkEnd then walks back to the last whitespace ≤ buffer end.
        var chunk = sink.ExpectNext();
        Assert.True(chunk.Length > 0);
        Assert.True(chunk.Length <= 30, $"Chunk length {chunk.Length} exceeded MaxChunkChars 30");

        src.SendComplete();
        // There may be a tail chunk for the trailing tokens.
        try { sink.ExpectNext(); } catch { /* tail not always present */ }
        sink.ExpectComplete();
    }

    private (TestPublisher.Probe<string> source, TestSubscriber.Probe<string> sink)
        CreateProbedFlow(int minChunk, int maxChunk)
    {
        var (pub, sub) = this.SourceProbe<string>()
            .Via(SentenceChunkerFlow.Create(minChunk, maxChunk))
            .ToMaterialized(this.SinkProbe<string>(), Keep.Both)
            .Run(Sys.Materializer());
        return (pub, sub);
    }
}
