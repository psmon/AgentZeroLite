using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Llm;
using Agent.Common.Voice;
using Agent.Common.Voice.Diarization;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.Services.Browser;

public sealed record SummarizeResult(bool Ok, string? Summary, int InputChars, int Chunks, string? Error);

/// <summary>
/// M0026 — YouTube oEmbed metadata, fetched host-side so the Agent Band
/// plugin avoids browser CORS on <c>youtube.com/oembed</c>. SSRF-safe:
/// the caller passes only an 11-char video id, and the host rebuilds the
/// canonical <c>watch?v=</c> URL itself — no arbitrary host is ever contacted.
/// </summary>
public sealed record OEmbedResult(bool Ok, string? VideoId, string? Title, string? Author, string? Thumbnail, string? Error);

/// <summary>
/// M0026 — result of a stateless one-shot LLM classification. Does NOT
/// touch the persistent <c>chat.*</c> session. <see cref="Category"/> is
/// always one of the caller-supplied categories (host clamps the model's
/// free-text reply to the whitelist; falls back to the last category).
/// </summary>
public sealed record ClassifyResult(bool Ok, string? Category, string? Raw, string? Error);

/// <summary>
/// One transcript line emitted by the voice-note pipeline (M0024 Phase 3).
/// <see cref="Text"/> is the recognised speech; speaker fields are optional
/// (null when diarization is Off). <see cref="IsPartial"/> distinguishes
/// the 10 s rolling preview from the final utterance — partials reuse the
/// same event so plugins that don't care about the distinction render them
/// as ordinary lines.
/// </summary>
public sealed record NoteTranscriptInfo(
    string Text,
    int? SpeakerId,
    string? SpeakerLabel,
    bool IsPartial);

/// <summary>
/// Capture source for the voice-note pipeline (M0024 Phase 3.5).
/// Mirrors the Voice tab's <c>VoiceInputSourceNames</c> so plugins can
/// pass the same constant the user picked in Settings/Voice.
/// </summary>
public static class NoteSourceNames
{
    public const string Microphone = "Microphone";
    public const string SystemLoopback = "SystemLoopback";
}

/// <summary>
/// Options bag for <c>note.start</c> — extended in Phase 3.5 from the
/// original sensitivity-only call. JS callers pass any subset; null
/// fields fall back to safe defaults (mic source + Settings/Voice
/// sensitivity + 30 s loopback chunk).
/// </summary>
public sealed record NoteStartOptions(
    int? Sensitivity = null,
    string? Source = null,
    string? LoopbackDeviceId = null,
    int? LoopbackChunkSec = null);

/// <summary>
/// Default <see cref="IZeroBrowser"/> implementation. Reuses the existing
/// VoiceRuntimeFactory + VoicePlaybackService so the WebDev sandbox shares
/// the same TTS/STT pipeline the Settings/Voice tab already drives.
/// </summary>
public sealed partial class WebDevHost : IZeroBrowser, IDisposable
{
    private readonly VoicePlaybackService _playback = new();

    // M0026 — shared client for host-side YouTube oEmbed lookups. Short
    // timeout so a slow network never wedges the plugin; the video still
    // plays even if metadata never arrives.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private ILocalChatSession? _chatSession;

    // ─── Voice-note (VAD-gated STT) state ───────────────────────────────
    // The plugin facade: Start → VoiceCaptureService runs with PCM buffering;
    // each UtteranceEnded auto-transcribes via the active STT provider and
    // raises NoteTranscript. Pause/Resume just toggles Muted (capture stays
    // alive so the level meter on the plugin side keeps reading).
    private readonly SemaphoreSlim _noteLock = new(1, 1);
    private VoiceCaptureService? _noteCapture;
    // M0024 Phase 3.5 — voice-note gains a second capture path (WASAPI
    // loopback) for analysing system playback (Zoom recording, YouTube,
    // music) in addition to the existing mic. Only one of the two is
    // populated at any time per StartNoteCaptureAsync call.
    private AgentZeroWpf.Services.Music.LoopbackCaptureService? _noteLoopback;
    private string _noteSource = NoteSourceNames.Microphone;
    private int _noteLoopbackChunkSec = 30;
    private System.Threading.Timer? _noteChunkTimer; // fires every _noteLoopbackChunkSec for loopback path
    private ISpeechToText?       _noteStt;
    private string               _noteLanguage = "auto";
    private CancellationTokenSource? _noteCts;

