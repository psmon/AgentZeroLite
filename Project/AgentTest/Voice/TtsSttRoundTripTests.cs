// ───────────────────────────────────────────────────────────
// TTS↔STT round-trip tests — pure-model, file-based, no Akka, no streams.
//
// Given : a known input string
// When  : run TTS (Windows SAPI) → save WAV → resample → run STT (Whisper.net)
// Then  : the recognised text matches the input
//
// Goal: a stable harness for measuring "how much of the input survives the
// TTS+STT round-trip" so we can compare hallucination rates as we tune
// rate, padding, model size, voice, language, etc. Console output is
// deliberately verbose — every parameter, byte count, ms timing, and
// edit distance is dumped so the test becomes a record of the round-trip
// quality, not just a pass/fail.
//
// Configuration:
//   STT — Whisper.net medium model (downloaded on first run, ~1.5 GB,
//         cached at %USERPROFILE%/.ollama/models/agentzero/whisper/)
//   TTS — Windows SAPI default voice (Microsoft Heami if Korean OS, else
//         system default)
//
// File-based, whole-blob: WAV is synthesised in full → resampled in full →
// fed to STT in full. No streaming, no chunking. This is the cleanest
// reference; the streaming path's quality is then measured against this.
// ───────────────────────────────────────────────────────────

using System.Diagnostics;
using System.IO;
using System.Text;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;
using Xunit.Abstractions;

namespace AgentTest.Voice;

public class TtsSttRoundTripTests
{
    private const string WhisperModelSize = "medium";
    private static readonly string OutputDir = Path.Combine(
        Path.GetTempPath(), "agentzero-tts-stt-tests");

    private readonly ITestOutputHelper _output;

