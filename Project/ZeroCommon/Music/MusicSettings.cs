namespace Agent.Common.Music;

/// <summary>
/// Stable string identifiers for music-classification providers. Mirrors the
/// SttProviderNames pattern so future on-device music models (MERT, CLAP) can
/// be added without breaking persisted settings.
/// </summary>
public static class MusicClassifierProviderNames
{
    /// <summary>
    /// MIT/ast-finetuned-audioset-10-10-0.4593 exported to ONNX. AudioSet 527
    /// classes incl. dozens of musical-instrument labels. 10-second / 16 kHz
    /// mono input. Locked as the only provider for the initial scaffold.
    /// </summary>
    public const string AstAudioSet = "ASTAudioSet";
}

/// <summary>
/// Audio capture sources for the Music Test. <c>Microphone</c> uses NAudio
/// <c>WaveInEvent</c> (existing voice pipeline); <c>SystemLoopback</c> uses
/// WASAPI loopback against a render endpoint — the right pick when the user
/// wants to analyse music coming out of the same PC (Spotify, YouTube,
/// game audio) without playing it through speakers + back into a mic.
/// </summary>
public static class MusicInputSourceNames
{
    public const string Microphone = "Microphone";
    public const string SystemLoopback = "SystemLoopback";
}

/// <summary>
/// Music-LLM (instrument classification + spectrum analysis) configuration.
/// Persisted as <c>music-settings.json</c> next to <c>voice-settings.json</c>
/// under <c>%LOCALAPPDATA%\AgentZeroLite\</c>.
///
/// First iteration ships a single fixed provider (AST AudioSet ONNX) — the
/// settings class is shaped to mirror VoiceSettings so adding MERT / CLAP
/// later is a same-shape extension, not a redesign.
/// </summary>
public sealed class MusicSettings
{
    /// <summary>Currently locked to <see cref="MusicClassifierProviderNames.AstAudioSet"/>.</summary>
    public string Provider { get; set; } = MusicClassifierProviderNames.AstAudioSet;

    /// <summary>
    /// Absolute path to the AST ONNX model file. Empty → use the convention
    /// path under <c>%LOCALAPPDATA%\AgentZeroLite\models\ast-audioset\model.onnx</c>.
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// Absolute path to AudioSet's <c>class_labels_indices.csv</c>. Empty →
    /// convention path beside the model file. Missing labels file degrades
    /// the test output to numeric indices instead of human-readable names.
    /// </summary>
    public string LabelsPath { get; set; } = "";

    /// <summary>
    /// Where audio comes from when the Test button is pressed.
    /// One of <see cref="MusicInputSourceNames"/>. Defaults to Microphone so
    /// existing test flows keep working without a settings migration.
    /// </summary>
    public string InputSource { get; set; } = MusicInputSourceNames.Microphone;

    /// <summary>NAudio WaveIn device index serialised as string. Empty → system default mic.</summary>
    public string InputDeviceId { get; set; } = "";

    /// <summary>
    /// MMDevice render-endpoint ID (e.g. <c>{0.0.0.00000000}.{guid}</c>) used
    /// when <see cref="InputSource"/> is <c>SystemLoopback</c>. Empty → default
    /// render device (whatever Windows is currently playing audio out of).
    /// </summary>
    public string LoopbackDeviceId { get; set; } = "";

    /// <summary>Capture window (seconds) used by the Music Test button. AST is trained on 10 s clips.</summary>
    public int TestDurationSeconds { get; set; } = 10;

    /// <summary>Top-K labels surfaced in the Test panel after inference.</summary>
    public int TopK { get; set; } = 5;
}
