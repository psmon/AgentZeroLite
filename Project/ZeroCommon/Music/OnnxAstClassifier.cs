using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Agent.Common.Music;

/// <summary>
/// AST (Audio Spectrogram Transformer) classifier driven by ONNX Runtime.
///
/// Expects an ONNX export of <c>MIT/ast-finetuned-audioset-10-10-0.4593</c>
/// (Optimum CLI: <c>optimum-cli export onnx --model MIT/ast-finetuned-audioset-10-10-0.4593 ast-audioset/</c>).
/// Input tensor shape (1, 1024, 128) — log-mel spectrogram. Output: (1, 527)
/// raw logits over AudioSet classes (sigmoid for multi-label scores).
/// </summary>
public sealed class OnnxAstClassifier : IMusicClassifier
{
    private readonly MusicSettings _settings;
    private InferenceSession? _session;
    private string[]? _labels;
    private string? _inputName;
    private readonly object _initLock = new();

    public OnnxAstClassifier(MusicSettings settings)
    {
        _settings = settings;
    }

    public string ProviderName => "AST AudioSet (ONNX)";
    public int RequiredSampleRate => 16_000;
    public int RequiredDurationSeconds => 10;

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_initLock)
            {
                if (_session is not null) return true;

                var modelPath = MusicSettingsStore.ResolveModelPath(_settings);
                if (!File.Exists(modelPath))
                {
                    progress?.Report($"✗ Model file not found: {modelPath}");
                    return false;
                }

                var sizeMb = new FileInfo(modelPath).Length / (1024.0 * 1024.0);
                progress?.Report($"Loading ONNX model ({sizeMb:F1} MB)…");

                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 0, // let ORT pick (0 = num cores)
                };

                _session = new InferenceSession(modelPath, options);
                _inputName = _session.InputMetadata.Keys.FirstOrDefault();

                var labelsPath = MusicSettingsStore.ResolveLabelsPath(_settings);
                _labels = LoadAudioSetLabels(labelsPath);

                var labelInfo = _labels is null
                    ? "labels file missing — falling back to numeric indices"
                    : $"{_labels.Length} labels loaded";
                progress?.Report($"✓ Model ready · input='{_inputName}' · {labelInfo}");
                return true;
            }
        }, ct);
    }

    public async Task<MusicInferenceResult> ClassifyAsync(byte[] pcm16, int topK, CancellationToken ct = default)
    {
        if (_session is null)
        {
            var ok = await EnsureReadyAsync(null, ct).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("AST model not ready — call EnsureReadyAsync first.");
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var preSw = Stopwatch.StartNew();
            var samples = MelSpectrogram.Pcm16ToFloat(pcm16);
            var logMel = MelSpectrogram.ComputeLogMel(samples);
            preSw.Stop();

            int frames = logMel.GetLength(0);
            int bins = logMel.GetLength(1);

            // AST expects (batch=1, time=1024, freq=128).
            var input = new DenseTensor<float>(new[] { 1, frames, bins });
            for (int t = 0; t < frames; t++)
                for (int m = 0; m < bins; m++)
                    input[0, t, m] = logMel[t, m];

            var feeds = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName ?? "input_values", input),
            };

            var infSw = Stopwatch.StartNew();
            using var results = _session!.Run(feeds);
            infSw.Stop();

            var logits = results.First().AsTensor<float>().ToArray();

            var scores = new float[logits.Length];
            for (int i = 0; i < logits.Length; i++)
                scores[i] = 1f / (1f + MathF.Exp(-logits[i]));

            var top = new List<MusicLabel>(topK);
            // Single-pass top-K selection — sort is fine at 527 classes.
            foreach (var (s, i) in scores
                .Select((s, i) => (s, i))
                .OrderByDescending(t => t.s)
                .Take(Math.Max(1, topK)))
            {
                top.Add(new MusicLabel(i, ResolveLabel(i), s));
            }

            return new MusicInferenceResult(
                TopLabels: top,
                SpectrogramFrames: frames,
                SpectrogramBins: bins,
                LogMel: logMel,
                PreprocessTime: preSw.Elapsed,
                InferenceTime: infSw.Elapsed);
        }, ct).ConfigureAwait(false);
    }

    private string ResolveLabel(int idx)
    {
        if (_labels is null || idx < 0 || idx >= _labels.Length)
            return $"class_{idx}";
        return _labels[idx];
    }

    /// <summary>
    /// Parse Google's <c>class_labels_indices.csv</c> (index,mid,display_name).
    /// Returns null when the file is missing so the caller can degrade to
    /// numeric labels instead of crashing.
    /// </summary>
    private static string[]? LoadAudioSetLabels(string path)
    {
        if (!File.Exists(path)) return null;

        var lines = File.ReadAllLines(path);
        var labels = new List<string>(lines.Length);
        // Skip header
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            if (parts.Length >= 3)
                labels.Add(parts[2].Trim('"'));
        }
        return labels.Count == 0 ? null : labels.ToArray();
    }

    private static string[] SplitCsvLine(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; sb.Append(c); }
            else if (c == ',' && !inQuotes) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }
}
