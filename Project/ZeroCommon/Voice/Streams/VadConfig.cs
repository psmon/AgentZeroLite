namespace Agent.Common.Voice.Streams;

/// <summary>
/// Static configuration for the segmenter Flow. Captured once at graph
/// materialization; runtime changes require re-materializing the graph.
///
/// VadThreshold is the same RMS amplitude (0~1) the legacy
/// <c>VoiceCaptureService</c> applies — sensitivity-to-threshold mapping
/// stays in <c>VoiceRuntimeFactory.SensitivityToThreshold</c>.
///
/// Default frame budget assumes 50 ms WaveIn buffers (Origin-proven), so
/// 40 frames ≈ 2 s of silence before declaring an utterance complete. The
/// pre-roll ring captures the second of audio leading up to the VAD
/// trigger so the first consonant survives.
/// </summary>
public sealed record VadConfig(
    float VadThreshold,
    double PreRollSeconds = 1.0,
    int UtteranceHangoverFrames = 40);
