using System.Diagnostics;
using Florence2;
using Microsoft.ML.OnnxRuntime;

namespace Agent.Common.Vision;

/// <summary>
/// On-device vision interpreter backed by the <c>Florence2</c> wrapper package
/// (curiosity-ai/florence2-sharp, MIT) over ONNX Runtime. The package owns the
/// hard parts — CLIP image preprocessing, the BART-style encoder/decoder, the
/// tokenizer, and the autoregressive generation loop — so this class is a thin
/// lifecycle + result-mapping shell, exactly like
/// <see cref="Agent.Common.Music.OnnxAstClassifier"/> is for AST.
///
/// Model files are expected under <see cref="VisionSettingsStore.ResolveModelDir"/>;
/// they are fetched pip-free from HuggingFace by <see cref="VisionModelDownloader"/>.
/// </summary>
public sealed class Florence2VisionInterpreter : IVisionInterpreter
{
    private readonly VisionSettings _settings;
    private readonly object _initLock = new();

    private FlorenceModelDownloader? _source;
    private Florence2Model? _model;
    private SessionOptions? _sessionOptions;

    public Florence2VisionInterpreter(VisionSettings settings) => _settings = settings;

    public string ProviderName => "Florence-2 (ONNX)";

    public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_initLock)
            {
                if (_model is not null) return true;

                var dir = VisionSettingsStore.ResolveModelDir(_settings);
                if (!VisionSettingsStore.IsModelPresent(dir))
                {
                    progress?.Report($"✗ Model not downloaded — click Download (dir: {dir})");
                    return false;
                }

                progress?.Report("Loading Florence-2 model…");
                var source = new FlorenceModelDownloader(dir);

                // Idempotent when files already exist: the wrapper skips present
                // files (no network) but this call is still required to register
                // the on-disk paths before the model ctor reads model bytes.
                source.DownloadModelsAsync(
                    st => { if (!string.IsNullOrEmpty(st.Message)) progress?.Report(st.Message); },
                    null,
                    ct).GetAwaiter().GetResult();

                var so = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 0, // 0 = let ORT pick (num cores)
                };

                var model = new Florence2Model(source, so);

                _source = source;
                _sessionOptions = so;
                _model = model;
                progress?.Report("✓ Florence-2 ready");
                return true;
            }
        }, ct);
    }

    public async Task<VisionResult> InterpretAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (_model is null)
        {
            var ok = await EnsureReadyAsync(null, ct).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("Vision model not ready — call EnsureReadyAsync first.");
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream(imageBytes, writable: false);

            var sw = Stopwatch.StartNew();
            // Run is synchronous + batched; we feed a single frame.
            var results = _model!.Run(TaskTypes.OD, new Stream[] { ms }, "", ct);
            sw.Stop();

            var first = results is { Length: > 0 } ? results[0] : null;

            var detections = new List<VisionDetection>();
            if (first?.BoundingBoxes is { } groups)
            {
                foreach (var g in groups)
                {
                    if (g?.BBoxes is null) continue;
                    foreach (var b in g.BBoxes)
                        detections.Add(new VisionDetection(g.Label ?? "", b.xmin, b.ymin, b.xmax, b.ymax));
                }
            }

            return new VisionResult(detections, first?.PureText ?? "", sw.Elapsed);
        }, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_initLock)
        {
            (_model as IDisposable)?.Dispose();
            _sessionOptions?.Dispose();
            _model = null;
            _source = null;
            _sessionOptions = null;
        }
        return ValueTask.CompletedTask;
    }
}
