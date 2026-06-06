namespace Agent.Common.Music;

/// <summary>
/// Log-mel spectrogram extractor matching the shape AST expects:
/// (frames=1024, mel_bins=128). Self-contained — no FFT NuGet dependency, just
/// an in-place iterative Cooley-Tukey radix-2 FFT.
///
/// Caveat: AST's reference feature extractor is Kaldi <c>compliance.kaldi.fbank</c>
/// (Povey window, pre-emphasis, energy floor). This impl uses Hann window + HTK
/// mel scale, which is close enough for first-pass label sanity checks but will
/// not reproduce the published mAP exactly. Refining to Povey/Kaldi parity is a
/// follow-up if instrument calls come back noisy.
/// </summary>
public static class MelSpectrogram
{
    // AST training-time normalization stats — same constants as the HF
    // ASTFeatureExtractor defaults. Output is (x - mean) / (std * 2).
    public const float AstMean = -4.2677393f;
    public const float AstStd = 4.5689974f;

    /// <summary>
    /// Extract a (targetFrames, nMels) log-mel matrix from 16 kHz mono float samples.
    /// Frames beyond the input's natural length stay at zero (padded with silence).
    /// </summary>
    public static float[,] ComputeLogMel(
        float[] samples,
        int sampleRate = 16_000,
        int frameLength = 400,
        int frameShift = 160,
        int nMels = 128,
        int targetFrames = 1024,
        float normalizeMean = AstMean,
        float normalizeStd = AstStd)
    {
        int nFft = NextPow2(frameLength);
        int nBins = nFft / 2 + 1;

        var window = HannWindow(frameLength);
        var melFilter = BuildMelFilterbank(nMels, nFft, sampleRate);

        int producible = samples.Length >= frameLength
            ? (samples.Length - frameLength) / frameShift + 1
            : 0;
        int frames = Math.Min(producible, targetFrames);

        var output = new float[targetFrames, nMels];

        var real = new float[nFft];
        var imag = new float[nFft];
        var power = new float[nBins];

        // Padding frames get the AST-normalized log of the silence floor so the
        // model isn't fed a step discontinuity at the end of short clips.
        float silenceFloor = (MathF.Log(1e-10f) - normalizeMean) / (normalizeStd * 2f);

        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * frameShift;
            Array.Clear(real, 0, nFft);
            Array.Clear(imag, 0, nFft);

            for (int i = 0; i < frameLength; i++)
                real[i] = samples[offset + i] * window[i];

            Fft(real, imag);

            for (int k = 0; k < nBins; k++)
                power[k] = real[k] * real[k] + imag[k] * imag[k];

            for (int m = 0; m < nMels; m++)
            {
                float melEnergy = 0f;
                for (int k = 0; k < nBins; k++)
                    melEnergy += power[k] * melFilter[m, k];

                float logMel = MathF.Log(MathF.Max(melEnergy, 1e-10f));
                output[frame, m] = (logMel - normalizeMean) / (normalizeStd * 2f);
            }
        }

        for (int frame = frames; frame < targetFrames; frame++)
            for (int m = 0; m < nMels; m++)
                output[frame, m] = silenceFloor;

        return output;
    }

    /// <summary>Convert 16-bit little-endian PCM bytes to normalized float (-1..1).</summary>
    public static float[] Pcm16ToFloat(byte[] pcm)
    {
        int n = pcm.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = sample / 32768f;
        }
        return samples;
    }

    private static int NextPow2(int x)
    {
        int p = 1;
        while (p < x) p <<= 1;
        return p;
    }

    private static float[] HannWindow(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
        return w;
    }

    /// <summary>Iterative in-place Cooley-Tukey radix-2 FFT. <c>real.Length</c> must be a power of 2.</summary>
    private static void Fft(float[] real, float[] imag)
    {
        int n = real.Length;

        // Bit-reverse permutation
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wlenR = MathF.Cos(ang);
            float wlenI = MathF.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                float wR = 1f, wI = 0f;
                for (int k = 0; k < half; k++)
                {
                    float uR = real[i + k];
                    float uI = imag[i + k];
                    int idx = i + k + half;
                    float vR = real[idx] * wR - imag[idx] * wI;
                    float vI = real[idx] * wI + imag[idx] * wR;
                    real[i + k] = uR + vR;
                    imag[i + k] = uI + vI;
                    real[idx] = uR - vR;
                    imag[idx] = uI - vI;
                    float nwR = wR * wlenR - wI * wlenI;
                    float nwI = wR * wlenI + wI * wlenR;
                    wR = nwR;
                    wI = nwI;
                }
            }
        }
    }

    /// <summary>HTK mel scale triangular filterbank. Slightly off from librosa's Slaney scale.</summary>
    private static float[,] BuildMelFilterbank(int nMels, int nFft, int sampleRate)
    {
        int nBins = nFft / 2 + 1;
        var fb = new float[nMels, nBins];

        float melMin = HzToMel(0f);
        float melMax = HzToMel(sampleRate / 2f);

        var hzPoints = new float[nMels + 2];
        var binPoints = new int[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
        {
            float mel = melMin + (melMax - melMin) * i / (nMels + 1);
            hzPoints[i] = MelToHz(mel);
            binPoints[i] = (int)MathF.Floor((nFft + 1) * hzPoints[i] / sampleRate);
        }

        for (int m = 0; m < nMels; m++)
        {
            int leftBin = binPoints[m];
            int centerBin = binPoints[m + 1];
            int rightBin = binPoints[m + 2];

            for (int k = Math.Max(leftBin, 0); k < centerBin && k < nBins; k++)
                fb[m, k] = (float)(k - leftBin) / Math.Max(1, centerBin - leftBin);
            for (int k = Math.Max(centerBin, 0); k < rightBin && k < nBins; k++)
                fb[m, k] = (float)(rightBin - k) / Math.Max(1, rightBin - centerBin);
        }
        return fb;
    }

    private static float HzToMel(float hz) => 2595f * MathF.Log10(1f + hz / 700f);
    private static float MelToHz(float mel) => 700f * (MathF.Pow(10f, mel / 2595f) - 1f);
}