    public TtsSttRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(OutputDir);
    }

    // ── Test cases (Korean + English, varying length) ────────────────────

    [Fact]
    public Task Korean_short_안녕하세요() =>
        RunRoundTrip("안녕하세요", language: "ko", caseId: "ko-short");

    [Fact]
    public Task Korean_question_today_weather() =>
        RunRoundTrip("오늘의 날씨는 어때?", language: "ko", caseId: "ko-question");

    [Fact]
    public Task Korean_long_multi_clause() =>
        RunRoundTrip(
            "내일의 날씨는 말고 모래의날씨는 흐리고 그리고 주간내내 비올예정입니다.",
            language: "ko",
            caseId: "ko-long");

    [Fact]
    public Task English_short_hello() =>
        RunRoundTrip("hello", language: "en", caseId: "en-short");

    [Fact]
    public Task English_question_how_are_you() =>
        RunRoundTrip("how are you?", language: "en", caseId: "en-question");

    // ── Round-trip core ──────────────────────────────────────────────────

    private async Task RunRoundTrip(string input, string language, string caseId)
    {
        var bar = new string('═', 70);
        Log(bar);
        Log($"Round-trip case: {caseId}");
        Log(bar);
        Log($"input              : \"{input}\"");
        Log($"language           : {language}");
        Log($"input chars        : {input.Length}");
        Log($"TTS provider       : WindowsTTS (SAPI default voice)");
        Log($"STT provider       : WhisperLocal (model={WhisperModelSize})");
        Log($"OS                 : {Environment.OSVersion}");
        Log($".NET               : {Environment.Version}");
        Log("");

        // ── Stage 1: TTS ──
        var tts = new WindowsTts();
        var availableVoices = await tts.GetAvailableVoicesAsync();
        var voicePreview = string.Join(", ", availableVoices.Take(5));
        Log($"TTS voices available: {voicePreview}{(availableVoices.Count > 5 ? $" (+{availableVoices.Count - 5} more)" : "")}");

        var ttsSw = Stopwatch.StartNew();
        var wav = await tts.SynthesizeAsync(input, voice: "");
        ttsSw.Stop();

        Assert.NotNull(wav);
        Assert.NotEmpty(wav);

        Log($"[STAGE 1/3] TTS    : {ttsSw.ElapsedMilliseconds} ms · {wav.Length} bytes");

        // Persist the synthesised WAV so the user can listen / inspect after
        // the test run regardless of pass/fail.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var wavPath = Path.Combine(OutputDir, $"{stamp}-{caseId}-tts.wav");
        await File.WriteAllBytesAsync(wavPath, wav);
        Log($"WAV saved          : {wavPath}");

        // ── Stage 2: WAV → PCM 16k mono ──
        var decodeSw = Stopwatch.StartNew();
        var pcm = WavToPcm.To16kMono(wav);
        decodeSw.Stop();

        Assert.NotEmpty(pcm);
        var pcmSeconds = pcm.Length / 32_000.0;
        Log($"[STAGE 2/3] decode : {decodeSw.ElapsedMilliseconds} ms · {pcm.Length} pcm bytes (~{pcmSeconds:F2}s audio)");

        var (peak, rms) = MeasurePcm16(pcm);
        var peakDb = peak > 0 ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
        var rmsDb  = rms  > 0 ? 20.0 * Math.Log10(rms)  : double.NegativeInfinity;
        Log($"PCM level          : peak={peakDb:F1} dBFS · rms={rmsDb:F1} dBFS");

        // Save the resampled PCM as a WAV too — what STT actually saw.
        var pcmWavPath = Path.Combine(OutputDir, $"{stamp}-{caseId}-stt-input-16k.wav");
        await File.WriteAllBytesAsync(pcmWavPath, WrapPcmAsWav(pcm, 16_000, 16, 1));
        Log($"STT-input WAV saved: {pcmWavPath}");

        // ── Stage 3: STT ──
        var stt = new WhisperLocalStt(WhisperModelSize);
        var prepSw = Stopwatch.StartNew();
        var ready = await stt.EnsureReadyAsync(new Progress<string>(s => Log($"  [STT-prep] {s}")));
        prepSw.Stop();
        Log($"STT prep           : {prepSw.ElapsedMilliseconds} ms · ready={ready}");

        if (!ready)
        {
            Log("✗ STT not ready (model download / load failed) — see prep messages above");
            Assert.Fail("Whisper model not ready");
            return;
        }

        var sttSw = Stopwatch.StartNew();
        var transcript = (await stt.TranscribeAsync(pcm, language)) ?? string.Empty;
        sttSw.Stop();

        var rtFactor = pcmSeconds > 0 ? sttSw.ElapsedMilliseconds / 1000.0 / pcmSeconds : 0;
        Log($"[STAGE 3/3] STT    : {sttSw.ElapsedMilliseconds} ms · {rtFactor:F2}x realtime");
        Log($"transcript         : \"{transcript}\"");
        Log($"transcript chars   : {transcript.Length}");

        // ── Comparison ──
        Log("");
        Log("─── COMPARISON ────────────────────────────────────────────");
        var inputNorm  = Normalize(input);
        var outputNorm = Normalize(transcript);
        var exactMatch = string.Equals(inputNorm, outputNorm, StringComparison.Ordinal);
        var editDist   = LevenshteinDistance(inputNorm, outputNorm);
        var maxLen     = Math.Max(inputNorm.Length, outputNorm.Length);
        var similarity = maxLen == 0 ? 1.0 : 1.0 - editDist / (double)maxLen;

        Log($"normalised input   : \"{inputNorm}\"");
        Log($"normalised output  : \"{outputNorm}\"");
        Log($"exact match        : {exactMatch}");
        Log($"edit distance      : {editDist}");
        Log($"similarity         : {similarity:P1} (Levenshtein-normalised)");

        // ── Total budget ──
        var totalMs = ttsSw.ElapsedMilliseconds + decodeSw.ElapsedMilliseconds + sttSw.ElapsedMilliseconds;
        Log("");
        Log($"TIMING TOTAL       : {totalMs} ms ({ttsSw.ElapsedMilliseconds} synth + {decodeSw.ElapsedMilliseconds} decode + {sttSw.ElapsedMilliseconds} STT)");
        Log($"VERDICT            : {(exactMatch ? "✓ PASS" : "✗ FAIL — see comparison")}");
        Log(bar);
        Log("");

        if (!exactMatch)
        {
            Assert.Fail(
                $"TTS↔STT round-trip mismatch (case: {caseId}):\n" +
                $"  input    : \"{input}\"\n" +
                $"  output   : \"{transcript}\"\n" +
                $"  edit dist: {editDist}\n" +
                $"  sim      : {similarity:P1}\n" +
                $"  TTS WAV  : {wavPath}\n" +
                $"  STT WAV  : {pcmWavPath}");
        }
    }

    // ── Logging helper — both ITestOutputHelper and Console ──────────────
    //
    // ITestOutputHelper: standard xUnit per-test capture (shown in test
    // explorer + dotnet test output regardless of pass/fail).
    // Console.WriteLine: redundant but ensures the trace shows up under
    // dotnet test loggers like "console;verbosity=detailed" too.

    private void Log(string line)
    {
        try { _output.WriteLine(line); } catch { /* helper may be disposed for failed asserts */ }
        Console.WriteLine(line);
    }

    // ── Normalisation ────────────────────────────────────────────────────
    //
    // The point of this test is to detect *content* drift in the TTS↔STT
    // round-trip — actual hallucinations, truncations, substitutions. We
    // explicitly do NOT want to flag:
    //   • case differences ("hello" → "Hello") — Whisper tokeniser adds
    //     leading caps even when the input was lowercase; not a recognition
    //     error.
    //   • punctuation differences ("hello" → "hello.") — likewise tokeniser
    //     post-processing.
    //   • whitespace differences (folding double spaces, trimming).
    //
    // Korean has no case, so ToLowerInvariant is a no-op there.

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ── Edit distance (Wagner-Fischer DP) ────────────────────────────────

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

    // ── Audio level + WAV wrap (kept inline to keep the test file self-
    //   contained; the production path uses Agent.Common.Voice.WavWriter
    //   which is internal). ───────────────────────────────────────────────

    private static (double peak, double rms) MeasurePcm16(byte[] pcm)
    {
        if (pcm.Length < 2) return (0, 0);
        long sumSq = 0;
        int peak = 0;
        var sampleCount = pcm.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            int abs = s < 0 ? -s : s;
            if (abs > peak) peak = abs;
            sumSq += (long)s * s;
        }
        var rms = Math.Sqrt(sumSq / (double)sampleCount) / 32768.0;
        return (peak / 32768.0, rms);
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
}
