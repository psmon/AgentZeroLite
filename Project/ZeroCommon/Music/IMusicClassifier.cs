namespace Agent.Common.Music;

/// <summary>
/// One classification result row. <see cref="Score"/> is post-sigmoid (0..1).
/// </summary>
public sealed record MusicLabel(int Index, string Name, float Score);

/// <summary>
/// Inference result bundle. <see cref="SpectrogramFrames"/> /
/// <see cref="SpectrogramBins"/> are surfaced so the Music tab can render the
/// same mel-spectrogram the model consumed — that's the "instrument + spectrum"
/// link the researching mode promises.
/// </summary>
public sealed record MusicInferenceResult(
    IReadOnlyList<MusicLabel> TopLabels,
    int SpectrogramFrames,
    int SpectrogramBins,
    float[,] LogMel,
    TimeSpan PreprocessTime,
    TimeSpan InferenceTime);

/// <summary>
/// Backend-agnostic music classifier. Mirrors the <c>ISpeechToText</c> shape:
/// EnsureReadyAsync warms up native resources before the first inference so the
/// Test button doesn't stall on cold-start ONNX session creation.
/// </summary>
public interface IMusicClassifier : IAsyncDisposable
{
    string ProviderName { get; }
    int RequiredSampleRate { get; }
    int RequiredDurationSeconds { get; }

    /// <summary>
    /// Pre-load the model + labels. Safe to call repeatedly; subsequent calls
    /// short-circuit when the session is already live. Progress messages are
    /// surface-quality status text for the UI.
    /// </summary>
    Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Run a single inference on raw 16 kHz mono 16-bit PCM. The caller is
    /// responsible for padding/truncating to <see cref="RequiredDurationSeconds"/>
    /// — pass a shorter clip and the model output may degrade.
    /// </summary>
    Task<MusicInferenceResult> ClassifyAsync(byte[] pcm16, int topK, CancellationToken ct = default);
}
