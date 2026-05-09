using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit.Xunit2;
using Agent.Common.Voice;
using Agent.Common.Voice.Streams;

namespace ZeroCommon.Tests.Voice;

// ───────────────────────────────────────────────────────────
// VoiceSensitivityProfile tests — M0015.
//
// Operator brief: lecture-style audio (speaker far from mic, ambient
// noise, quiet stretches) is missed by the current Strict tuning that
// was calibrated for a close-mic personal use case. A Loose profile
// must accept softer frames while still rejecting pure silence.
//
// Scope here is the profile→VadConfig math + segmenter behaviour
// driven from each profile. STT model selection (medium vs small)
// belongs to a separate concern and is not exercised here.
// ───────────────────────────────────────────────────────────
public sealed class VoiceSensitivityProfileTests : TestKit
{
    // Synthetic frame fixtures. The numeric Rms values mimic real-world
    // bands — 0.5 = clear personal voice, 0.04 = soft distant lecturer,
    // 0.0 = digital silence.
    private static MicFrame Frame(float rms, int bytes = 1600) =>
        new(Pcm16k: new byte[bytes], Rms: rms);

    private const float DistantLecturerRms = 0.04f;
    private const float ClosePersonalRms   = 0.5f;
    private const float SilenceRms         = 0.0f;

    // A representative slider value the operator already runs in the UI
    // (stored VadThreshold=25 ⇒ slider=75 ⇒ "high sensitivity"). Tests
    // pin against this value so the curves stay calibrated for the same
    // user-facing setting.
    private const double TypicalSensitivityPercent = 75.0;

    [Fact]
    public void Strict_threshold_matches_legacy_sensitivity_curve()
    {
        // Strict must reproduce the historical (100-sens)/400 floor 0.005
        // curve byte-for-byte so existing personal-mic users see no shift.
        var cfg = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict,
            TypicalSensitivityPercent);

