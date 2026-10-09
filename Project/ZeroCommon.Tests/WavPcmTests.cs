using Agent.Common.Voice;
using Xunit;

namespace ZeroCommon.Tests;

public class WavPcmTests
{
    private static byte[] Sine(int rate, int channels, double seconds, bool unknownSizes = false)
    {
        var frames = (int)(rate * seconds);
        var pcm = new byte[frames * channels * 2];
        for (var f = 0; f < frames; f++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 440 * f / rate) * 16000);
            for (var c = 0; c < channels; c++)
            {
                pcm[(f * channels + c) * 2] = (byte)s;
                pcm[(f * channels + c) * 2 + 1] = (byte)(s >> 8);
            }
        }
        var wav = WavWriter.WrapPcmAsWav(pcm, rate, 16, channels);
        if (unknownSizes)
        {
            // OpenAI TTS streams RIFF and data sizes as 0xFFFFFFFF.
            BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 4);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 40);
        }
        return wav;
    }

    [Fact]
    public void Supertonic_rate_stereo_is_mixed_and_resampled_to_16k_mono()
    {
        var pcm = WavPcm.To16kMono(Sine(44_100, 2, 1.0));
        Assert.InRange(pcm.Length / 2, 15_990, 16_010);
    }

    [Fact]
    public void Unknown_chunk_sizes_read_to_the_end_of_the_buffer()
    {
        var decoded = WavPcm.Decode(Sine(24_000, 1, 0.5, unknownSizes: true));
        Assert.Equal(24_000, decoded.SampleRate);
        Assert.Equal(12_000, decoded.Samples.Length);
    }

    [Fact]
    public void Sixteen_k_mono_round_trips_unchanged_in_length()
    {
        var pcm = WavPcm.To16kMono(Sine(16_000, 1, 0.25));
        Assert.Equal(4_000 * 2, pcm.Length);
    }

    [Fact]
    public void Not_a_wav_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => WavPcm.Decode(new byte[64]));
    }
}
