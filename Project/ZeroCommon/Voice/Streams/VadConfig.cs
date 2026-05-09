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
///
/// <para><b>MaxUtteranceSeconds</b> caps how long a single utterance can
/// grow before it is force-emitted to STT, even if the silence hangover
/// has not been reached. Defaults to 0 (disabled) for back-compat. The
/// real-world failure mode this guards against (M0015 / 후속 진행 #2):
/// at near-floor threshold + ambient noise the hangover never trips and
/// a single utterance balloons to tens of seconds, starving the STT
/// queue and producing one giant transcription instead of natural
/// sentence boundaries.</para>
/// </summary>
public sealed record VadConfig(
    float VadThreshold,
    double PreRollSeconds = 1.0,
    int UtteranceHangoverFrames = 40,
    double MaxUtteranceSeconds = 0.0);
