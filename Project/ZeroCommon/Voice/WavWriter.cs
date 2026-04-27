namespace Agent.Common.Voice;

/// <summary>
/// Minimal RIFF WAV serialiser used by both <see cref="OpenAiWhisperStt"/> and
/// <see cref="WebnoriGemmaStt"/> when shipping raw PCM through APIs that
/// require a self-describing audio container.
/// </summary>
internal static class WavWriter
{
    public static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, int bitsPerSample, int channels)
    {
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = pcm.Length;
        var fileSize = 36 + dataSize;

        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(fileSize);
        bw.Write("WAVE"u8);

        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);

        bw.Write("data"u8);
        bw.Write(dataSize);
        bw.Write(pcm);

        return ms.ToArray();
    }
}
