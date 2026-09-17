using System.Text;

namespace ZeroWearable.Voice;

/// <summary>
/// Turns SuperTonic's 44.1 kHz float output into what the device plays: 16 kHz mono
/// PCM16, IMA ADPCM, in self-contained blocks.
///
/// No NAudio here on purpose - its resamplers go through Media Foundation / WinMM,
/// which would drag COM into a binary that has to survive Native AOT. A windowed-sinc
/// resampler and the ADPCM tables are a hundred lines and have no platform surface.
///
/// The block layout is the one the Chat app's firmware already decodes:
///   [predictor int16 LE][step index u8][reserved u8][nibbles, low nibble first]
/// so a device-side decoder ports across unchanged.
/// </summary>
public static class DeviceAudio
{
    public const int TargetRate = 16000;

    /// <summary>Samples per ADPCM block. 960 keeps one block at 484 bytes.</summary>
    public const int SamplesPerBlock = 960;

    /// <summary>host -> device speech frame magic.</summary>
    public const byte SpeechMagic = 0xA6;
    /// <summary>device -> host microphone frame magic.</summary>
    public const byte MicMagic = 0xA5;

    /// <summary>
    /// Resample float PCM in [-1,1] to 16 kHz PCM16. Windowed-sinc interpolation with
    /// the cutoff at the output Nyquist, which is what keeps 44.1 kHz speech from
    /// aliasing into a buzz when decimated by 2.75625.
    /// </summary>
    public static byte[] ToPcm16k(float[] samples, int sourceRate)
    {
        if (samples.Length == 0) return [];

        if (sourceRate == TargetRate)
        {
            var direct = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++) WriteSample(direct, i * 2, samples[i]);
            return direct;
        }

        const int taps = 12;                                  // 25-tap kernel
        var ratio = (double)sourceRate / TargetRate;          // input samples per output sample
        var cutoff = Math.Min(1.0, 1.0 / ratio);              // normalized to the input Nyquist
        var outCount = (int)(samples.Length / ratio);
        var pcm = new byte[outCount * 2];

        for (var n = 0; n < outCount; n++)
        {
            var center = n * ratio;
            var i0 = (int)Math.Floor(center);

            double acc = 0, weight = 0;
            for (var k = -taps; k <= taps; k++)
            {
                var idx = i0 + k;
                if (idx < 0 || idx >= samples.Length) continue;

                var d = center - idx;
                var w = Sinc(cutoff * d) * Blackman(d / (taps + 1.0));
                acc += samples[idx] * w;
                weight += w;
            }
            WriteSample(pcm, n * 2, weight > 1e-9 ? (float)(acc / weight) : 0f);
        }
        return pcm;
    }

    private static void WriteSample(byte[] buffer, int offset, float value)
    {
        var clamped = Math.Clamp(value, -1f, 1f);
        var s = (short)(clamped * 32767);
        buffer[offset] = (byte)s;
        buffer[offset + 1] = (byte)(s >> 8);
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1.0;
        var pix = Math.PI * x;
        return Math.Sin(pix) / pix;
    }

    private static double Blackman(double t)
    {
        if (Math.Abs(t) >= 1.0) return 0.0;
        var x = (t + 1.0) / 2.0;                              // map [-1,1] -> [0,1]
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * x) + 0.08 * Math.Cos(4 * Math.PI * x);
    }

    /// <summary>Build one speech frame: 0xA6 | id | seq(LE16) | ADPCM block.</summary>
    public static byte[] SpeechFrame(int id, int seq, ReadOnlySpan<byte> block)
    {
        var frame = new byte[4 + block.Length];
        frame[0] = SpeechMagic;
        frame[1] = (byte)id;
        frame[2] = (byte)seq;
        frame[3] = (byte)(seq >> 8);
        block.CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// <summary>Splits an inbound frame: magic | id(1) | seq(2 LE) | block.</summary>
    public static bool TryParseFrame(byte[] frame, byte magic, out int id, out int seq,
        out ReadOnlyMemory<byte> block)
    {
        id = 0;
        seq = 0;
        block = default;
        if (frame.Length < 5 || frame[0] != magic) return false;
        id = frame[1];
        seq = frame[2] | (frame[3] << 8);
        block = new ReadOnlyMemory<byte>(frame, 4, frame.Length - 4);
        return true;
    }

    /// <summary>Milliseconds of audio in a 16 kHz mono PCM16 buffer.</summary>
    public static int DurationMs(byte[] pcm16) => (int)(pcm16.Length / 2.0 / TargetRate * 1000);

    /// <summary>Wrap 16 kHz mono PCM16 in a WAV container (debug captures).</summary>
    public static byte[] ToWav(byte[] pcm16, int rate = TargetRate)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            const int channels = 1, bits = 16;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + pcm16.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * bits / 8);
            w.Write((short)(channels * bits / 8));
            w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm16.Length);
            w.Write(pcm16);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// IMA ADPCM (4-bit) encoder. Each block carries its own predictor and step index, so
