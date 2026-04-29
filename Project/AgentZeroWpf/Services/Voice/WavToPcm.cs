using System.IO;
using Agent.Common;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Decode a WAV byte array into raw PCM 16 kHz / 16-bit / mono — the format
/// every <c>ISpeechToText</c> implementation in this project expects on
/// <c>TranscribeAsync</c>. Used by the Virtual Voice "bypass acoustic loop"
/// path so synthesised audio can feed STT directly without traversing the
/// speaker → microphone → OS-level echo-cancel gauntlet.
///
/// Pipeline: WaveFileReader → ISampleProvider → ToMono (if needed)
/// → WdlResamplingSampleProvider (if rate ≠ 16 kHz) → ToWaveProvider16
/// → byte[] (header-less PCM).
///
/// Why WdlResamplingSampleProvider and not <c>MediaFoundationResampler</c>:
/// the latter requires <c>MediaFoundationApi.Startup</c> on a thread with
/// proper apartment state, which doesn't always cooperate inside WPF
/// dispatcher contexts. WDL is bundled with NAudio, runs anywhere, and
/// the quality is plenty for STT (Whisper / cloud models tolerate
/// linear-phase resampling without measurable accuracy loss).
/// </summary>
public static class WavToPcm
{
    /// <summary>
    /// Decode a WAV blob to raw PCM 16k mono 16-bit. Logs the source format
    /// + output stats so the bypass path can be diagnosed when STT produces
    /// garbage on a synthesised input that audibly sounds correct.
    /// </summary>
    public static byte[] To16kMono(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length == 0) return Array.Empty<byte>();

        // OpenAI TTS streams WAV with chunk-size sentinels of 0xFFFFFFFF
        // ("unknown length"). NAudio's WaveFileReader passes that straight
        // to MemoryStream.set_Position which overflows Int32 and throws
        // ArgumentOutOfRangeException. PatchWavHeaderSizes (already used
        // by VoicePlaybackService for the same providers) rewrites the
        // RIFF + data chunk sizes to actual byte counts.
        wavBytes = VoicePlaybackService.PatchWavHeaderSizes(wavBytes);

        using var ms = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(ms);
        var fmt = reader.WaveFormat;
        AppLogger.Log($"[WavToPcm] source | rate={fmt.SampleRate} ch={fmt.Channels} bits={fmt.BitsPerSample} enc={fmt.Encoding} bytesPerSec={fmt.AverageBytesPerSecond} dataBytes={reader.Length}");

        byte[] pcm;

        // Common case shortcut — SAPI Heami in particular often emits
        // exactly 16 kHz / 16-bit / mono, in which case we just want the
        // data chunk bytes with no resampling pass.
        if (fmt.SampleRate == 16_000
            && fmt.Channels == 1
            && fmt.BitsPerSample == 16
            && fmt.Encoding == WaveFormatEncoding.Pcm)
        {
            pcm = ReadAll(reader);
            AppLogger.Log($"[WavToPcm] shortcut | already 16k/1/16 PCM, header stripped, {pcm.Length} bytes");
        }
        else
        {
            ISampleProvider provider = reader.ToSampleProvider();
            if (provider.WaveFormat.Channels > 1)
                provider = new StereoToMonoSampleProvider(provider);
            if (provider.WaveFormat.SampleRate != 16_000)
                provider = new WdlResamplingSampleProvider(provider, 16_000);

            var pcm16 = provider.ToWaveProvider16();
            using var outMs = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
                outMs.Write(buffer, 0, read);
            pcm = outMs.ToArray();
            AppLogger.Log($"[WavToPcm] resampled | {fmt.SampleRate}→16000, ch{fmt.Channels}→1, {pcm.Length} bytes");
        }

        // Stats — RMS and peak in dBFS. Whisper expects audio in roughly
        // [-30, -10] dB RMS. Out-of-range levels (silent or clipping)
        // correlate with hallucinated output. Logs surface this so we
        // can tell whether the recognition failure is "audio is bad"
        // vs "audio is fine but model rejects synthetic timbre".
        var (peak, rms) = MeasurePcm16(pcm);
        var peakDb = peak > 0 ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
        var rmsDb  = rms  > 0 ? 20.0 * Math.Log10(rms)  : double.NegativeInfinity;
        AppLogger.Log($"[WavToPcm] level | peak={peakDb:F1} dBFS rms={rmsDb:F1} dBFS samples={pcm.Length / 2}");

        return pcm;
    }

    private static byte[] ReadAll(WaveStream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            ms.Write(buffer, 0, read);
        return ms.ToArray();
    }

    /// <summary>
    /// Walk a 16-bit little-endian PCM byte array and return (peak, rms)
    /// in normalised [0, 1] amplitude. Cheap — done linearly, no copies.
    /// </summary>
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
}
