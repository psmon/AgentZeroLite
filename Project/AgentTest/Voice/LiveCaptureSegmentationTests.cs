// ───────────────────────────────────────────────────────────
// Live capture sweep — M0015 / 후속 진행 #3.
//
// Operator brief (paraphrased): I have a TV playing at sufficient volume
// while I record a one-minute note. The continuous stream must split into
// meaningful units (sentences) and save line-by-line. Lecture tempo is
// faster than command tempo. Make a unit test that records, measures, and
// helps us pick the right timing.
//
// What this file does:
//
//   1. Capture 60 seconds from the default mic at 16 kHz mono 16-bit.
//   2. Persist the raw PCM as a WAV (so the operator can re-run this
//      sweep on the same audio without re-recording).
//   3. Replay the captured frames through VoiceSegmenterStage with a
//      sweep of (hangover × pre-roll × threshold) configurations.
//   4. For each configuration, dump:
//        - segment count, mean/std-dev/max segment duration
//        - each segment as a numbered .wav so the operator can listen
//          and judge "is each .wav one sentence?"
//   5. Write a markdown report summarising the sweep so the operator
//      can pick the configuration that best matches sentence-pace
//      segmentation.
//
// This test is SKIPPED by default — it requires a desktop session with a
// working microphone and an audio source playing. To run it, remove the
// Skip attribute (or pass --filter "FullyQualifiedName~RecordOneMinute"
// to the IDE runner, set the Skip="" override locally, etc.).
// ───────────────────────────────────────────────────────────

using System.Diagnostics;
using System.IO;
using System.Text;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.TestKit.Xunit2;
using Agent.Common.Voice;
using Agent.Common.Voice.Streams;
using AgentZeroWpf.Services.Voice;
using NAudio.Wave;
using Xunit.Abstractions;

namespace AgentTest.Voice;

public sealed class LiveCaptureSegmentationTests : TestKit
{
    private const int SampleRate         = 16_000;
    private const int BitsPerSample      = 16;
    private const int Channels           = 1;
    private const int FrameMilliseconds  = 50;
    private const int FrameBytes         = SampleRate / (1000 / FrameMilliseconds) * (BitsPerSample / 8);   // 1600
    private const int CaptureSeconds     = 60;

    private static readonly string OutputRoot = Path.Combine(
        Path.GetTempPath(), "agentzero-voice-tuning");

    private readonly ITestOutputHelper _output;