    // M0024 Phase 3 — diarizer for speaker labels on the note pipeline.
    // Lazily created on first utterance; null when DiarizationSettings is Off.
    // Independent ONNX session from Settings/Voice/Test (each runs its own
    // inference; the model file on disk is shared).
    private SherpaSpeakerDiarizer? _noteDiarizer;

    // M0024 Phase 3 — partial transcript timer for the note pipeline. Mirrors
    // the Settings/Voice/Test pattern (Phase 2.5): every 10 s during active
    // capture, peek the buffer and emit a partial NoteTranscript with
    // IsPartial=true. Plugins render the partial dimmer so the user sees
    // recording is alive before the silence-segmented utterance closes.
    private System.Threading.Timer? _notePartialTimer;
    private int _notePartialBusy;
    private int _notePartialLastBytes;
    private const int NotePartialIntervalMs = 10_000;
    private const int NotePartialMinBytes = 16_000 * 2 * 2; // 2 s of 16 kHz mono PCM16

    public event Action<NoteTranscriptInfo>? NoteTranscript;  // M0024 Phase 3 — rich payload
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
    /// Begin capture. Two paths share this entry point (M0024 Phase 3.5):
    ///
    ///   • <b>Microphone</b> — VAD-gated; each ~2 s of trailing silence
    ///     closes an utterance → auto-transcribe → push via NoteTranscript.
    ///   • <b>SystemLoopback</b> — continuous WASAPI loopback against the
    ///     current render endpoint (or one the user picked). No VAD; a
    ///     30 s chunk timer drains the buffer and fires NoteTranscript as
    ///     if it were one utterance.
    ///
    /// Both paths share the same partial-transcript timer (10 s rolling
    /// preview) and the same Sherpa diarizer.
    ///
    /// Idempotent — a second call while already running returns the same
    /// payload without restarting capture.
    ///
    /// Returns the effective sensitivity / threshold / source so JS can
    /// sync its UI.
    /// </summary>
    public async Task<object> StartNoteCaptureAsync(NoteStartOptions? opts = null)
    {
        opts ??= new NoteStartOptions();
        await _noteLock.WaitAsync();
        try
        {
            var v = VoiceSettingsStore.Load();

            var source = string.IsNullOrEmpty(opts.Source) ? NoteSourceNames.Microphone : opts.Source!;
            if (source != NoteSourceNames.Microphone && source != NoteSourceNames.SystemLoopback)
                source = NoteSourceNames.Microphone;

            // Effective sensitivity (mic only — loopback has no VAD):
            //   • caller passed one → respect it (slider)
            //   • else use Settings/Voice's stored VadThreshold (origin
            //     proven default 25 → sensitivity 75) so the plugin
            //     inherits the value the user has already tuned for
            //     their mic.
            int effectiveSens = opts.Sensitivity.HasValue
                ? Math.Clamp(opts.Sensitivity.Value, 0, 100)
                : Math.Clamp(100 - v.VadThreshold, 0, 100);
            // M0015 후속 진행 #1: voice-note plugin captures lecture-style
            // audio (distant speaker, ambient noise) — that's exactly the
            // Loose-profile use case. Pass isAgentMode: true so the saved
            // "Auto" token resolves to Loose here. Explicit Strict / Loose
            // tokens still win.
            float threshold = VoiceRuntimeFactory.SensitivityToThreshold(
                effectiveSens, v, isAgentMode: true);

            if (_noteCapture is not null || _noteLoopback is not null)
            {
                // Already running — let the caller know the live values.
                return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold, source = _noteSource };
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

            _noteSource = source;
            _noteCts = new CancellationTokenSource();
            _noteLanguage = string.IsNullOrWhiteSpace(v.SttLanguage) ? "auto" : v.SttLanguage;

            if (source == NoteSourceNames.SystemLoopback)
            {
                // ── M0024 Phase 3.5 — WASAPI loopback path ───────────
                // No VAD; user records continuous system audio and we slice
                // it into N-second chunks via a Timer. Each chunk drains the
                // buffer and routes through the same STT+diarize+emit path
                // OnLoopbackChunkTick orchestrates.
                _noteLoopbackChunkSec = (opts.LoopbackChunkSec is int s && s > 0) ? Math.Clamp(s, 5, 120) : 30;
                try
                {
                    var lb = new AgentZeroWpf.Services.Music.LoopbackCaptureService { BufferPcm = true };
                    lb.AmplitudeChanged += OnNoteAmplitude;
                    lb.Start(opts.LoopbackDeviceId ?? "");
                    _noteLoopback = lb;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[WebDev:Note] loopback capture failed: {ex.GetType().Name}: {ex.Message}");
                    NoteError?.Invoke("Loopback capture failed: " + ex.Message);
                    return new { ok = false, error = ex.Message };
                }

                AppLogger.Log($"[WebDev:Note] capture started | source=SystemLoopback chunkSec={_noteLoopbackChunkSec} lang={_noteLanguage} loopbackDevice='{opts.LoopbackDeviceId ?? ""}'");
                StartNotePartialTimer();
                StartNoteChunkTimer();
                return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold, source };
            }

            // ── Existing microphone path ─────────────────────────────
            // M0015 / 후속 진행 #2 — drive PreRoll / Hangover / cap from
            // the active sensitivity profile so Loose lecture-tuning takes
            // effect end-to-end on the voice-note path too. Voice-note is
            // a lecture/note-capture surface so isAgentMode: true matches
            // the Auto-token resolution used elsewhere.
            var vadCfg = VoiceRuntimeFactory.BuildVadConfig(v, isAgentMode: true);
            var cap = new VoiceCaptureService
            {
                BufferPcm = true,
                Muted = v.MicMuted,
                VadThreshold = threshold,
                PreRollSeconds = vadCfg.PreRollSeconds,
                UtteranceHangoverFrames = vadCfg.UtteranceHangoverFrames,
                MaxUtteranceSeconds = vadCfg.MaxUtteranceSeconds,
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

            _noteCapture = cap;
            // Cache the user's chosen STT language so every utterance
            // transcribes the same way Settings/Voice does. Whisper's
            // "auto" detector is unreliable on short utterances —
            // omitting it was why transcripts came back empty even
            // though Settings/Voice (which always passes v.SttLanguage)
            // was working on the same mic.
            AppLogger.Log($"[WebDev:Note] capture started | source=Microphone sens={effectiveSens} threshold={threshold:F4} lang={_noteLanguage} muted={cap.Muted} device={v.InputDeviceId}");

            // M0024 Phase 3 — 10s partial timer (Phase 2.5 패턴 공통화).
            // Reuses the same _noteStt instance so we don't re-warm Whisper.
            StartNotePartialTimer();
            return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold, source };
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
        // M0015 후속 진행 #1: live slider tweak still respects the active
        // profile — Auto in voice-note context resolves to Loose so a
        // moved slider doesn't silently revert to Strict-curve mapping.
        var v = VoiceSettingsStore.Load();
        c.VadThreshold = VoiceRuntimeFactory.SensitivityToThreshold(percent, v, isAgentMode: true);
    }

