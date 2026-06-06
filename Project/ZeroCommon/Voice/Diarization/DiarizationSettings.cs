namespace Agent.Common.Voice.Diarization;

/// <summary>Stable string identifiers for diarization providers. Same shape as <c>SttProviderNames</c>.</summary>
public static class DiarizationProviderNames
{
    /// <summary>Diarization disabled — Voice / Music / voice-note pipelines emit no Speaker labels.</summary>
    public const string Off = "Off";

    /// <summary>
    /// k2-fsa Sherpa-ONNX with pyannote-segmentation-3-0 + 3D-Speaker embedding.
    /// Pure ONNX, ~50 MB cached locally, CPU-only inference. Default when M0024
    /// ships.
    /// </summary>
    public const string SherpaPyannote3D = "SherpaPyannote3D";
}

/// <summary>
/// Speaker-diarization configuration. Persisted as <c>diarization-settings.json</c>
/// next to the LLM/Voice/Music side-car files under
/// <c>%LOCALAPPDATA%\AgentZeroLite\</c>. Mission M0024.
/// </summary>
public sealed class DiarizationSettings
{
    /// <summary>One of <see cref="DiarizationProviderNames"/>. Default Off — diarization is opt-in.</summary>
    public string Provider { get; set; } = DiarizationProviderNames.Off;

    /// <summary>
    /// Override path to the segmentation ONNX. Empty → convention path under
    /// <c>%LOCALAPPDATA%\AgentZeroLite\models\sherpa-diarization\segmentation.onnx</c>.
    /// </summary>
    public string SegmentationModelPath { get; set; } = "";

    /// <summary>
    /// Override path to the speaker-embedding ONNX. Empty → convention path
    /// under <c>%LOCALAPPDATA%\AgentZeroLite\models\sherpa-diarization\embedding.onnx</c>.
    /// </summary>
    public string EmbeddingModelPath { get; set; } = "";

    /// <summary>
    /// 0 = auto-cluster (let the embedding clusterer pick); &gt;0 = fix the
    /// number of speakers ahead of time (useful for known 2-person interview).
    /// </summary>
    public int ExpectedSpeakerCount { get; set; } = 0;

    /// <summary>
    /// Internal-Op threads passed to ONNX Runtime sessions. 0 = let ORT pick
    /// (number of cores). Mirror of the AST classifier's threading default.
    /// </summary>
    public int NumThreads { get; set; } = 0;
}