    public LiveCaptureSegmentationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(OutputRoot);
    }

    [Fact(Skip = "Manual: requires a desktop session, a working mic, and an audio source playing. Remove Skip locally to run.")]
    public void RecordOneMinute_SweepHangoverThresholdAndPreRoll_DumpsSegmentsAndReport()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var sessionDir = Path.Combine(OutputRoot, stamp);
        Directory.CreateDirectory(sessionDir);
        Log($"Session directory : {sessionDir}");

        // ── Stage 1: capture ─────────────────────────────────────────────
        var rawWavPath = Path.Combine(sessionDir, "live-capture.wav");
        var pcm = CaptureToWav(rawWavPath);
        Log($"Captured          : {pcm.Length} pcm bytes ({pcm.Length / (double)(SampleRate * BitsPerSample / 8 * Channels):F2}s)");

        // ── Stage 2: chunk into MicFrames ────────────────────────────────
        var frames = SliceIntoFrames(pcm);
        Log($"Frames generated  : {frames.Count} ({FrameMilliseconds}ms each)");
        var rmsStats = AnalyseRmsDistribution(frames);
        Log($"RMS distribution  : min={rmsStats.Min:F4} p25={rmsStats.P25:F4} median={rmsStats.Median:F4} p75={rmsStats.P75:F4} max={rmsStats.Max:F4}");

        // ── Stage 3: sweep configurations ────────────────────────────────
        var hangoverSweep   = new[] { 12, 16, 24, 40, 60, 80 };       // 0.6 / 0.8 / 1.2 / 2.0 / 3.0 / 4.0 seconds
        var preRollSweep    = new[] { 0.3, 0.5, 1.0 };
        var thresholdSweep  = new[] { 0.012f, 0.025f, 0.040f, 0.060f };

        var report = new StringBuilder();
        report.AppendLine("# Live capture segmentation sweep");
        report.AppendLine();
        report.AppendLine($"- Captured: `{rawWavPath}`");
        report.AppendLine($"- Captured duration: {pcm.Length / (double)(SampleRate * BitsPerSample / 8):F1}s");
        report.AppendLine($"- RMS min/p25/median/p75/max: {rmsStats.Min:F4} / {rmsStats.P25:F4} / {rmsStats.Median:F4} / {rmsStats.P75:F4} / {rmsStats.Max:F4}");
        report.AppendLine();
        report.AppendLine("| threshold | preRoll | hangover (frames / s) | segments | mean dur | std dev | min dur | max dur | total speech |");
        report.AppendLine("|-----------|---------|-----------------------|----------|----------|---------|---------|---------|--------------|");

        foreach (var threshold in thresholdSweep)
        foreach (var preRoll in preRollSweep)
        foreach (var hangover in hangoverSweep)
        {
            var cfg = new VadConfig(
                VadThreshold: threshold,
                PreRollSeconds: preRoll,
                UtteranceHangoverFrames: hangover,
                MaxUtteranceSeconds: 25.0);

            var segments = ReplayThroughSegmenter(frames, cfg);
            var stats = SegmentStats(segments);

            var configKey = $"th{threshold:F3}_pr{preRoll:F1}_hg{hangover:D2}";
            var configDir = Path.Combine(sessionDir, configKey);
            Directory.CreateDirectory(configDir);
            DumpSegments(segments, configDir);

            var hangoverSec = hangover * 0.05;
            report.AppendLine(
                $"| {threshold:F3} | {preRoll:F1}s | {hangover} ({hangoverSec:F1}s) | " +
                $"{stats.Count} | {stats.MeanSeconds:F2}s | {stats.StdDevSeconds:F2}s | " +
                $"{stats.MinSeconds:F2}s | {stats.MaxSeconds:F2}s | {stats.TotalSeconds:F1}s |");
            Log($"th={threshold:F3} pr={preRoll:F1}s hg={hangover}f → {stats.Count} segments, mean={stats.MeanSeconds:F2}s, max={stats.MaxSeconds:F2}s");
        }

        // ── Stage 4: persist report ──────────────────────────────────────
        var reportPath = Path.Combine(sessionDir, "sweep-report.md");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Log($"Report written    : {reportPath}");

        // No hard assertion — this test is for measurement, not pass/fail.
        // The operator's review criterion: open each config's segment WAVs
        // and confirm "each WAV is one sentence" → that hangover/preroll
        // pair is the right tuning.
        Assert.NotEmpty(report.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private byte[] CaptureToWav(string wavPath)
    {
        using var waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = FrameMilliseconds,
            NumberOfBuffers = 3,
        };

        var pcmBuffer = new MemoryStream();
        var captureLock = new object();
        waveIn.DataAvailable += (s, e) =>
        {
            lock (captureLock) { pcmBuffer.Write(e.Buffer, 0, e.BytesRecorded); }
        };

        Log($"Capture starting  : {CaptureSeconds}s @ {SampleRate} Hz");
        var sw = Stopwatch.StartNew();
        waveIn.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(CaptureSeconds));
        waveIn.StopRecording();
        sw.Stop();
        // Drain any in-flight buffers.
        Thread.Sleep(200);

        byte[] pcm;
        lock (captureLock) { pcm = pcmBuffer.ToArray(); }
        Log($"Capture finished  : {sw.ElapsedMilliseconds} ms wall clock");

        File.WriteAllBytes(wavPath, WrapPcmAsWav(pcm, SampleRate, BitsPerSample, Channels));
        return pcm;
    }

    private static List<MicFrame> SliceIntoFrames(byte[] pcm)
    {
        var frames = new List<MicFrame>(pcm.Length / FrameBytes + 1);
        for (int offset = 0; offset + FrameBytes <= pcm.Length; offset += FrameBytes)
        {
            var chunk = new byte[FrameBytes];
            Buffer.BlockCopy(pcm, offset, chunk, 0, FrameBytes);
            frames.Add(new MicFrame(chunk, ComputeRms(chunk)));
        }
        return frames;
    }

    private static float ComputeRms(byte[] frame16Bit)
    {
        double sumSquares = 0;
        var samples = frame16Bit.Length / 2;
        for (int i = 0; i < frame16Bit.Length; i += 2)
        {
            short s = (short)(frame16Bit[i] | (frame16Bit[i + 1] << 8));
            var norm = s / 32768.0;
            sumSquares += norm * norm;
        }
        return (float)Math.Sqrt(sumSquares / Math.Max(1, samples));
    }

    private List<PcmSegment> ReplayThroughSegmenter(List<MicFrame> frames, VadConfig cfg)
    {
        var segments = new List<PcmSegment>();
        var task = Source.From(frames)
            .Via(VoiceSegmenterFlow.Create(cfg))
            .RunForeach(seg => { lock (segments) segments.Add(seg); }, Sys.Materializer());
        task.Wait(TimeSpan.FromSeconds(60));
        return segments;
    }

    private static void DumpSegments(List<PcmSegment> segments, string dir)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var path = Path.Combine(dir, $"segment-{i + 1:D3}-{segments[i].DurationSeconds:F1}s.wav");
            File.WriteAllBytes(path, WrapPcmAsWav(segments[i].Pcm16k, SampleRate, BitsPerSample, Channels));
        }
    }

    private record SegStats(int Count, double MeanSeconds, double StdDevSeconds, double MinSeconds, double MaxSeconds, double TotalSeconds);

    private static SegStats SegmentStats(List<PcmSegment> segments)
    {
        if (segments.Count == 0) return new SegStats(0, 0, 0, 0, 0, 0);
        var durs = segments.Select(s => s.DurationSeconds).ToList();
        var mean = durs.Average();
        var variance = durs.Sum(d => (d - mean) * (d - mean)) / durs.Count;
        return new SegStats(
            Count: segments.Count,
            MeanSeconds: mean,
            StdDevSeconds: Math.Sqrt(variance),
            MinSeconds: durs.Min(),
            MaxSeconds: durs.Max(),
            TotalSeconds: durs.Sum());
    }

    private record RmsStats(double Min, double P25, double Median, double P75, double Max);

    private static RmsStats AnalyseRmsDistribution(List<MicFrame> frames)
    {
        if (frames.Count == 0) return new RmsStats(0, 0, 0, 0, 0);
        var sorted = frames.Select(f => (double)f.Rms).OrderBy(x => x).ToList();
        double Pct(double p)
        {
            var idx = Math.Clamp((int)(p * sorted.Count), 0, sorted.Count - 1);
            return sorted[idx];
        }
        return new RmsStats(sorted[0], Pct(0.25), Pct(0.5), Pct(0.75), sorted[^1]);
    }

    private new void Log(string msg) => _output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    private static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, int bitsPerSample, int channels)
    {
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = pcm.Length;
        var fileSize = 36 + dataSize;
        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(fileSize); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)channels);
        bw.Write(sampleRate); bw.Write(byteRate); bw.Write((short)blockAlign); bw.Write((short)bitsPerSample);
        bw.Write("data"u8); bw.Write(dataSize); bw.Write(pcm);
        return ms.ToArray();
    }
}
