using System.IO;
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
internal static class WavToPcm
{
    public static byte[] To16kMono(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length == 0) return Array.Empty<byte>();

        using var ms = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(ms);

        // Common case shortcut — SAPI Heami in particular often emits
        // exactly 16 kHz / 16-bit / mono, in which case we just want the
        // data chunk bytes with no resampling pass.
        if (reader.WaveFormat.SampleRate == 16_000
            && reader.WaveFormat.Channels == 1
            && reader.WaveFormat.BitsPerSample == 16
            && reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm)
        {
            return ReadAll(reader);
        }

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
        return outMs.ToArray();
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
}
