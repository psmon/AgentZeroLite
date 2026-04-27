namespace Agent.Common.Voice;

/// <summary>
/// Stable string identifiers for STT (speech-to-text) providers. Stored as
/// strings in <see cref="VoiceSettings.SttProvider"/> so the JSON file remains
/// readable even when new providers are added.
///
/// AgentZeroLite differentiates from the AgentWin origin by exposing local
/// embedded models (Whisper.net + Gemma audio variants) alongside cloud APIs;
/// new built-in voices/STT models may be added over time.
/// </summary>
public static class SttProviderNames
{
    /// <summary>Whisper.net offline model (tiny / small / medium GGML).</summary>
    public const string WhisperLocal = "WhisperLocal";

    /// <summary>OpenAI cloud Whisper API (whisper-1).</summary>
    public const string OpenAIWhisper = "OpenAIWhisper";

    /// <summary>
    /// Webnori-hosted multimodal Gemma endpoint. Re-uses the LLM-tab Webnori
    /// credentials (no duplicate API key here) and the model picker on this
    /// tab is filtered to audio-capable Webnori models.
    /// </summary>
    public const string WebnoriGemma = "WebnoriGemma";

    /// <summary>
    /// On-device Gemma audio variant loaded via the same self-built llama.dll
    /// runtime AgentZeroLite already ships. Active when an audio-capable GGUF
    /// is present in <c>LlmModelCatalog</c>; the local LLM must be loaded.
    /// </summary>
    public const string LocalGemma = "LocalGemma";
}

/// <summary>Stable string identifiers for TTS (text-to-speech) providers.</summary>
public static class TtsProviderNames
{
    /// <summary>Voice output disabled. Default for fresh installs.</summary>
    public const string Off = "Off";

    /// <summary>Windows SAPI via System.Speech.Synthesis (offline, free).</summary>
    public const string WindowsTts = "WindowsTTS";

    /// <summary>OpenAI tts-1 cloud API.</summary>
    public const string OpenAITts = "OpenAITTS";
}

/// <summary>
/// Voice (STT/TTS + microphone capture) configuration. Persisted as JSON next
/// to the LLM settings — see <see cref="VoiceSettingsStore"/>. Voice prompts
/// are routed to the LLM that the user picked on the LLM tab via
/// <c>LlmGateway.OpenSession()</c>; this class therefore does NOT carry a
/// separate "voice LLM" provider — that's a deliberate split from the origin.
/// </summary>
public sealed class VoiceSettings
{
    // ── STT ──────────────────────────────────────────────────────────────
    public string SttProvider { get; set; } = SttProviderNames.WhisperLocal;

    /// <summary>Whisper GGML size: "tiny" | "small" | "medium" (WhisperLocal only).</summary>
    public string SttWhisperModel { get; set; } = "small";

    /// <summary>BCP-47 short tag or "auto". Origin defaults: auto/ko/en/ja/zh.</summary>
    public string SttLanguage { get; set; } = "auto";

    /// <summary>
    /// Try CUDA when loading Whisper.net. Falls back to CPU automatically if
    /// the runtime isn't available — purely an opt-in performance hint.
    /// </summary>
    public bool SttUseGpu { get; set; } = false;

    /// <summary>OpenAIWhisper provider API key. Stored even when another provider is active.</summary>
    public string SttOpenAIApiKey { get; set; } = "";

    /// <summary>
    /// Catalog id of the audio-capable local Gemma GGUF when LocalGemma is the
    /// active provider. Empty until the user picks one — the model list is
    /// derived at runtime from <c>LlmModelCatalog</c> entries flagged as audio.
    /// </summary>
    public string SttLocalGemmaModelId { get; set; } = "";

    /// <summary>
    /// Selected model id when WebnoriGemma is the active provider. Refresh on
    /// the Voice tab fetches the audio-capable subset of Webnori's model list.
    /// </summary>
    public string SttWebnoriModel { get; set; } = "";

    // ── TTS ──────────────────────────────────────────────────────────────
    public string TtsProvider { get; set; } = TtsProviderNames.Off;

    /// <summary>Provider-specific voice id (e.g. "Microsoft Heami" / "alloy").</summary>
    public string TtsVoice { get; set; } = "";

    /// <summary>OpenAITTS provider API key. Stored even when another provider is active.</summary>
    public string TtsOpenAIApiKey { get; set; } = "";

    // ── Capture / VAD ────────────────────────────────────────────────────

    /// <summary>NAudio device index serialised as string. Empty → system default.</summary>
    public string InputDeviceId { get; set; } = "";

    /// <summary>
    /// Amplitude threshold (0–100) below which audio is treated as silence.
    /// The Voice Test slider exposes the inverse ("sensitivity"): UI 100 →
    /// stored 0, UI 0 → stored 100. Origin proven default is 25.
    /// </summary>
    public int VadThreshold { get; set; } = 25;

    /// <summary>true = VAD auto-segment by silence, false = manual START/STOP.</summary>
    public bool VoiceTestAutoMode { get; set; } = true;
}