    public bool IsNoteCapturing =>
        _noteCapture is { IsCapturing: true }
        || _noteLoopback is { IsCapturing: true };

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
        var cap = _noteCapture;
        if (cap is null) return;
        var pcm = cap.ConsumePcmBuffer();
        // Filter sub-half-second utterances — 16 kHz mono 16-bit = 32 KB/s,
        // so < 8000 bytes ≈ < 250 ms. Whisper just returns junk on those
        // and we'd surface noise lines in the timeline.
        if (pcm.Length < 8000)
        {
            AppLogger.Log($"[WebDev:Note] utterance dropped — too short ({pcm.Length} bytes)");
            return;
        }
        AppLogger.Log($"[WebDev:Note] utterance → STT | bytes={pcm.Length} lang={_noteLanguage}");
        _ = Task.Run(() => ProcessFinalChunkAsync(pcm, "utterance"));
    }

    /// <summary>
    /// Get-or-create the cached note-side diarizer. Returns null when the
    /// user has DiarizationSettings.Provider = Off OR the model files
    /// aren't present yet. Each WebDevHost instance owns its own diarizer
    /// (separate from the Settings/Voice/Test diarizer) so the two pipelines
    /// don't share session state.
    /// </summary>
    private async Task<SherpaSpeakerDiarizer?> GetReadyNoteDiarizerAsync(CancellationToken ct)
    {
        var s = DiarizationSettingsStore.Load();
        if (s.Provider == DiarizationProviderNames.Off) return null;

        _noteDiarizer ??= new SherpaSpeakerDiarizer(s);
        var ok = await _noteDiarizer.EnsureReadyAsync(null, ct).ConfigureAwait(false);
        return ok ? _noteDiarizer : null;
    }

    private static string MajoritySpeaker(IReadOnlyList<SpeakerSegment> segments, out int speakerId)
    {
        var totals = new Dictionary<int, double>();
        foreach (var s in segments)
        {
            totals.TryGetValue(s.SpeakerId, out var sum);
            totals[s.SpeakerId] = sum + s.DurationSec;
        }
        int bestId = -1;
        double bestDur = -1;
        foreach (var kv in totals)
            if (kv.Value > bestDur) { bestDur = kv.Value; bestId = kv.Key; }
        speakerId = bestId;
        return new SpeakerSegment(0, 0, bestId).SpeakerLabel;
    }

    // ── M0024 Phase 3 — note partial timer ───────────────────────────────

    private void StartNotePartialTimer()
    {
        StopNotePartialTimer();
        _notePartialLastBytes = 0;
        System.Threading.Interlocked.Exchange(ref _notePartialBusy, 0);
        // System.Threading.Timer instead of DispatcherTimer — WebDevHost has
        // no Dispatcher (it lives in the service layer, not WPF). Callback
        // fires on the threadpool which is exactly what we want (STT is
        // CPU-bound; no UI marshalling needed because we emit via the
        // existing NoteTranscript event which the bridge already marshals).
        _notePartialTimer = new System.Threading.Timer(OnNotePartialTick, null,
            NotePartialIntervalMs, NotePartialIntervalMs);
        AppLogger.Log($"[WebDev:Note-Partial] timer started, interval={NotePartialIntervalMs / 1000}s");
    }

    private void StopNotePartialTimer()
    {
        try { _notePartialTimer?.Dispose(); } catch { }
        _notePartialTimer = null;
    }

    private void OnNotePartialTick(object? _)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _notePartialBusy, 1, 0) != 0) return;
        try
        {
            var stt = _noteStt;
            var ctsToken = _noteCts?.Token ?? CancellationToken.None;
            var lang = _noteLanguage;
            if (stt is null || ctsToken.IsCancellationRequested)
            {
                System.Threading.Interlocked.Exchange(ref _notePartialBusy, 0);
                return;
            }

            // M0024 Phase 3.5 — peek whichever capture source is active.
            byte[] pcm;
            if (_noteLoopback is { IsCapturing: true })
                pcm = _noteLoopback.PeekPcmBuffer();
            else if (_noteCapture is not null)
                pcm = _noteCapture.PeekPcmBuffer();
            else
            {
                System.Threading.Interlocked.Exchange(ref _notePartialBusy, 0);
                return;
            }

            if (pcm.Length < NotePartialMinBytes || pcm.Length == _notePartialLastBytes)
            {
                System.Threading.Interlocked.Exchange(ref _notePartialBusy, 0);
                return;
            }
            _notePartialLastBytes = pcm.Length;

            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await stt.TranscribeAsync(pcm, lang, ctsToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text)) return;
                    var trimmed = text.Trim();
                    if (WhisperHallucinationFilter.IsLikelyHallucination(trimmed)) return;
                    AppLogger.Log($"[WebDev:Note-Partial] ok | bytes={pcm.Length} chars={trimmed.Length} source={_noteSource}");
                    NoteTranscript?.Invoke(new NoteTranscriptInfo(trimmed, SpeakerId: null, SpeakerLabel: null, IsPartial: true));
                }
                catch (OperationCanceledException) { /* expected on STOP */ }
                catch (Exception ex)
                {
                    AppLogger.Log($"[WebDev:Note-Partial] tick failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _notePartialBusy, 0);
                }
            });
        }
        catch
        {
            System.Threading.Interlocked.Exchange(ref _notePartialBusy, 0);
        }
    }

    // ── M0024 Phase 3.5 — loopback chunk timer ───────────────────────────

    private void StartNoteChunkTimer()
    {
        StopNoteChunkTimer();
        var ms = _noteLoopbackChunkSec * 1000;
        _noteChunkTimer = new System.Threading.Timer(OnNoteChunkTick, null, ms, ms);
        AppLogger.Log($"[WebDev:Note-Chunk] timer started, interval={_noteLoopbackChunkSec}s");
    }

    private void StopNoteChunkTimer()
    {
        try { _noteChunkTimer?.Dispose(); } catch { }
        _noteChunkTimer = null;
    }

    /// <summary>
    /// Loopback path's "synthetic utterance end" — fires every
    /// <see cref="_noteLoopbackChunkSec"/> seconds, drains the loopback
    /// buffer, and routes through the same STT + diarize + emit pipeline
    /// the mic utterance flow uses. NoteUtteranceStarted/Ended events also
    /// fire so plugin UI state mirrors a real utterance cycle.
    /// </summary>
    private void OnNoteChunkTick(object? _)
    {
        var lb = _noteLoopback;
        var stt = _noteStt;
        if (lb is null || stt is null) return;
        if ((_noteCts?.Token ?? CancellationToken.None).IsCancellationRequested) return;

        var pcm = lb.ConsumePcmBuffer();
        if (pcm.Length < 8000)
        {
            AppLogger.Log($"[WebDev:Note-Chunk] empty chunk skipped (bytes={pcm.Length})");
            return;
        }
        // Reset partial baseline because the buffer was just drained; next
        // partial tick should compare against the freshly-empty buffer.
        _notePartialLastBytes = 0;

        AppLogger.Log($"[WebDev:Note-Chunk] tick → STT | bytes={pcm.Length} ({pcm.Length / 32_000.0:F1}s)");
        try { NoteUtteranceEnded?.Invoke(); } catch { }
        _ = Task.Run(() => ProcessFinalChunkAsync(pcm, "loopback-chunk"));
    }

    /// <summary>
    /// Shared STT + diarize + emit pipeline used by both the mic
    /// UtteranceEnded handler and the loopback chunk timer. The
    /// label is logged so it's clear which path produced the line.
    /// </summary>
    private async Task ProcessFinalChunkAsync(byte[] pcm, string label)
    {
        var stt = _noteStt;
        var ctsToken = _noteCts?.Token ?? CancellationToken.None;
        var lang = _noteLanguage;
        if (stt is null) return;
        try
        {
            var text = await stt.TranscribeAsync(pcm, lang, ctsToken);
            if (ctsToken.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[WebDev:Note-{label}] STT returned empty | bytes={pcm.Length} lang={lang}");
                return;
            }
            var trimmed = text.Trim();
            if (WhisperHallucinationFilter.IsLikelyHallucination(trimmed))
            {
                AppLogger.Log($"[WebDev:Note-{label}] hallucination dropped | chars={trimmed.Length}");
                return;
            }

            int? speakerId = null;
            string? speakerLabel = null;
            try
            {
                var diar = await GetReadyNoteDiarizerAsync(ctsToken).ConfigureAwait(false);
                if (diar is not null)
                {
                    var dSettings = DiarizationSettingsStore.Load();
                    var dResult = await diar.DiarizeAsync(pcm, dSettings.ExpectedSpeakerCount, ctsToken).ConfigureAwait(false);
                    if (dResult.Segments.Count > 0)
                    {
                        var lbl = MajoritySpeaker(dResult.Segments, out var id);
                        speakerId = id; speakerLabel = lbl;
                        AppLogger.Log($"[WebDev:Note-Diar:{label}] segments={dResult.Segments.Count} speakers={dResult.SpeakerCount} pick={lbl} inferMs={dResult.InferenceTime.TotalMilliseconds:F0}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception dx)
            {
                AppLogger.Log($"[WebDev:Note-Diar:{label}] failed (continuing without speaker): {dx.GetType().Name}: {dx.Message}");
            }

            AppLogger.Log($"[WebDev:Note-{label}] STT ok | chars={trimmed.Length} speaker={(speakerLabel ?? "—")}");
            NoteTranscript?.Invoke(new NoteTranscriptInfo(trimmed, speakerId, speakerLabel, IsPartial: false));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Note-{label}] STT transcribe failed: {ex.GetType().Name}: {ex.Message}");
            NoteError?.Invoke("Transcribe failed: " + ex.Message);
        }
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
        StopNotePartialTimer();
        StopNoteChunkTimer();
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
        // M0024 Phase 3.5 — loopback teardown mirrors mic.
        var lb = _noteLoopback; _noteLoopback = null;
        if (lb is not null)
        {
            lb.AmplitudeChanged -= OnNoteAmplitude;
            try { lb.Stop(); } catch { }
            try { lb.Dispose(); } catch { }
        }
        try { _noteCts?.Cancel(); } catch { }
        _noteCts?.Dispose();
        _noteCts = null;
        _noteStt = null;
        // Diarizer disposes its native ONNX sessions on next process exit.
        // Keep the instance across capture sessions so a second Start
        // doesn't re-load the ~50 MB model files — same caching pattern
        // _noteStt uses.
        AppLogger.Log($"[WebDev:Note] capture stopped (was source={_noteSource})");
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

    // ─────────────────────────────────────────────────────────────────
    //  Agent Band (M0026) — YouTube oEmbed + stateless LLM classify
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch YouTube oEmbed metadata (title / author / thumbnail) for a
    /// video id. Runs host-side so the plugin isn't blocked by the missing
    /// CORS headers on the public oEmbed endpoint. The id is validated and
    /// the request URL is rebuilt from a canonical <c>watch?v=</c> string,
    /// so a malicious "id" can never redirect the fetch to another host.
    /// </summary>
    public async Task<OEmbedResult> YouTubeOEmbedAsync(string videoId, CancellationToken ct = default)
    {
        if (!IsValidVideoId(videoId))
            return new OEmbedResult(false, videoId, null, null, null, "invalid videoId");
        try
        {
            var watch = "https://www.youtube.com/watch?v=" + videoId;
            var url = "https://www.youtube.com/oembed?url=" + Uri.EscapeDataString(watch) + "&format=json";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new OEmbedResult(false, videoId, null, null, null, $"oembed HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? title  = root.TryGetProperty("title",         out var tEl)  ? tEl.GetString()  : null;
            string? author = root.TryGetProperty("author_name",   out var aEl)  ? aEl.GetString()  : null;
            string? thumb  = root.TryGetProperty("thumbnail_url", out var thEl) ? thEl.GetString() : null;
            AppLogger.Log($"[WebDev] oembed ok | id={videoId} title='{Trunc(title ?? "", 50)}'");
            return new OEmbedResult(true, videoId, title, author, thumb, null);
        }
        catch (OperationCanceledException) { return new OEmbedResult(false, videoId, null, null, null, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] oembed failed: {ex.GetType().Name}: {ex.Message}");
            return new OEmbedResult(false, videoId, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// One-shot LLM classification of a YouTube title into exactly one of
    /// <paramref name="categories"/>. Uses a throwaway session (like
    /// <see cref="SummarizeChunkAsync"/>) so the user's <c>chat.*</c> history
    /// is never polluted. The model's free-text answer is clamped to the
    /// supplied whitelist host-side, so even an adversarial title (prompt
    /// injection) can only ever yield one of the allowed labels — worst case
    /// it lands on the fallback (last category).
    /// </summary>
    public async Task<ClassifyResult> ClassifyAsync(string title, string? channel, IReadOnlyList<string> categories, CancellationToken ct = default)
    {
        var allowed = (categories ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (string.IsNullOrWhiteSpace(title))
            return new ClassifyResult(false, null, null, "empty title");
        if (allowed.Count == 0)
            return new ClassifyResult(false, null, null, "no categories");
        if (!LlmGateway.IsActiveAvailable())
        {
            // M0026 후속 #1 — this used to return silently, so an all-"기타"
            // result (LLM off → plugin keyword-fallback) had NO log trail.
            // Log it and return a stable token the plugin keys on to show
            // "LLM 꺼짐 → 키워드 추정" instead of pretending it classified.
            AppLogger.Log("[WebDev] classify skipped — LLM backend not loaded (Settings → LLM); plugin will keyword-fallback.");
            return new ClassifyResult(false, null, null, "llm-not-ready");
        }

        var fallback = allowed[^1];
        await _chatLock.WaitAsync(ct);
        try
        {
            await using var session = LlmGateway.OpenSession();
            var list = string.Join(", ", allowed);
            var ch = string.IsNullOrWhiteSpace(channel) ? "" : $"\n채널: {channel}";
            // Hardened prompt — let the model lean on what it knows about the
            // artist/group/track, give a couple of few-shot anchors, and force
            // a single bare label so MatchCategory lands cleanly.
            var prompt =
                "너는 음악 장르 분류기야. 아래 유튜브 영상을 카테고리 중 정확히 하나로 분류해.\n" +
                "가수·그룹 이름이나 곡 제목을 알면 그 지식으로 장르를 추정해도 좋아.\n" +
                "반드시 카테고리 이름 하나만 답하고, 다른 말은 절대 쓰지 마.\n" +
                $"카테고리: {list}\n" +
                "예) BABYMONSTER - DRIP → K-Pop / 베토벤 교향곡 5번 → 클래식 / Bill Evans Trio → 재즈\n\n" +
                $"제목: {title}{ch}";
            var reply = ((await session.SendAsync(prompt, ct)) ?? "").Trim();
            var match = MatchCategory(reply, allowed) ?? fallback;
            AppLogger.Log($"[WebDev] classify | title='{Trunc(title, 50)}' → {match} (raw='{Trunc(reply, 40)}')");
            return new ClassifyResult(true, match, reply, null);
        }
        catch (OperationCanceledException) { return new ClassifyResult(false, null, null, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] classify failed: {ex.GetType().Name}: {ex.Message}");
            return new ClassifyResult(false, null, null, ex.Message);
        }
        finally { _chatLock.Release(); }
    }

    private static bool IsValidVideoId(string id)
        => !string.IsNullOrEmpty(id) && id.Length is >= 8 and <= 16
           && id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');

    // Extract the 11-char video id from any YouTube URL form (watch / youtu.be
    // / embed / shorts / live), tolerating extra params like &list=…&start_radio=1,
    // or accept a bare id. Mirrors the plugin's JS parseVideoId so the host is
    // self-contained and unit-testable. Returns null when no id is present.
    public static string? ParseYouTubeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (Regex.IsMatch(s, "^[A-Za-z0-9_-]{11}$")) return s;
        foreach (var pat in new[]
        {
            @"[?&]v=([A-Za-z0-9_-]{11})",
            @"youtu\.be/([A-Za-z0-9_-]{11})",
            @"/embed/([A-Za-z0-9_-]{11})",
            @"/shorts/([A-Za-z0-9_-]{11})",
            @"/live/([A-Za-z0-9_-]{11})",
        })
        {
            var m = Regex.Match(s, pat);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // Clamp the model's free-text answer to one of the allowed labels.
    // First a direct/contains hit (handles "이 영상은 재즈입니다"), then the
    // reverse (reply is a substring of a label). Returns null when nothing
    // matches so the caller can apply its fallback.
    private static string? MatchCategory(string reply, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var r = reply.ToLowerInvariant();
        foreach (var c in allowed)
            if (r.Contains(c.ToLowerInvariant())) return c;
        foreach (var c in allowed)
            if (r.Length >= 2 && c.ToLowerInvariant().Contains(r)) return c;
        return null;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    public void Dispose()
    {
        try { TearDownNoteCaptureLocked(); } catch { }
        try { TearDownMusicLocked(); } catch { }
        try { DisposeVision(); } catch { }
        _noteLock.Dispose();
        _musicLock.Dispose();
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
