namespace Agent.Common.Voice;

/// <summary>
/// WAV → raw PCM 16 kHz / 16-bit / mono, the format every <see cref="ISpeechToText"/>
/// expects — without NAudio, so a host that is not Windows can feed synthesised speech
/// straight back into STT. The WPF host has its own NAudio-based <c>WavToPcm</c>; this one
/// exists for the Avalonia host and anything else in ZeroCommon's reach.
///
/// <para>Handles 16-bit PCM and 32-bit float (format tags 1, 3 and the 0xFFFE
/// extensible wrapper), any channel count (averaged to mono), and the 0xFFFFFFFF
/// "unknown length" chunk sizes OpenAI TTS streams — the data chunk then runs to the
/// end of the buffer. Resampling is linear, which Whisper tolerates without measurable
/// loss.</para>
/// </summary>
public static class WavPcm
{
    public const int TargetRate = 16_000;

    /// <summary>The decoded samples, mono, in [-1, 1], and their sample rate.</summary>
    public sealed record Decoded(float[] Samples, int SampleRate)
    {
        public double DurationSeconds => SampleRate == 0 ? 0 : (double)Samples.Length / SampleRate;
    }

    public static byte[] To16kMono(byte[] wav)
    {
        var decoded = Decode(wav);
        return ToPcm16(Resample(decoded.Samples, decoded.SampleRate, TargetRate));
    }

    public static Decoded Decode(byte[] wav)
    {
        if (wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE"))
            throw new InvalidDataException("not a RIFF/WAVE buffer");

        int formatTag = 0, channels = 0, rate = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToUInt32(wav, pos + 4);
            var body = pos + 8;
            if (id == "fmt ")
            {
                formatTag = BitConverter.ToUInt16(wav, body);
                channels = BitConverter.ToUInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
                if (formatTag == 0xFFFE && size >= 26) formatTag = BitConverter.ToUInt16(wav, body + 24);
            }
            else if (id == "data")
            {
                var length = size == uint.MaxValue || body + (long)size > wav.Length ? wav.Length - body : (int)size;
                if (channels == 0 || rate == 0) throw new InvalidDataException("data chunk before fmt chunk");
                return new Decoded(ToMonoFloat(wav.AsSpan(body, length), formatTag, channels, bits), rate);
            }
            if (size == uint.MaxValue) break;
            pos = body + (int)size + (int)(size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    /// <summary>Plain 16-bit mono PCM WAV — the one shape every player accepts.</summary>
    public static byte[] ToWav(Decoded decoded) =>
        WavWriter.WrapPcmAsWav(ToPcm16(decoded.Samples), decoded.SampleRate, 16, 1);

    public static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate || samples.Length == 0) return samples;
        var outLength = (int)((long)samples.Length * toRate / fromRate);
        var result = new float[outLength];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < outLength; i++)
        {
            var src = i * step;
            var i0 = (int)src;
            var i1 = Math.Min(i0 + 1, samples.Length - 1);
            var t = (float)(src - i0);
            result[i] = samples[i0] * (1 - t) + samples[i1] * t;
        }
        return result;
    }

    public static byte[] ToPcm16(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        return pcm;
    }

    private static float[] ToMonoFloat(ReadOnlySpan<byte> data, int formatTag, int channels, int bits)
    {
        var bytesPerSample = bits / 8;
        if (formatTag == 1 && bits == 16 || formatTag == 3 && bits == 32)
        {
            var frames = data.Length / (bytesPerSample * channels);
            var mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var at = (f * channels + c) * bytesPerSample;
                    sum += formatTag == 3
                        ? BitConverter.ToSingle(data.Slice(at, 4))
                        : BitConverter.ToInt16(data.Slice(at, 2)) / 32768f;
                }
                mono[f] = sum / channels;
            }
            return mono;
        }
        throw new NotSupportedException($"WAV format tag {formatTag} with {bits}-bit samples");
    }

    private static bool Tag(byte[] b, int at, string tag) =>
        b[at] == tag[0] && b[at + 1] == tag[1] && b[at + 2] == tag[2] && b[at + 3] == tag[3];
}
