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
/// <para>
/// <b>Memory model (2026-07-15) — chunk-scoped instance.</b> Each
/// <see cref="DiarizeAsync"/> call builds its OWN
/// <see cref="OfflineSpeakerDiarization"/>, runs <c>Process</c>, and disposes
/// it in a <c>using</c> block, so ALL native memory (ONNX session arenas,
/// segmentation + embedding + clustering scratch) is returned to the OS after
/// every utterance. This mirrors how <c>WhisperLocalStt</c> disposes its
/// per-call processor while keeping the heavy model resident.
/// </para>
/// <para>
/// The previous design held ONE reused instance for the whole capture session.
/// ONNX Runtime's arena allocator grows to its peak working set and never
/// returns memory to the OS, so a reused instance's footprint only ratcheted
/// up. On an integrated GPU (shared system RAM) that pressure compounded with
/// Whisper's Vulkan allocations and faulted the driver a few minutes into a
/// note-capture session (STT-only sessions, which recycle per call, stayed
/// stable). Sherpa exposes no factory/processor split, so "return memory per
/// chunk" necessarily means recreating the session each call (~46 MB model
/// re-init). The build cost is logged; if it proves too heavy, switch to a
/// recycle-every-N cache — a localized change to <see cref="DiarizeAsync"/>.
/// </para>
///
/// Threading: inference (<c>Process</c>) runs synchronously and is wrapped in
/// <c>Task.Run</c> so callers stay UI-thread friendly.
/// </summary>
public sealed class SherpaSpeakerDiarizer : ISpeakerDiarizer
{
    private readonly DiarizationSettings _settings;
    private readonly object _initLock = new();
    // Validated model paths — resolved + existence-checked once in
    // EnsureReadyAsync. No persistent native instance is held (see class remarks).
    private bool _validated;
    private string? _segPath;
    private string? _embPath;

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
                if (_validated) return true;

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

                // Warm once: build + immediately dispose so config/model errors
                // surface at Start time (not on the first utterance) and the OS
                // file cache is primed. Per-chunk instances are created fresh in
                // DiarizeAsync — this warm instance is NOT retained.
                var warmSw = Stopwatch.StartNew();
                using (var warm = new OfflineSpeakerDiarization(BuildConfig(segPath, embPath)))
                {
                    warmSw.Stop();
                    progress?.Report($"✓ Diarizer ready · sample rate {warm.SampleRate} Hz (warm build {warmSw.ElapsedMilliseconds} ms)");
                    AppLogger.Log($"[Diar] warm build ok | buildMs={warmSw.ElapsedMilliseconds} seg={segMb:F1}MB emb={embMb:F1}MB");
                }

                _segPath = segPath;
                _embPath = embPath;
                _validated = true;
                return true;
            }
        }, ct);
    }

    private OfflineSpeakerDiarizationConfig BuildConfig(string segPath, string embPath)
    {
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
        return config;
    }

    public async Task<DiarizationResult> DiarizeAsync(byte[] pcm16, int hintSpeakerCount = 0, CancellationToken ct = default)
    {
        if (!_validated)
        {
            var ok = await EnsureReadyAsync(null, ct).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("Sherpa diarizer not ready — call EnsureReadyAsync first.");
        }

        string segPath, embPath;
        lock (_initLock) { segPath = _segPath!; embPath = _embPath!; }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var samples = Pcm16ToFloat(pcm16);

            // Chunk-scoped instance: build → process → dispose. The using block
            // returns every byte of native scratch to the OS after this chunk,
            // exactly like WhisperLocalStt's per-call processor. See class remarks
            // for why a persistent instance was the crash trigger on integrated GPUs.
            var buildSw = Stopwatch.StartNew();
            using var sd = new OfflineSpeakerDiarization(BuildConfig(segPath, embPath));
            buildSw.Stop();

            var inferSw = Stopwatch.StartNew();
            var raw = sd.Process(samples);
            inferSw.Stop();

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

            AppLogger.Log($"[Diar] chunk-scoped | buildMs={buildSw.ElapsedMilliseconds} inferMs={inferSw.ElapsedMilliseconds} samples={samples.Length} segs={list.Count} speakers={speakerCount}");
            return new DiarizationResult(list, speakerCount, inferSw.Elapsed);
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
        // No persistent native instance to release — DiarizeAsync scopes each
        // OfflineSpeakerDiarization to its own using block, so there is nothing
        // to leak here. Reset validation state for the IAsyncDisposable contract.
        lock (_initLock) { _validated = false; }
        return ValueTask.CompletedTask;
    }
}
