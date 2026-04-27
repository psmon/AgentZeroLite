namespace Agent.Common.Voice;

/// <summary>
/// Speech-to-Text provider abstraction. Implementations convert PCM audio to
/// text. Ported verbatim from the AgentWin origin so concrete providers
/// (Whisper.net, OpenAI Whisper, Webnori-Gemma audio, on-device Gemma audio)
/// can be added behind a single switch without UI churn.
/// </summary>
public interface ISpeechToText
{
    string ProviderName { get; }

    /// <summary>
    /// Resolve any one-time prerequisites (download model, verify credentials)
    /// before <see cref="TranscribeAsync"/> is first called.
    /// </summary>
    Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Transcribe 16-bit 16 kHz mono PCM audio to text.</summary>
    Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default);
}
