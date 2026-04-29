using System.IO;
using Agent.Common;
using NAudio.Wave;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Prepends and appends low-level white noise to a WAV byte array. Used by
/// the virtual voice tester so the synthesised speech sits inside a window
/// of "room tone" rather than slamming straight from digital silence into
/// the first phoneme — Whisper handles natural-noise-floor audio more
/// reliably than absolute-silence-then-speech, especially on TTS voices
/// whose attack envelopes are sharper than human speech.
///
/// Noise is plain uniform-distribution white noise scaled to a target
/// RMS in dBFS. -45 dBFS is "quiet room tone" — clearly audible if you
/// crank monitors but well below the speech floor. Pink noise would be
/// more natural but uniform is enough for STT robustness.
///
/// Format constraint: 16-bit linear PCM only (every TTS provider in this
/// project produces that today). Other formats fall through unchanged
/// with a one-line log so we can spot it if a future provider uses
/// 32-bit float.
/// </summary>
internal static class WavNoisePadder
{
    public static byte[] PadWithNoise(byte[] wavBytes, double leadSec, double trailSec, double rmsDbfs)
    {
        if (wavBytes is null || wavBytes.Length == 0) return wavBytes ?? Array.Empty<byte>();
        if (leadSec <= 0 && trailSec <= 0) return wavBytes;

        try
        {
            // Same OpenAI-streaming-WAV-sentinel patch WavToPcm uses —
            // without it NAudio's WaveFileReader fails on
            // 0xFFFFFFFF chunk sizes via MemoryStream.set_Position
            // overflow.
            wavBytes = VoicePlaybackService.PatchWavHeaderSizes(wavBytes);

            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);
            var fmt = reader.WaveFormat;

            if (fmt.BitsPerSample != 16 || fmt.Encoding != WaveFormatEncoding.Pcm)
            {
                AppLogger.Log($"[WavPadder] skip — unsupported format bits={fmt.BitsPerSample} enc={fmt.Encoding}");
                return wavBytes;
            }

            // Read the original PCM data.
            var origData = new byte[reader.Length];
            int read = 0;
            while (read < origData.Length)
            {
                int n = reader.Read(origData, read, origData.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < origData.Length)
                Array.Resize(ref origData, read);

            // Generate lead + trail noise sized in *bytes* aligned to the
            // block (channels * 2 for 16-bit).
            int blockAlign = Math.Max(1, fmt.BlockAlign);
            int leadBytes  = AlignDown((int)(leadSec  * fmt.AverageBytesPerSecond), blockAlign);
            int trailBytes = AlignDown((int)(trailSec * fmt.AverageBytesPerSecond), blockAlign);
            var leadNoise  = GenerateWhiteNoise16(leadBytes,  rmsDbfs);
            var trailNoise = GenerateWhiteNoise16(trailBytes, rmsDbfs);

            // Concat: leadNoise + origData + trailNoise.
            var combined = new byte[leadNoise.Length + origData.Length + trailNoise.Length];
            Buffer.BlockCopy(leadNoise,  0, combined, 0,                                   leadNoise.Length);
            Buffer.BlockCopy(origData,   0, combined, leadNoise.Length,                    origData.Length);
            Buffer.BlockCopy(trailNoise, 0, combined, leadNoise.Length + origData.Length,  trailNoise.Length);

            var result = WrapPcmAsWav(combined, fmt.SampleRate, fmt.BitsPerSample, fmt.Channels);
            AppLogger.Log($"[WavPadder] padded | rate={fmt.SampleRate} ch={fmt.Channels} bits={fmt.BitsPerSample} lead={leadSec:F2}s trail={trailSec:F2}s rmsDbfs={rmsDbfs:F0} orig={wavBytes.Length}→{result.Length} bytes");
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WavPadder] failed — {ex.GetType().Name}: {ex.Message} (returning unpadded)");
            return wavBytes;
        }
    }

    private static int AlignDown(int n, int block) => n - (n % block);

    /// <summary>
    /// Build a byte array of <paramref name="byteCount"/> bytes containing
    /// uniform white noise int16 samples scaled so the RMS equals
    /// <paramref name="rmsDbfs"/> dBFS. Uniform distribution has RMS =
    /// peak/√3, so we set peak = rmsAmp × √3 to hit the requested RMS.
    /// </summary>
    private static byte[] GenerateWhiteNoise16(int byteCount, double rmsDbfs)
    {
        if (byteCount <= 0) return Array.Empty<byte>();
        // Round to even (16-bit alignment).
        byteCount &= ~1;
        var buffer = new byte[byteCount];
        if (byteCount == 0) return buffer;

        var targetRmsAmp = 32768.0 * Math.Pow(10.0, rmsDbfs / 20.0);
        var peakAmp = (int)Math.Min(32767, targetRmsAmp * Math.Sqrt(3.0));
        if (peakAmp <= 0) return buffer; // dBFS so quiet it's effectively silence

        var rng = Random.Shared;
        var sampleCount = byteCount / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)rng.Next(-peakAmp, peakAmp + 1);
            buffer[i * 2]     = (byte)(s & 0xFF);
            buffer[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return buffer;
    }

    /// <summary>
    /// Local copy of WavWriter logic — that one's `internal` to ZeroCommon.
    /// Tiny enough to inline rather than pierce the assembly boundary.
    /// </summary>
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
