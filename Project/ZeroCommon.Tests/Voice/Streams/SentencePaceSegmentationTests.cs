// ───────────────────────────────────────────────────────────
// Sentence-pace segmentation — M0015 / 후속 진행 #3.
//
// Operator brief: lecture audio comes at faster tempo than a CLI command.
// "명령보다 템포가 더 빠름". A 4-second hangover (Loose's original tuning)
// rides through inter-sentence silence and merges every sentence in the
// minute into one mega-blob. The operator wants line-by-line transcripts,
// one line per sentence.
//
// This file pins the sentence-pace contract with synthetic input: 12
// "sentences" (4s loud each) separated by 1.5s inter-sentence silence,
// totalling ~66s of synthetic audio. With sentence-pace tuning the
// segmenter emits ~12 segments; with paragraph-pace tuning it emits 1.
//
// 50ms WaveIn buffers → 1 frame = 1600 bytes @ 16kHz/16-bit/mono.
// ───────────────────────────────────────────────────────────

using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit.Xunit2;
using Agent.Common.Voice;
using Agent.Common.Voice.Streams;

namespace ZeroCommon.Tests.Voice.Streams;

public sealed class SentencePaceSegmentationTests : TestKit
{
    private const int FrameBytes = 1600;          // 50ms @ 16kHz/16-bit/mono
    private const int FramesPerSecond = 20;       // 1000ms / 50ms

    private const int LoudFramesPerSentence = 80;       // 4 seconds of speech
    private const int QuietFramesBetween    = 30;       // 1.5 seconds of inter-sentence silence
    private const int Sentences             = 12;        // ≈ 66 seconds total

    private static MicFrame Loud()  => new(new byte[FrameBytes], Rms: 0.5f);
    private static MicFrame Quiet() => new(new byte[FrameBytes], Rms: 0.0f);

    [Fact]
    public void Loose_sentence_pace_emits_one_segment_per_sentence()
    {
        // Sentence-pace tuning: hangover < inter-sentence silence so the
        // gap between sentences trips the hangover and emits a segment
        // per sentence. With 30 quiet frames between sentences and a
        // 24-frame hangover, the hangover fires inside the gap.
        var cfg = new VadConfig(
            VadThreshold: 0.1f,
            PreRollSeconds: 0.0,                  // no pre-roll for size determinism
            UtteranceHangoverFrames: 24,          // 1.2 s — sentence pace
            MaxUtteranceSeconds: 0.0);            // cap disabled — we want hangover-driven cuts

        var segments = RunPattern(cfg);

        Assert.Equal(Sentences, segments.Count);
        // Each segment should carry the 80 loud frames + 24 hangover-tail
        // frames = 104 frames (5.2 s). The remaining 6 inter-sentence
        // quiet frames are dropped between utterances.
        var expectedBytesPerSegment = (LoudFramesPerSentence + 24) * FrameBytes;
        foreach (var seg in segments)
        {
            Assert.Equal(expectedBytesPerSegment, seg.Pcm16k.Length);
            Assert.InRange(seg.DurationSeconds, 4.5, 6.0);
        }
    }

    [Fact]
    public void Paragraph_pace_hangover_merges_all_sentences_into_one_blob()
    {
        // The original Loose tuning used 80-frame (4 s) hangover. With
        // 30-frame inter-sentence silence the hangover never trips —
        // every sentence merges. This is the failure mode the operator
        // observed in the live recording. Without the duration cap we'd
        // get one giant blob for the entire minute.
        var cfg = new VadConfig(
            VadThreshold: 0.1f,
            PreRollSeconds: 0.0,
            UtteranceHangoverFrames: 80,          // 4 s — paragraph pace
            MaxUtteranceSeconds: 0.0);

        var segments = RunPattern(cfg);

        Assert.Single(segments);
        // The single segment must span essentially the entire pattern —
        // 12 sentences × (80 loud + 30 quiet) = 1320 frames = 66 s.
        Assert.True(segments[0].DurationSeconds > 60.0,
            $"Mega-blob {segments[0].DurationSeconds:F1}s should swallow the whole minute");
    }

    [Fact]
    public void Duration_cap_prevents_runaway_blob_even_with_long_hangover()
    {
        // Combination of long hangover + duration cap: the cap is the
        // backstop when the hangover is mistuned. With 80-frame hangover
        // and 25 s cap, segments cut at every 25 s boundary (3 segments
        // for a 66 s minute). Better than 1 mega-blob but still coarser
        // than sentence pace — the cap is a safety net, not a substitute
        // for correct hangover.
        var cfg = new VadConfig(
            VadThreshold: 0.1f,
            PreRollSeconds: 0.0,
            UtteranceHangoverFrames: 80,
            MaxUtteranceSeconds: 25.0);

        var segments = RunPattern(cfg);

        Assert.True(segments.Count >= 2,
            $"Cap should split the mega-blob into at least 2 segments, got {segments.Count}");
        foreach (var seg in segments)
        {
            Assert.True(seg.DurationSeconds <= 28.0,
                $"Capped segment {seg.DurationSeconds:F1}s exceeds cap+tolerance");
        }
    }

    [Fact]
    public void Default_loose_profile_produces_sentence_pace_segments()
    {
        // Production wiring check — the Loose profile from
        // VoiceSensitivityProfile must produce sentence-pace segments
        // on this fixture without any further tuning. If a future commit
        // bumps Loose's hangover back to 4 s this test fails immediately.
        var cfg = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose, sensitivityPercent: 75.0);

        var segments = RunPattern(cfg);

        Assert.True(segments.Count >= Sentences - 1,
            $"Loose profile must emit ~{Sentences} segments per minute, got {segments.Count}. " +
            "If Loose hangover was bumped above the inter-sentence silence the segments merge.");
        Assert.True(segments.Count <= Sentences + 2,
            $"Loose profile emitted {segments.Count} segments — sounds over-eager (false splits within sentences). " +
            "Hangover may be too short.");
    }

    private List<PcmSegment> RunPattern(VadConfig cfg)
    {
        var (probe, sink) = this.SourceProbe<MicFrame>()
            .Via(VoiceSegmenterFlow.Create(cfg))
            .ToMaterialized(this.SinkProbe<PcmSegment>(), Keep.Both)
            .Run(Sys.Materializer());

        // Demand more than we'll possibly produce so segments emit as
        // soon as they're ready; we'll collect until either we hit the
        // expected count or the source completes.
        sink.Request(Sentences * 2 + 5);

        for (int s = 0; s < Sentences; s++)
        {
            for (int i = 0; i < LoudFramesPerSentence; i++) probe.SendNext(Loud());
            for (int i = 0; i < QuietFramesBetween; i++) probe.SendNext(Quiet());
        }
        // Trail with extra silence so any pending utterance closes via
        // hangover before the source completes.
        for (int i = 0; i < Math.Max(cfg.UtteranceHangoverFrames + 5, 100); i++)
            probe.SendNext(Quiet());
        probe.SendComplete();

        // Drain the sink until completion. ExpectNextOrComplete returns
        // null when the stream completes.
        var segments = new List<PcmSegment>();
        while (true)
        {
            try
            {
                var seg = sink.ExpectNext(TimeSpan.FromSeconds(2));
                segments.Add(seg);
            }
            catch
            {
                break;
            }
        }
        return segments;
    }
}
