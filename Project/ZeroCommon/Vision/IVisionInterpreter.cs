namespace Agent.Common.Vision;

/// <summary>
/// One object-detection box in captured-image pixel coordinates. Florence-2's OD
/// task returns labels + boxes only (no confidence score), so there is no Score.
/// </summary>
public readonly record struct VisionDetection(string Label, float XMin, float YMin, float XMax, float YMax)
{
    public float Width => XMax - XMin;
    public float Height => YMax - YMin;
}

/// <summary>Result of interpreting one frame — detections plus any raw model text and timing.</summary>
public sealed record VisionResult(
    IReadOnlyList<VisionDetection> Detections,
    string RawText,
    TimeSpan InferenceTime);

/// <summary>
/// Backend-agnostic contract for the on-device vision model, mirroring
/// <see cref="Agent.Common.Music.IMusicClassifier"/>: lazy load then per-frame
/// inference. The only implementation today is <see cref="Florence2VisionInterpreter"/>.
/// </summary>
public interface IVisionInterpreter : IAsyncDisposable
{
    string ProviderName { get; }

    /// <summary>
    /// Load the model into memory. Returns false (with a progress message) when the
    /// model files are not present — the caller should route the user to Download.
    /// </summary>
    Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Run the configured task on one encoded image (PNG/JPEG bytes).</summary>
    Task<VisionResult> InterpretAsync(byte[] imageBytes, CancellationToken ct = default);
}
