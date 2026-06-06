namespace Agent.Common.Music;

/// <summary>
/// Cheap, real-time spectrum analyzer for the Music tab's live bar display.
/// Distinct from <see cref="MelSpectrogram"/> on purpose — that one targets
/// AST inference accuracy (1024×128 mel, full STFT), this one targets
/// 30 Hz UI repaint (one FFT per call, log-frequency band aggregation).
///
/// Pipeline:
///   PCM16 → float → Hann-windowed FFT → |X|² → log-bucketed bars → 0..1 normalized
///
/// Stateful — keeps a rolling sample buffer so repeated calls always operate
/// on the most recent <c>FftSize</c> samples regardless of caller chunk size.
/// </summary>
public sealed class SpectrumBars
{
    public const int FftSize = 2048;
    public const int SampleRate = 16_000;

    private readonly float[] _ring = new float[FftSize];
    private int _ringWrite;

    private readonly float[] _window;
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];

    // Hann window amplitude correction. Peak |X[k]| for a full-scale sine =
    // N/2 * coherent_gain. coherent_gain(Hann) = mean(window) ≈ 0.5, so the
    // peak magnitude is N/4 and peak power is N²/16. Divide raw |X|² by this
    // so 0 dB on the analyzer corresponds to a digital full-scale sine — the
    // bars become a proper dBFS spectrogram instead of a raw-FFT readout that
    // saturates the moment any real audio hits the input.
    private readonly float _powerNormFactor;

    public int NyquistHz => SampleRate / 2;

    public SpectrumBars()
    {
        _window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));

        // Peak power for a unit sine through a Hann-windowed N-pt FFT = (N/4)².
        // Inverting that puts 0 dB at digital full-scale.
        float peakMag = FftSize / 4f;
        _powerNormFactor = 1f / (peakMag * peakMag);
    }

    /// <summary>Append a 16 kHz mono PCM16 chunk to the rolling sample window.</summary>
    public void Push(byte[] pcm16)
    {
        if (pcm16 is null || pcm16.Length < 2) return;
        int n = pcm16.Length / 2;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            _ring[_ringWrite] = s / 32768f;
            _ringWrite = (_ringWrite + 1) % FftSize;
        }
    }

    /// <summary>
    /// Compute <paramref name="nBars"/> log-frequency band levels (0..1) from
    /// the current ring contents. Returned array is freshly allocated and
    /// safe to hand to the UI thread.
    /// </summary>
    /// <param name="floorDb">dBFS that maps to bar 0%. Default -60 = typical music noise floor.</param>
    /// <param name="ceilDb">dBFS that maps to bar 100%. Default -3 = just under digital clipping headroom.</param>
    public float[] ComputeBars(int nBars, float minHz = 30f, float maxHz = 8000f, float floorDb = -60f, float ceilDb = -3f)
    {
        // Unroll the ring into a linear buffer (oldest sample first), apply
        // Hann window, run FFT.
        for (int i = 0; i < FftSize; i++)
        {
            int idx = (_ringWrite + i) % FftSize;
            _fftReal[i] = _ring[idx] * _window[i];
            _fftImag[i] = 0f;
        }
        Fft(_fftReal, _fftImag);

        int bins = FftSize / 2;
        var bars = new float[nBars];

        // Log-spaced band edges in Hz → FFT bin indices. The first band
        // collects every bin from minHz up to the next geometric edge, so
        // very low bins aren't all rolled into one fat bar.
        float logMin = MathF.Log(MathF.Max(1f, minHz));
        float logMax = MathF.Log(MathF.Min(maxHz, NyquistHz));
        float dbRange = ceilDb - floorDb;

        for (int b = 0; b < nBars; b++)
        {
            float loHz = MathF.Exp(logMin + (logMax - logMin) * b / nBars);
            float hiHz = MathF.Exp(logMin + (logMax - logMin) * (b + 1) / nBars);

            int loBin = Math.Clamp((int)MathF.Floor(loHz * FftSize / SampleRate), 1, bins - 1);
            int hiBin = Math.Clamp((int)MathF.Ceiling(hiHz * FftSize / SampleRate), loBin + 1, bins);

            float peak = 0f;
            for (int k = loBin; k < hiBin; k++)
            {
                float power = (_fftReal[k] * _fftReal[k] + _fftImag[k] * _fftImag[k]) * _powerNormFactor;
                if (power > peak) peak = power;
            }

            // Convert to dBFS then map to 0..1. Empty bin → floor.
            float db = peak > 0 ? 10f * MathF.Log10(peak) : floorDb;
            float v = (db - floorDb) / dbRange;
            bars[b] = Math.Clamp(v, 0f, 1f);
        }
        return bars;
    }

    /// <summary>Iterative in-place Cooley-Tukey radix-2 FFT. Same impl as <c>MelSpectrogram.Fft</c>; duplicated to keep this class self-contained.</summary>
    private static void Fft(float[] real, float[] imag)
    {
        int n = real.Length;
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
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
}
