namespace Agent.Common.Voice.Diarization;

/// <summary>
/// One contiguous span of audio attributed to a single speaker.
/// Times are seconds from the start of the analysed clip; <see cref="SpeakerId"/>
/// is a per-clip integer label (0, 1, 2, …) — not a global identity.
/// </summary>
public sealed record SpeakerSegment(double StartSec, double EndSec, int SpeakerId)
{
    public double DurationSec => Math.Max(0, EndSec - StartSec);

    /// <summary>Render as human-readable "Speaker A", "Speaker B" … from <see cref="SpeakerId"/>.</summary>
    public string SpeakerLabel => SpeakerId < 0
        ? "?"
        : SpeakerId < 26
            ? $"Speaker {(char)('A' + SpeakerId)}"
            : $"Speaker {SpeakerId + 1}";
}

/// <summary>
/// Full diarization result — every analyzed sample's speaker assignment plus
/// the cluster count we settled on. Independent of any STT transcript;
/// callers merge text + speaker spans by timestamp overlap.
/// </summary>
public sealed record DiarizationResult(
    IReadOnlyList<SpeakerSegment> Segments,
    int SpeakerCount,
    TimeSpan InferenceTime);

/// <summary>
/// Backend-agnostic speaker diarization. Mirrors the shape of
/// <c>IMusicClassifier</c> on purpose: <see cref="EnsureReadyAsync"/> warms
/// native resources; <see cref="DiarizeAsync"/> runs one analysis on raw
/// 16 kHz mono PCM16.
///
/// Implementations today:
/// <list type="bullet">
///   <item><c>SherpaSpeakerDiarizer</c> — pyannote-segmentation-3-0 + 3D-Speaker
///   embedding via k2-fsa Sherpa-ONNX (pure ONNX, no Python).</item>
/// </list>
///
/// Future implementations (e.g. pyannote v3 via Python subprocess, NVIDIA
/// NeMo Sortformer, WhisperX) slot into the same seam without UI churn.
/// </summary>
public interface ISpeakerDiarizer : IAsyncDisposable
{
    string ProviderName { get; }
    int RequiredSampleRate { get; }

    /// <summary>
    /// Pre-load model files + native session. Safe to call repeatedly; subsequent
    /// calls short-circuit when ready. Progress messages feed the UI status line.
    /// </summary>
    Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Run diarization on a single 16 kHz mono PCM16 byte buffer.
    /// <paramref name="hintSpeakerCount"/> = 0 → auto-cluster; &gt; 0 → force
    /// that many speakers (some models use it, others ignore).
    /// </summary>
    Task<DiarizationResult> DiarizeAsync(byte[] pcm16, int hintSpeakerCount = 0, CancellationToken ct = default);
}
