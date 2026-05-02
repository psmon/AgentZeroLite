namespace Agent.Common.Browser;

/// <summary>
/// Native bridge contract exposed to AgentZero's in-app browser (WebView2).
/// Sandboxed web apps under <c>Wasm/</c> call these methods via the JS bridge
/// (<c>window.zero.invoke</c>) instead of going over the network. Lives in
/// ZeroCommon so future WASM plugins can reference the contract without
/// pulling WPF.
/// </summary>
public interface IZeroBrowser
{
    /// <summary>App version string from <c>version.txt</c>.</summary>
    string GetAppVersion();

    /// <summary>Currently configured voice providers (STT / TTS / LLM backend).</summary>
    VoiceProvidersInfo GetVoiceProviders();

    /// <summary>Synthesize <paramref name="text"/> with the active TTS provider and play it back.</summary>
    Task<TtsResult> SpeakAsync(string text, CancellationToken ct = default);

    /// <summary>Stop in-flight playback.</summary>
    void StopSpeaking();

    /// <summary>Snapshot of the active LLM backend the chat ops will route through.</summary>
    LlmStatusInfo GetLlmStatus();

    /// <summary>
    /// Single-shot chat against the active backend. Maintains one persistent
    /// session per host instance so successive calls share turn history;
    /// call <see cref="ResetChatAsync"/> to start over.
    /// </summary>
    Task<ChatResult> ChatSendAsync(string text, CancellationToken ct = default);

    /// <summary>Streaming variant of <see cref="ChatSendAsync"/>.</summary>
    IAsyncEnumerable<string> ChatStreamAsync(string text, CancellationToken ct = default);

    /// <summary>Drop the persistent chat session so the next send starts fresh.</summary>
    Task ResetChatAsync();
}

/// <summary>Snapshot of the user's voice config — read-only view for the web UI.</summary>
public sealed record VoiceProvidersInfo(string Stt, string Tts, string LlmBackend);

/// <summary>Result of a TTS call. <see cref="Ok"/> false means <see cref="Error"/> is populated.</summary>
public sealed record TtsResult(bool Ok, string? Provider, int Bytes, string? Format, string? Error);

/// <summary>
/// Active LLM backend snapshot. <see cref="Available"/> is false when no
/// backend is ready (Local not loaded, External missing key/model). The web
/// UI surfaces <see cref="Detail"/> so the user knows what to fix.
/// </summary>
public sealed record LlmStatusInfo(bool Available, string Backend, string Model, string Detail);

/// <summary>Single-shot chat reply. <see cref="Ok"/> false means <see cref="Error"/> is populated.</summary>
public sealed record ChatResult(bool Ok, string? Reply, int Turn, string? Error);