/// one lost frame costs one block rather than the rest of the utterance.
/// </summary>
public static class ImaAdpcm
{
    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
        279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166,
        1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
        4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    public static List<byte[]> EncodeBlocks(byte[] pcm16, int samplesPerBlock)
    {
        var shorts = new short[pcm16.Length / 2];
        for (var i = 0; i < shorts.Length; i++) shorts[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));

        var blocks = new List<byte[]>();
        int predictor = 0, index = 0;
        for (var offset = 0; offset < shorts.Length; offset += samplesPerBlock)
        {
            var n = Math.Min(samplesPerBlock, shorts.Length - offset);
            if ((n & 1) == 1) n--;                            // blocks hold whole byte pairs
            if (n <= 0) break;
            blocks.Add(EncodeBlock(shorts.AsSpan(offset, n), ref predictor, ref index));
        }
        return blocks;
    }

    /// <summary>
    /// Decodes one self-contained block, appending PCM16 to <paramref name="pcm"/>. The
    /// mirror of <see cref="EncodeBlock"/>; the device's encoder is the one in
    /// brookesia_app_chat, and the block header is what makes a lost frame cost 60 ms
    /// instead of the rest of the utterance.
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Stream pcmOut)
    {
        if (block.Length < 5) return;

        var predictor = (short)(block[0] | (block[1] << 8));
        var index = Math.Clamp((int)block[2], 0, 88);

        for (var i = 4; i < block.Length; i++)
        {
            for (var half = 0; half < 2; half++)
            {
                var nibble = half == 0 ? block[i] & 0x0F : (block[i] >> 4) & 0x0F;
                var step = StepTable[index];

                var delta = step >> 3;
                if ((nibble & 4) != 0) delta += step;
                if ((nibble & 2) != 0) delta += step >> 1;
                if ((nibble & 1) != 0) delta += step >> 2;

                var next = (nibble & 8) != 0 ? predictor - delta : predictor + delta;
                predictor = (short)Math.Clamp(next, short.MinValue, short.MaxValue);
                index = Math.Clamp(index + IndexTable[nibble], 0, 88);

                pcmOut.WriteByte((byte)predictor);
                pcmOut.WriteByte((byte)((ushort)predictor >> 8));
            }
        }
    }

    public static byte[] EncodeBlock(ReadOnlySpan<short> samples, ref int predictor, ref int index)
    {
        var output = new byte[4 + (samples.Length + 1) / 2];
        output[0] = (byte)predictor;
        output[1] = (byte)(predictor >> 8);
        output[2] = (byte)index;
        output[3] = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var step = StepTable[index];
            var diff = samples[i] - predictor;
            var nibble = 0;
            if (diff < 0) { nibble = 8; diff = -diff; }

            var vp = step >> 3;
            if (diff >= step) { nibble |= 4; diff -= step; vp += step; }
            if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; vp += step >> 1; }
            if (diff >= step >> 2) { nibble |= 1; vp += step >> 2; }

            predictor = Math.Clamp((nibble & 8) != 0 ? predictor - vp : predictor + vp,
                short.MinValue, short.MaxValue);
            index = Math.Clamp(index + IndexTable[nibble], 0, 88);

            if ((i & 1) == 0) output[4 + i / 2] = (byte)nibble;
            else output[4 + i / 2] |= (byte)(nibble << 4);
        }
        return output;
    }
}
