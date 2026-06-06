using System.Diagnostics;
using SherpaOnnx;

namespace Agent.Common.Voice.Diarization;

/// <summary>
/// k2-fsa Sherpa-ONNX based speaker diarization. Uses:
///
///   • Segmentation: pyannote-segmentation-3-0 (~6 MB) — frame-level VAD-aware
///     speaker change detection.
///   • Embedding:    3D-Speaker eres2net base (~40 MB) — per-segment speaker
///     embedding for clustering.
///
/// Input contract: 16 kHz mono float samples in <c>[-1, 1]</c>. PCM16 byte
/// input is converted in <see cref="DiarizeAsync"/>.
///
/// Threading: <see cref="OfflineSpeakerDiarization"/> is built on the
/// capture thread but inference (<c>Process</c>) runs synchronously and
/// is wrapped in <c>Task.Run</c> so callers stay UI-thread friendly.
/// </summary>
public sealed class SherpaSpeakerDiarizer : ISpeakerDiarizer
{
    private readonly DiarizationSettings _settings;
    private OfflineSpeakerDiarization? _sd;
    private readonly object _initLock = new();

    public SherpaSpeakerDiarizer(DiarizationSettings settings)
    {
        _settings = settings;
    }

    public string ProviderName => "Sherpa-ONNX (pyannote + 3D-Speaker)";
    public int RequiredSampleRate => 16_000;

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_initLock)
            {
                if (_sd is not null) return true;

                var segPath = DiarizationSettingsStore.ResolveSegmentationPath(_settings);
                var embPath = DiarizationSettingsStore.ResolveEmbeddingPath(_settings);

                if (!File.Exists(segPath))
                {
                    progress?.Report($"✗ Segmentation model missing: {segPath}");
                    return false;
                }
                if (!File.Exists(embPath))
                {
                    progress?.Report($"✗ Embedding model missing: {embPath}");
                    return false;
                }

                var segMb = new FileInfo(segPath).Length / (1024.0 * 1024.0);
                var embMb = new FileInfo(embPath).Length / (1024.0 * 1024.0);
                progress?.Report($"Loading Sherpa diarization (segmentation {segMb:F1} MB + embedding {embMb:F1} MB)…");

                var config = new OfflineSpeakerDiarizationConfig();
                config.Segmentation.Pyannote.Model = segPath;
                config.Embedding.Model = embPath;
                config.Clustering.NumClusters = _settings.ExpectedSpeakerCount > 0
                    ? _settings.ExpectedSpeakerCount
                    : -1; // -1 → auto-cluster using the threshold path
                if (_settings.NumThreads > 0)
                {
                    config.Segmentation.NumThreads = _settings.NumThreads;
                    config.Embedding.NumThreads = _settings.NumThreads;
                }

                _sd = new OfflineSpeakerDiarization(config);
                progress?.Report($"✓ Diarizer ready · sample rate {_sd.SampleRate} Hz");
                return true;
            }
        }, ct);
    }

    public async Task<DiarizationResult> DiarizeAsync(byte[] pcm16, int hintSpeakerCount = 0, CancellationToken ct = default)
    {
        if (_sd is null)
        {
            var ok = await EnsureReadyAsync(null, ct).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("Sherpa diarizer not ready — call EnsureReadyAsync first.");
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var samples = Pcm16ToFloat(pcm16);
            var sw = Stopwatch.StartNew();
            var raw = _sd!.Process(samples);
            sw.Stop();

            var list = new List<SpeakerSegment>();
            // Sherpa returns per-segment Start/End/Speaker triples.
            foreach (var seg in raw)
            {
                list.Add(new SpeakerSegment(seg.Start, seg.End, seg.Speaker));
            }

            int speakerCount = 0;
            if (list.Count > 0)
            {
                int max = -1;
                foreach (var s in list) if (s.SpeakerId > max) max = s.SpeakerId;
                speakerCount = max + 1;
            }

            return new DiarizationResult(list, speakerCount, sw.Elapsed);
        }, ct).ConfigureAwait(false);
    }

    private static float[] Pcm16ToFloat(byte[] pcm)
    {
        int n = pcm.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    public ValueTask DisposeAsync()
    {
        // Sherpa OfflineSpeakerDiarization owns native handles. Currently no
        // public Dispose on the C# wrapper (1.10.x); rely on finalizer for
        // process exit. If a later version exposes IDisposable, plug it here.
        _sd = null;
        return ValueTask.CompletedTask;
    }
}
