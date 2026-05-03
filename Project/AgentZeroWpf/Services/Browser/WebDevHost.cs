using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Llm;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.Services.Browser;

public sealed record SummarizeResult(bool Ok, string? Summary, int InputChars, int Chunks, string? Error);

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

    // ─── Voice-note (VAD-gated STT) state ───────────────────────────────
    // The plugin facade: Start → VoiceCaptureService runs with PCM buffering;
    // each UtteranceEnded auto-transcribes via the active STT provider and
    // raises NoteTranscript. Pause/Resume just toggles Muted (capture stays
    // alive so the level meter on the plugin side keeps reading).
    private readonly SemaphoreSlim _noteLock = new(1, 1);
    private VoiceCaptureService? _noteCapture;
    private ISpeechToText?       _noteStt;
    private string               _noteLanguage = "auto";
    private CancellationTokenSource? _noteCts;

    public event Action<string>? NoteTranscript;     // (text)
    public event Action?         NoteUtteranceStarted;
    public event Action?         NoteUtteranceEnded;
    public event Action<string>? NoteError;          // (message)
    public event Action<float>?  NoteAmplitude;      // (rms 0..1) — throttled ~10 Hz
    public event Action<bool>?   NoteSpeaking;       // frame-level VAD decision (faster than utterance)

    public float CurrentVadThreshold => _noteCapture?.VadThreshold ?? 0f;

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

    // ─────────────────────────────────────────────────────────────────
    //  Voice-note plugin surface
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begin VAD-gated capture. Each utterance (~2s of trailing silence
    /// closes a segment) is auto-transcribed and pushed via
    /// <see cref="NoteTranscript"/>. Idempotent — a second call while
    /// already running returns the same payload without restarting capture.
    ///
    /// Returns the effective sensitivity / threshold so JS can sync its
    /// slider — important when sensitivityPercent is null and we fall
    /// back to the user's Settings/Voice VadThreshold.
    /// </summary>
    public async Task<object> StartNoteCaptureAsync(int? sensitivityPercent = null)
    {
        await _noteLock.WaitAsync();
        try
        {
            var v = VoiceSettingsStore.Load();

            // Effective sensitivity:
            //   • caller passed one → respect it (slider)
            //   • else use Settings/Voice's stored VadThreshold (origin
            //     proven default 25 → sensitivity 75) so the plugin
            //     inherits the value the user has already tuned for
            //     their mic.
            int effectiveSens = sensitivityPercent.HasValue
                ? Math.Clamp(sensitivityPercent.Value, 0, 100)
                : Math.Clamp(100 - v.VadThreshold, 0, 100);
            float threshold = VoiceRuntimeFactory.SensitivityToThreshold(effectiveSens);

            if (_noteCapture is not null)
            {
                // Already running — let the caller know the live values.
                return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold };
            }

            _noteStt = VoiceRuntimeFactory.BuildStt(v);
            if (_noteStt is null)
            {
                AppLogger.Log("[WebDev:Note] start aborted — STT provider is Off");
                NoteError?.Invoke("STT provider is Off — pick one in Settings/Voice first");
                return new { ok = false, error = "stt-off" };
            }
            try
            {
                AppLogger.Log($"[WebDev:Note] STT preload | provider={v.SttProvider} model={v.SttWhisperModel} gpu={v.SttUseGpu}");
                var ready = await _noteStt.EnsureReadyAsync();
                if (!ready)
                {
                    NoteError?.Invoke("STT model not ready — open Settings → Voice and complete model setup");
                    _noteStt = null;
                    return new { ok = false, error = "stt-not-ready" };
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Note] STT preload failed: {ex.GetType().Name}: {ex.Message}");
                NoteError?.Invoke("STT init failed: " + ex.Message);
                _noteStt = null;
                return new { ok = false, error = ex.Message };
            }

            var cap = new VoiceCaptureService
            {
                BufferPcm = true,
                Muted = v.MicMuted,
                VadThreshold = threshold,
            };

            cap.UtteranceStarted += OnNoteUtteranceStarted;
            cap.UtteranceEnded   += OnNoteUtteranceEnded;
            cap.AmplitudeChanged += OnNoteAmplitude;
            cap.SpeakingStateChanged += OnNoteSpeaking;
            try
            {
                var deviceNumber = VoiceRuntimeFactory.ParseDeviceNumber(v.InputDeviceId);
                cap.Start(deviceNumber);
            }
            catch (Exception ex)
            {
                cap.Dispose();
                AppLogger.Log($"[WebDev:Note] mic capture failed: {ex.GetType().Name}: {ex.Message}");
                NoteError?.Invoke("Mic capture failed: " + ex.Message);
                return new { ok = false, error = ex.Message };
            }

            _noteCts = new CancellationTokenSource();
            _noteCapture = cap;
            // Cache the user's chosen STT language so every utterance
            // transcribes the same way Settings/Voice does. Whisper's
            // "auto" detector is unreliable on short utterances —
            // omitting it was why transcripts came back empty even
            // though Settings/Voice (which always passes v.SttLanguage)
            // was working on the same mic.
            _noteLanguage = string.IsNullOrWhiteSpace(v.SttLanguage) ? "auto" : v.SttLanguage;
            AppLogger.Log($"[WebDev:Note] capture started | sens={effectiveSens} threshold={threshold:F4} lang={_noteLanguage} muted={cap.Muted} device={v.InputDeviceId}");
            return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold };
        }
        finally { _noteLock.Release(); }
    }

    public async Task StopNoteCaptureAsync()
    {
        await _noteLock.WaitAsync();
        try { TearDownNoteCaptureLocked(); }
        finally { _noteLock.Release(); }
    }

    public void PauseNoteCapture()
    {
        var c = _noteCapture; if (c is not null) c.Muted = true;
    }

    public void ResumeNoteCapture()
    {
        var c = _noteCapture; if (c is not null) c.Muted = false;
    }

    public void SetNoteSensitivity(int percent)
    {
        var c = _noteCapture; if (c is null) return;
        c.VadThreshold = VoiceRuntimeFactory.SensitivityToThreshold(percent);
    }

    public bool IsNoteCapturing => _noteCapture is { IsCapturing: true };

    private void OnNoteUtteranceStarted()
    {
        // Seed the buffer with the pre-roll ring (~1s) so the first
        // consonant of the utterance isn't clipped — this is the same
        // step Settings/Voice does, missing it here was why short
        // utterances were producing empty STT results.
        _noteCapture?.SeedBufferWithPreRoll();
        AppLogger.Log("[WebDev:Note] utterance started");
        NoteUtteranceStarted?.Invoke();
    }

    private void OnNoteSpeaking(bool isSpeaking)
    {
        NoteSpeaking?.Invoke(isSpeaking);
    }

    private void OnNoteUtteranceEnded()
    {
        NoteUtteranceEnded?.Invoke();
        var cap = _noteCapture; var stt = _noteStt; var ctsToken = _noteCts?.Token ?? CancellationToken.None;
        var lang = _noteLanguage;
        if (cap is null || stt is null) return;
        var pcm = cap.ConsumePcmBuffer();
        // Filter sub-half-second utterances — 16 kHz mono 16-bit = 32 KB/s,
        // so < 8000 bytes ≈ < 250 ms. Whisper just returns junk on those
        // and we'd surface noise lines in the timeline.
        if (pcm.Length < 8000)
        {
            AppLogger.Log($"[WebDev:Note] utterance dropped — too short ({pcm.Length} bytes)");
            return;
        }
        AppLogger.Log($"[WebDev:Note] utterance → STT | bytes={pcm.Length} lang={lang}");
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await stt.TranscribeAsync(pcm, lang, ctsToken);
                if (ctsToken.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(text))
                {
                    AppLogger.Log($"[WebDev:Note] STT returned empty | bytes={pcm.Length} lang={lang}");
                    return;
                }
                AppLogger.Log($"[WebDev:Note] STT ok | chars={text.Length}");
                NoteTranscript?.Invoke(text.Trim());
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Note] STT transcribe failed: {ex.GetType().Name}: {ex.Message}");
                NoteError?.Invoke("Transcribe failed: " + ex.Message);
            }
        });
    }

    // Throttled amplitude relay — VoiceCaptureService fires ~20 Hz which
    // is more than the JS UI needs. Coalesce to ~10 Hz to keep WebView2
    // event traffic light without sacrificing meter responsiveness.
    private long _lastAmpTick;
    private void OnNoteAmplitude(float rms)
    {
        var now = Environment.TickCount64;
        if (now - _lastAmpTick < 90) return;
        _lastAmpTick = now;
        NoteAmplitude?.Invoke(rms);
    }

    private void TearDownNoteCaptureLocked()
    {
        var cap = _noteCapture; _noteCapture = null;
        if (cap is not null)
        {
            cap.UtteranceStarted -= OnNoteUtteranceStarted;
            cap.UtteranceEnded   -= OnNoteUtteranceEnded;
            cap.AmplitudeChanged -= OnNoteAmplitude;
            cap.SpeakingStateChanged -= OnNoteSpeaking;
            try { cap.Stop(); } catch { }
            try { cap.Dispose(); } catch { }
        }
        try { _noteCts?.Cancel(); } catch { }
        _noteCts?.Dispose();
        _noteCts = null;
        _noteStt = null;
        AppLogger.Log("[WebDev:Note] capture stopped");
    }

    /// <summary>
    /// LLM-backed text summarization. No tokenizer in <see cref="LlmGateway"/>,
    /// so chunking uses character length as a proxy: text longer than
    /// <paramref name="maxChars"/> is split in half (on a sentence boundary
    /// when possible), each half summarized recursively, then the partial
    /// summaries are joined and summarized one more time. Default
    /// <paramref name="maxChars"/> is 6000 — well below the 8k-token range
    /// most local backends expose.
    /// </summary>
    public async Task<SummarizeResult> SummarizeAsync(string text, int maxChars = 6000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SummarizeResult(false, null, 0, 0, "empty input");
        if (!LlmGateway.IsActiveAvailable())
            return new SummarizeResult(false, null, text.Length, 0, "Active LLM backend not ready — open Settings → LLM.");

        try
        {
            int chunks = 0;
            var summary = await SummarizeRecursiveAsync(text.Trim(), maxChars, depth: 0, chunkCounter: c => chunks = c, ct);
            AppLogger.Log($"[WebDev:Note] summarize ok | inputChars={text.Length} chunks={chunks} outChars={summary?.Length ?? 0}");
            return new SummarizeResult(true, summary, text.Length, chunks, null);
        }
        catch (OperationCanceledException) { return new SummarizeResult(false, null, text.Length, 0, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Note] summarize failed: {ex.GetType().Name}: {ex.Message}");
            return new SummarizeResult(false, null, text.Length, 0, ex.Message);
        }
    }

    private async Task<string> SummarizeRecursiveAsync(string text, int maxChars, int depth, Action<int> chunkCounter, CancellationToken ct)
    {
        if (depth > 6) return text; // hard recursion ceiling
        if (text.Length <= maxChars)
        {
            chunkCounter(1);
            return await SummarizeChunkAsync(text, ct);
        }
        // Split in halves on the closest sentence boundary to the midpoint.
        int mid = text.Length / 2;
        int split = FindSentenceBoundary(text, mid);
        var left  = text.Substring(0, split).Trim();
        var right = text.Substring(split).Trim();

        var sLeft  = await SummarizeRecursiveAsync(left,  maxChars, depth + 1, chunkCounter, ct);
        var sRight = await SummarizeRecursiveAsync(right, maxChars, depth + 1, chunkCounter, ct);
        var merged = sLeft + "\n\n" + sRight;

        // One more pass to consolidate.
        return await SummarizeChunkAsync(merged, ct);
    }

    private async Task<string> SummarizeChunkAsync(string chunk, CancellationToken ct)
    {
        await _chatLock.WaitAsync(ct);
        try
        {
            // Use a one-shot session so prior chat history doesn't pollute
            // the summary. Disposing it here also prevents the cached
            // _chatSession (used by chat.send) from being mutated.
            await using var session = LlmGateway.OpenSession();
            var prompt = "다음 음성노트 텍스트를 핵심만 간결하게 한국어로 요약해줘. 글머리 기호 또는 짧은 단락으로:\n\n" + chunk;
            return (await session.SendAsync(prompt, ct)).Trim();
        }
        finally { _chatLock.Release(); }
    }

    private static int FindSentenceBoundary(string text, int around)
    {
        // Look ±200 chars from `around` for a sentence terminator. Falls
        // back to `around` when the window has no clean break.
        int radius = Math.Min(200, text.Length / 4);
        int lo = Math.Max(0, around - radius);
        int hi = Math.Min(text.Length - 1, around + radius);
        for (int i = around; i <= hi; i++)
            if (text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == '\n') return i + 1;
        for (int i = around - 1; i >= lo; i--)
            if (text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == '\n') return i + 1;
        return around;
    }

    public void Dispose()
    {
        try { TearDownNoteCaptureLocked(); } catch { }
        _noteLock.Dispose();
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
