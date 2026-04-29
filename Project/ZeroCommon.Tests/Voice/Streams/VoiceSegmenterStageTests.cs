// ───────────────────────────────────────────────────────────
// VoiceSegmenterStage tests — headless, drives the GraphStage with
// synthetic MicFrame fixtures via Akka.Streams.TestKit's
// TestSource.Probe / TestSink.Probe.
//
// Scope: VAD threshold + utterance-end hangover + pre-roll seeding.
// Out of scope: STT (separate worker actor), playback (P2/P3).
// ───────────────────────────────────────────────────────────

using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit.Xunit2;
using Agent.Common.Voice.Streams;

namespace ZeroCommon.Tests.Voice.Streams;

public sealed class VoiceSegmenterStageTests : TestKit
{
    private static MicFrame Loud(int frameBytes = 1600) =>
        new(Pcm16k: new byte[frameBytes], Rms: 0.5f);   // above any reasonable threshold

    private static MicFrame Quiet(int frameBytes = 1600) =>
        new(Pcm16k: new byte[frameBytes], Rms: 0.0f);   // below threshold

    private static VadConfig Cfg(double preRollSec = 1.0, int hangoverFrames = 4, float threshold = 0.1f)
        => new(VadThreshold: threshold, PreRollSeconds: preRollSec, UtteranceHangoverFrames: hangoverFrames);

    [Fact]
    public void Quiet_only_emits_no_segment()
    {
        var (probe, sink) = CreateProbedFlow(Cfg(hangoverFrames: 2));

        sink.Request(8);
        for (int i = 0; i < 20; i++) probe.SendNext(Quiet());
        probe.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void Loud_then_quiet_emits_one_segment_after_hangover()
    {
        var cfg = Cfg(preRollSec: 0.0, hangoverFrames: 3);
        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(2);
        // 5 loud frames → utterance starts and accumulates 5 × 1600 = 8000 bytes.
        for (int i = 0; i < 5; i++) probe.SendNext(Loud());
        // 3 quiet frames → reaches hangover threshold; segment emits on the 3rd.
        for (int i = 0; i < 3; i++) probe.SendNext(Quiet());

        var seg = sink.ExpectNext();
        // 5 loud + 3 trailing-silence frames included.
        Assert.Equal(8 * 1600, seg.Pcm16k.Length);
        Assert.True(seg.DurationSeconds > 0.0);
    }

    [Fact]
    public void PreRoll_seeds_utterance_buffer()
    {
        // 1 s preroll = 50 frames × 1600 bytes (assuming 50 fps × 32 bytes).
        // To keep the fixture small use 0.1 s preroll = 5 × 320-byte frames.
        var cfg = new VadConfig(VadThreshold: 0.1f, PreRollSeconds: 0.1, UtteranceHangoverFrames: 2);
        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(2);
        // Frame size 320 = 10 ms at 16 kHz/16-bit → 0.1 s = 5 frames.
        // Send 5 quiet frames (these seed the pre-roll ring without emitting).
        for (int i = 0; i < 5; i++) probe.SendNext(Quiet(frameBytes: 320));
        // Trip VAD with 2 loud frames…
        for (int i = 0; i < 2; i++) probe.SendNext(Loud(frameBytes: 320));
        // …then 2 quiet frames to close out via hangover.
        for (int i = 0; i < 2; i++) probe.SendNext(Quiet(frameBytes: 320));

        var seg = sink.ExpectNext();
        // Expected = 5 preRoll + 2 loud + 2 trailing silence = 9 × 320 = 2880 bytes.
        Assert.Equal(9 * 320, seg.Pcm16k.Length);
    }

    [Fact]
    public void Brief_mid_utterance_pause_does_not_split()
    {
        // Hangover = 5 frames. A 3-frame quiet gap sits well under it, so the
        // segment must NOT split.
        var cfg = Cfg(preRollSec: 0.0, hangoverFrames: 5);
        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(2);
        for (int i = 0; i < 4; i++) probe.SendNext(Loud());          // first burst
        for (int i = 0; i < 3; i++) probe.SendNext(Quiet());         // brief pause
        for (int i = 0; i < 4; i++) probe.SendNext(Loud());          // second burst
        for (int i = 0; i < 5; i++) probe.SendNext(Quiet());         // hangover triggers

        var seg = sink.ExpectNext();
        // 4 + 3 + 4 + 5 = 16 frames included.
        Assert.Equal(16 * 1600, seg.Pcm16k.Length);
    }

    [Fact]
    public void Two_separated_utterances_emit_two_segments()
    {
        var cfg = Cfg(preRollSec: 0.0, hangoverFrames: 3);
        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(3);
        for (int i = 0; i < 4; i++) probe.SendNext(Loud());          // utterance 1
        for (int i = 0; i < 3; i++) probe.SendNext(Quiet());         // closes #1
        for (int i = 0; i < 5; i++) probe.SendNext(Quiet());         // gap (segmenter idle)
        for (int i = 0; i < 4; i++) probe.SendNext(Loud());          // utterance 2
        for (int i = 0; i < 3; i++) probe.SendNext(Quiet());         // closes #2

        var s1 = sink.ExpectNext();
        var s2 = sink.ExpectNext();
        Assert.Equal(7 * 1600, s1.Pcm16k.Length);
        Assert.Equal(7 * 1600, s2.Pcm16k.Length);
    }

    private (TestPublisher.Probe<MicFrame> source, TestSubscriber.Probe<PcmSegment> sink)
        CreateProbedFlow(VadConfig cfg)
    {
        var (pub, sub) = this.SourceProbe<MicFrame>()
            .Via(VoiceSegmenterFlow.Create(cfg))
            .ToMaterialized(this.SinkProbe<PcmSegment>(), Keep.Both)
            .Run(Sys.Materializer());
        return (pub, sub);
    }
}
