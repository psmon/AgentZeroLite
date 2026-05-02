using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Llm;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Default <see cref="IZeroBrowser"/> implementation. Reuses the existing
/// VoiceRuntimeFactory + VoicePlaybackService so the WebDev sandbox shares
/// the same TTS/STT pipeline the Settings/Voice tab already drives.
/// </summary>
public sealed class WebDevHost : IZeroBrowser, IDisposable
{
    private readonly VoicePlaybackService _playback = new();

    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private ILocalChatSession? _chatSession;

    public string GetAppVersion()
    {
        try { return Agent.Common.Module.AppVersionProvider.GetDisplayVersion(); }
        catch { return "v?"; }
    }

    public VoiceProvidersInfo GetVoiceProviders()
    {
        var v = VoiceSettingsStore.Load();
        var llm = TryReadLlmBackendName();
        return new VoiceProvidersInfo(
            Stt: string.IsNullOrWhiteSpace(v.SttProvider) ? "off" : v.SttProvider,
            Tts: string.IsNullOrWhiteSpace(v.TtsProvider) ? "off" : v.TtsProvider,
            LlmBackend: llm);
    }

    public async Task<TtsResult> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult(false, null, 0, null, "empty text");

        var v = VoiceSettingsStore.Load();
        var tts = VoiceRuntimeFactory.BuildTts(v);
        if (tts is null)
            return new TtsResult(false, v.TtsProvider, 0, null, "TTS provider is Off — pick one in Settings/Voice first");

        try
        {
            var voiceId = ResolveVoiceId(v);
            var bytes = await tts.SynthesizeAsync(text, voiceId, ct);
            if (bytes is null || bytes.Length == 0)
                return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, "synthesis returned 0 bytes");

            _playback.Play(bytes, tts.AudioFormat);
            AppLogger.Log($"[WebDev] TTS OK | provider={tts.ProviderName} bytes={bytes.Length} fmt={tts.AudioFormat}");
            return new TtsResult(true, tts.ProviderName, bytes.Length, tts.AudioFormat, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] TTS failed: {ex.GetType().Name}: {ex.Message}");
            return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, ex.Message);
        }
    }

    public void StopSpeaking() => _playback.Stop();

    public LlmStatusInfo GetLlmStatus()
    {
        try
        {
            var s = LlmSettingsStore.Load();
            var available = LlmGateway.IsActiveAvailable();
            var backend = s.ActiveBackend.ToString();
            string model;
            string detail;
            if (s.ActiveBackend == LlmActiveBackend.Local)
            {
                model = LlmModelCatalog.FindById(s.ModelId).DisplayName;
                detail = available
                    ? "Local model loaded — ready."
                    : "Local model not loaded — open Settings → LLM and click Load.";
            }
            else
            {
                model = string.IsNullOrEmpty(s.ResolveExternalModel()) ? "(none)" : s.ResolveExternalModel();
                detail = available
                    ? $"External · {s.External.Provider} ready."
                    : $"External · {s.External.Provider} not configured — open Settings → LLM → External.";
            }
            return new LlmStatusInfo(available, backend, model, detail);
        }
        catch (Exception ex)
        {
            return new LlmStatusInfo(false, "unknown", "?", "Failed to read LLM settings: " + ex.Message);
        }
    }

    public async Task<ChatResult> ChatSendAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ChatResult(false, null, 0, "empty prompt");

        if (!LlmGateway.IsActiveAvailable())
            return new ChatResult(false, null, 0, "Active LLM backend not ready — open Settings → LLM.");

        await _chatLock.WaitAsync(ct);
        try
        {
            var session = EnsureSession();
            var reply = await session.SendAsync(text, ct);
            AppLogger.Log($"[WebDev] Chat OK | turn={session.TurnCount} replyChars={(reply?.Length ?? 0)}");
            return new ChatResult(true, reply, session.TurnCount, null);
        }
        catch (OperationCanceledException) { return new ChatResult(false, null, 0, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] Chat failed: {ex.GetType().Name}: {ex.Message}");
            return new ChatResult(false, null, 0, ex.Message);
        }
        finally { _chatLock.Release(); }
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (!LlmGateway.IsActiveAvailable())
            throw new InvalidOperationException("Active LLM backend not ready — open Settings → LLM.");

        await _chatLock.WaitAsync(ct);
        try
        {
            var session = EnsureSession();
            await foreach (var tok in session.SendStreamAsync(text, ct))
                yield return tok;
            AppLogger.Log($"[WebDev] Chat stream OK | turn={session.TurnCount}");
        }
        finally { _chatLock.Release(); }
    }

    public async Task ResetChatAsync()
    {
        await _chatLock.WaitAsync();
        try
        {
            var s = _chatSession;
            _chatSession = null;
            if (s is not null)
            {
                try { await s.DisposeAsync(); } catch { }
            }
            AppLogger.Log("[WebDev] Chat session reset");
        }
        finally { _chatLock.Release(); }
    }

    private ILocalChatSession EnsureSession()
    {
        // Caller holds _chatLock. Lazy + cached for the lifetime of this host.
        return _chatSession ??= LlmGateway.OpenSession();
    }

    public void Dispose()
    {
        _playback.Dispose();
        try { _chatSession?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _chatSession = null;
        _chatLock.Dispose();
    }

    private static string ResolveVoiceId(VoiceSettings v)
    {
        if (!string.IsNullOrWhiteSpace(v.TtsVoice)) return v.TtsVoice;
        return v.TtsProvider == TtsProviderNames.OpenAITts ? "alloy" : string.Empty;
    }

    private static string TryReadLlmBackendName()
    {
        try
        {
            var s = Agent.Common.Llm.LlmSettingsStore.Load();
            return s.ActiveBackend.ToString();
        }
        catch { return "unknown"; }
    }
}
