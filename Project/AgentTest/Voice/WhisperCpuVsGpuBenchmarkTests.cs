// ───────────────────────────────────────────────────────────
// Whisper CPU vs GPU (Vulkan) micro-benchmark.
//
// Reuses TtsSttRoundTripTests' synth + decode plumbing but holds the input
// fixed and runs STT twice — once on CPU, once on GPU/Vulkan — then prints
// a side-by-side table with timings, RT factor, and transcript similarity.
// Useful as a quick smoke test that the Vulkan runtime actually loaded
// (look for "useGpu=True device=N" in the AppLogger output).
//
// xUnit doesn't have a "benchmark" mode — this is just a [Fact] that logs
// numbers. It passes as long as the GPU run produces a transcript at all
// (even a wrong one); accuracy is reported but not asserted, since GPU and
// CPU may pick slightly different decodings on the same input.
// ───────────────────────────────────────────────────────────

using System.Diagnostics;
using System.IO;
using System.Text;
using AgentZeroWpf.Services.Voice;
using Xunit.Abstractions;

namespace AgentTest.Voice;

public class WhisperCpuVsGpuBenchmarkTests
{
    private const string Input = "안녕하세요 오늘의 날씨는 화창하고 맑습니다.";
    private const string Language = "ko";
    private const string WhisperModelSize = "small"; // medium is overkill for a smoke bench

    private static readonly string OutputDir = Path.Combine(
        Path.GetTempPath(), "agentzero-whisper-bench");

    private readonly ITestOutputHelper _output;

    public WhisperCpuVsGpuBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(OutputDir);
    }

    [Fact]
    public async Task Cpu_vs_GpuVulkan_korean_weather_sentence()
    {
        var bar = new string('═', 70);
        Log(bar);
        Log("Whisper CPU vs GPU (Vulkan) benchmark");
        Log(bar);
        Log($"input              : \"{Input}\"");
        Log($"language           : {Language}");
        Log($"model              : {WhisperModelSize}");
        Log($"OS                 : {Environment.OSVersion}");

        // ── GPU enumeration: log what the heuristic would pick ──
        var adapters = GpuEnumerator.Enumerate();
        Log("");
        Log($"WMI adapters       : {adapters.Count}");
        foreach (var a in adapters)
        {
            var vramGb = a.VramBytes / (1024.0 * 1024.0 * 1024.0);
            Log($"  #{a.Index} {a.Name} · {vramGb:F2} GB · vendorScore={a.VendorScore}");
        }
        var bestIdx = GpuEnumerator.PickBestIndex(adapters);
        Log($"auto-best index    : {bestIdx}");
        Log("");

        // ── TTS once, share PCM across both runs ──
        var tts = new WindowsTts();
        var ttsSw = Stopwatch.StartNew();
        var wav = await tts.SynthesizeAsync(Input, voice: "");
        ttsSw.Stop();
        Assert.NotEmpty(wav);

        var pcm = WavToPcm.To16kMono(wav);
        Assert.NotEmpty(pcm);
        var pcmSeconds = pcm.Length / 32_000.0;

        Log($"TTS synth          : {ttsSw.ElapsedMilliseconds} ms · {wav.Length} bytes");
        Log($"PCM length         : {pcm.Length} bytes (~{pcmSeconds:F2} s audio)");
        Log("");

        // Persist what STT will see, for after-the-fact listening.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var pcmWavPath = Path.Combine(OutputDir, $"{stamp}-bench-input-16k.wav");
        await File.WriteAllBytesAsync(pcmWavPath, WrapPcmAsWav(pcm, 16_000, 16, 1));
        Log($"STT-input WAV      : {pcmWavPath}");
        Log("");

        // ── Run #1: CPU ──
        WhisperLocalStt.Unload(); // force fresh factory
        var cpuRun = await RunOne(pcm, useGpu: false, gpuIndex: 0, label: "CPU");

        // ── Run #2: GPU / Vulkan auto ──
        WhisperLocalStt.Unload();
        var gpuRun = await RunOne(pcm, useGpu: true, gpuIndex: -1, label: "GPU/Vulkan");

        // ── Summary table ──
        Log("");
        Log("─── SUMMARY ──────────────────────────────────────────────");
        Log($"{"backend",-12} {"prep ms",10} {"transcribe ms",16} {"realtime",10} {"sim",8}");
        Log($"{cpuRun.Label,-12} {cpuRun.PrepMs,10} {cpuRun.TranscribeMs,16} {cpuRun.Rt,10:F2} {cpuRun.Similarity,8:P0}");
        Log($"{gpuRun.Label,-12} {gpuRun.PrepMs,10} {gpuRun.TranscribeMs,16} {gpuRun.Rt,10:F2} {gpuRun.Similarity,8:P0}");

        if (cpuRun.TranscribeMs > 0)
        {
            var speedup = cpuRun.TranscribeMs / (double)Math.Max(1, gpuRun.TranscribeMs);
            Log($"GPU speedup        : {speedup:F2}x (transcribe phase only)");
        }
        Log("");
        Log($"CPU transcript     : \"{cpuRun.Transcript}\"");
        Log($"GPU transcript     : \"{gpuRun.Transcript}\"");
        Log(bar);

        // Sanity: both runs must produce non-empty output. We don't assert
        // exact equality — Whisper's beam decoding can diverge between
        // backends on the same input.
        Assert.False(string.IsNullOrWhiteSpace(cpuRun.Transcript), "CPU transcript empty");
        Assert.False(string.IsNullOrWhiteSpace(gpuRun.Transcript), "GPU transcript empty");
    }

    private async Task<RunResult> RunOne(byte[] pcm, bool useGpu, int gpuIndex, string label)
    {
        Log($"── {label} ──");
        var stt = new WhisperLocalStt(WhisperModelSize)
        {
            UseGpu = useGpu,
            GpuDeviceIndex = gpuIndex,
        };

        var prepSw = Stopwatch.StartNew();
        var ready = await stt.EnsureReadyAsync(new Progress<string>(s => Log($"  [prep] {s}")));
        prepSw.Stop();
        Assert.True(ready, $"{label} STT not ready");
        Log($"  prep          : {prepSw.ElapsedMilliseconds} ms");

        var sw = Stopwatch.StartNew();
        var transcript = (await stt.TranscribeAsync(pcm, Language)) ?? "";
        sw.Stop();

        var pcmSeconds = pcm.Length / 32_000.0;
        var rt = pcmSeconds > 0 ? sw.ElapsedMilliseconds / 1000.0 / pcmSeconds : 0;
        var similarity = ComputeSimilarity(Input, transcript);

        Log($"  transcribe    : {sw.ElapsedMilliseconds} ms · {rt:F2}x realtime");
        Log($"  transcript    : \"{transcript}\"");
        Log($"  similarity    : {similarity:P1}");
        Log("");

        return new RunResult(label, prepSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, rt, transcript, similarity);
    }

    private record RunResult(
        string Label,
        long PrepMs,
        long TranscribeMs,
        double Rt,
        string Transcript,
        double Similarity);

    private static double ComputeSimilarity(string a, string b)
    {
        var an = Normalize(a);
        var bn = Normalize(b);
        if (an.Length == 0 && bn.Length == 0) return 1.0;
        var dist = LevenshteinDistance(an, bn);
        var maxLen = Math.Max(an.Length, bn.Length);
        return maxLen == 0 ? 1.0 : 1.0 - dist / (double)maxLen;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

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

    private void Log(string line)
    {
        try { _output.WriteLine(line); } catch { }
        Console.WriteLine(line);
    }
}
