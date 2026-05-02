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
}

/// <summary>Snapshot of the user's voice config — read-only view for the web UI.</summary>
public sealed record VoiceProvidersInfo(string Stt, string Tts, string LlmBackend);

/// <summary>Result of a TTS call. <see cref="Ok"/> false means <see cref="Error"/> is populated.</summary>
public sealed record TtsResult(bool Ok, string? Provider, int Bytes, string? Format, string? Error);