        Assert.Equal(0.0625f, cfg.VadThreshold, precision: 4);
        Assert.Equal(1.0,     cfg.PreRollSeconds);
        Assert.Equal(40,      cfg.UtteranceHangoverFrames);
    }

    [Fact]
    public void Loose_threshold_is_strictly_below_strict_at_same_sensitivity()
    {
        var strict = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, TypicalSensitivityPercent);
        var loose  = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose,  TypicalSensitivityPercent);

        // Loose must accept softer audio than Strict at the same slider —
        // this part of the curve was always the point of Loose.
        Assert.True(loose.VadThreshold < strict.VadThreshold,
            $"Loose threshold {loose.VadThreshold} should be below strict {strict.VadThreshold}");
    }

    [Fact]
    public void Loose_uses_sentence_pace_timing_relative_to_strict()
    {
        // M0015 / 후속 진행 #3 — Loose is now tuned for sentence-pace
        // segmentation (lecture / note-taking surfaces). Operator
        // feedback: "lecture tempo is faster than command tempo".
        // A long hangover merges sentences into mega-blobs; a long
        // pre-roll inflates UI latency for line-by-line note saving.
        // So Loose must have SHORTER hangover and SHORTER pre-roll
        // than Strict, not longer.
        var strict = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, TypicalSensitivityPercent);
        var loose  = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose,  TypicalSensitivityPercent);

        Assert.True(loose.UtteranceHangoverFrames < strict.UtteranceHangoverFrames,
            $"Loose hangover {loose.UtteranceHangoverFrames} must be below strict {strict.UtteranceHangoverFrames} for sentence pace");

        Assert.True(loose.PreRollSeconds < strict.PreRollSeconds,
            $"Loose preroll {loose.PreRollSeconds}s must be below strict {strict.PreRollSeconds}s for line-by-line latency");

        // Hangover for Loose must sit between intra-sentence micro-pause
        // (~0.3s) and natural sentence boundary (~0.8–1.5s) so the gap
        // between sentences trips it but the gap between words doesn't.
        var hangoverSeconds = loose.UtteranceHangoverFrames * 0.05;
        Assert.InRange(hangoverSeconds, 0.5, 2.0);
    }

    [Fact]
    public void Both_profiles_respect_minimum_threshold_floor()
    {
        // At 100% UI sensitivity Strict floors at 0.005 (origin-proven).
        // Loose mustn't dip below the same floor — a 0 threshold turns
        // every frame into "speaking" and floods the pipeline.
        var strict = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, sensitivityPercent: 100.0);
        var loose  = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose,  sensitivityPercent: 100.0);

        Assert.True(strict.VadThreshold >= 0.005f);
        Assert.True(loose.VadThreshold  >= 0.005f);
    }

    [Fact]
    public void Loose_floor_is_strictly_above_strict_floor_at_max_sensitivity()
    {
        // M0015 / 후속 진행 #2 — at sens=100 the original implementation
        // collapsed both curves to 0.005, defeating Loose's whole point.
        // Loose's floor must now sit above Strict's so the slider's max
        // position still discriminates the two profiles meaningfully.
        var strict = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, sensitivityPercent: 100.0);
        var loose  = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose,  sensitivityPercent: 100.0);

        Assert.True(loose.VadThreshold > strict.VadThreshold,
            $"Loose floor {loose.VadThreshold} must be above strict floor {strict.VadThreshold} at max sensitivity");
        Assert.True(loose.VadThreshold >= 0.012f,
            $"Loose floor {loose.VadThreshold} must be at least 0.012 to reject typical room ambient noise");
    }

    [Fact]
    public void Loose_floor_still_admits_distant_lecturer_rms()
    {
        // The whole point of Loose is to pick up the 0.04-band distant
        // speaker. Raising the floor to 0.012 must NOT regress that
        // fixture — 0.04 must still be above the new floor.
        var loose = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose, sensitivityPercent: 100.0);
        Assert.True(DistantLecturerRms > loose.VadThreshold,
            $"Distant lecturer RMS {DistantLecturerRms} must still trip Loose floor {loose.VadThreshold}");
    }

    [Fact]
    public void Profiles_set_max_utterance_cap()
    {
        // The cap must be enabled (>0) for both profiles so the legacy
        // VoiceCaptureService and the streaming VoiceSegmenterStage both
        // get protected from runaway utterances.
        var strict = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, sensitivityPercent: 75.0);
        var loose  = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose,  sensitivityPercent: 75.0);

        Assert.True(strict.MaxUtteranceSeconds > 0,
            "Strict profile must enable the duration cap");
        Assert.True(loose.MaxUtteranceSeconds > 0,
            "Loose profile must enable the duration cap");
        // Strict permits longer monologues than Loose — close mic users
        // tend to dictate cleanly, lecture audio is more likely to stack
        // ambient noise into one runaway segment so Loose caps tighter.
        Assert.True(loose.MaxUtteranceSeconds <= strict.MaxUtteranceSeconds);
    }

    [Fact]
    public void Strict_rejects_distant_lecturer_frames()
    {
        // Distant frames sit at 0.04 RMS. Strict@75 = 0.0625 threshold
        // so the segmenter must NOT emit anything — this is the failure
        // mode the operator hit in the field (강의중 인식 안 됨).
        var cfg = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, TypicalSensitivityPercent);
        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(4);
        for (int i = 0; i < 30; i++) probe.SendNext(Frame(DistantLecturerRms));
        probe.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void Loose_accepts_distant_lecturer_frames()
    {
        // Same fixture, Loose profile — must trip VAD and emit one segment
        // once the trailing silence reaches the (longer) hangover.
        var cfg = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose, TypicalSensitivityPercent);
        // Override hangover for test speed; keep the threshold which is
        // the property under test.
        cfg = cfg with { UtteranceHangoverFrames = 3, PreRollSeconds = 0.0 };

        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(2);
        for (int i = 0; i < 5; i++) probe.SendNext(Frame(DistantLecturerRms)); // utterance
        for (int i = 0; i < 3; i++) probe.SendNext(Frame(SilenceRms));         // hangover

        var seg = sink.ExpectNext();
        Assert.Equal(8 * 1600, seg.Pcm16k.Length);
    }

    [Fact]
    public void Loose_still_rejects_pure_silence()
    {
        // The danger of lowering the threshold is false-positive segments
        // on quiet rooms. Loose must NOT trigger on Rms=0 frames.
        var cfg = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Loose, TypicalSensitivityPercent);
        cfg = cfg with { UtteranceHangoverFrames = 3, PreRollSeconds = 0.0 };

        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(2);
        for (int i = 0; i < 30; i++) probe.SendNext(Frame(SilenceRms));
        probe.SendComplete();
        sink.ExpectComplete();
    }

    [Fact]
    public void Strict_admits_close_personal_voice_unchanged()
    {
        // Sanity: clear personal voice (Rms=0.5) must still segment under
        // Strict — i.e. the Loose addition didn't regress the original
        // user case.
        var cfg = VoiceSensitivityProfile.BuildVadConfig(
            VoiceSensitivityLevel.Strict, TypicalSensitivityPercent);
        cfg = cfg with { UtteranceHangoverFrames = 3, PreRollSeconds = 0.0 };

        var (probe, sink) = CreateProbedFlow(cfg);

        sink.Request(2);
        for (int i = 0; i < 5; i++) probe.SendNext(Frame(ClosePersonalRms));
        for (int i = 0; i < 3; i++) probe.SendNext(Frame(SilenceRms));

        var seg = sink.ExpectNext();
        Assert.Equal(8 * 1600, seg.Pcm16k.Length);
    }

    [Fact]
    public void Profile_round_trips_through_string_token()
    {
        // VoiceSettings persists profile as a string for JSON stability;
        // unknown / empty / case-folded values must default back to Strict
        // so the user is never silently switched into Loose.
        Assert.Equal(VoiceSensitivityLevel.Strict, VoiceSensitivityProfile.Parse(""));
        Assert.Equal(VoiceSensitivityLevel.Strict, VoiceSensitivityProfile.Parse("Strict"));
        Assert.Equal(VoiceSensitivityLevel.Strict, VoiceSensitivityProfile.Parse("STRICT"));
        Assert.Equal(VoiceSensitivityLevel.Loose,  VoiceSensitivityProfile.Parse("Loose"));
        Assert.Equal(VoiceSensitivityLevel.Loose,  VoiceSensitivityProfile.Parse("loose"));
        Assert.Equal(VoiceSensitivityLevel.Strict, VoiceSensitivityProfile.Parse("garbage"));
    }

    [Fact]
    public void Auto_resolves_to_loose_in_agent_mode()
    {
        // M0015 후속 — when the user opts into "Auto", AgentBot's AiMode
        // (long-form agent interaction, often off-mic) gets Loose so a
        // distant or quiet voice still trips VAD.
        Assert.Equal(
            VoiceSensitivityLevel.Loose,
            VoiceSensitivityProfile.ResolveAuto("Auto", isAgentMode: true));
    }

    [Fact]
    public void Auto_resolves_to_strict_in_chat_mode()
    {
        // ChatMode (quick reply, close mic) keeps Strict — the original
        // origin-proven tuning that minimises false positives.
        Assert.Equal(
            VoiceSensitivityLevel.Strict,
            VoiceSensitivityProfile.ResolveAuto("Auto", isAgentMode: false));
    }

    [Fact]
    public void Explicit_profile_wins_over_mode_in_resolver()
    {
        // If the user has explicitly chosen Strict or Loose, Auto-style
        // mode resolution must NOT override their choice.
        Assert.Equal(
            VoiceSensitivityLevel.Strict,
            VoiceSensitivityProfile.ResolveAuto("Strict", isAgentMode: true));
        Assert.Equal(
            VoiceSensitivityLevel.Loose,
            VoiceSensitivityProfile.ResolveAuto("Loose",  isAgentMode: false));
    }

    [Fact]
    public void Resolver_is_case_insensitive_for_auto_token()
    {
        Assert.Equal(
            VoiceSensitivityLevel.Loose,
            VoiceSensitivityProfile.ResolveAuto("auto", isAgentMode: true));
        Assert.Equal(
            VoiceSensitivityLevel.Loose,
            VoiceSensitivityProfile.ResolveAuto("AUTO", isAgentMode: true));
    }

    [Fact]
    public void Resolver_falls_back_to_strict_on_unknown_token()
    {
        // Unknown tokens fall to Strict regardless of mode — same safety
        // posture as Parse(): never silently route a confused user into
        // Loose.
        Assert.Equal(
            VoiceSensitivityLevel.Strict,
            VoiceSensitivityProfile.ResolveAuto("garbage", isAgentMode: true));
        Assert.Equal(
            VoiceSensitivityLevel.Strict,
            VoiceSensitivityProfile.ResolveAuto("",        isAgentMode: true));
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
