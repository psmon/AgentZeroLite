using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Mp3;
using Agent.Common.Telemetry;
using Microsoft.Web.WebView2.Core;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Routes <c>chrome.webview.postMessage</c> JSON envelopes to <see cref="IZeroBrowser"/>
/// methods. The WebDev panel constructs one bridge per WebView2 host.
///
/// Wire format:
///   request  → { id: number, op: string, args?: object }
///   response → { id: number, ok: boolean, result?: any, error?: string }
///   event    → { op: "event", channel: string, data: any }   (host-pushed; chat streaming)
/// </summary>
public sealed class WebDevBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CoreWebView2 _core;
    private readonly IZeroBrowser _host;
    // CoreWebView2 is STA-bound — every PostWebMessageAsJson must run on
    // the UI thread that created the WebView2. Voice capture / Whisper /
    // chat streaming all fire from background threads, so we marshal
    // every outbound message through this dispatcher. Captured at
    // construction so the bridge keeps working even if the UI thread's
    // SynchronizationContext changes later.
    private readonly Dispatcher _uiDispatcher;

    // Optional concrete handle so VAD-driven note events can be wired
    // back as JS events. Stays null when host is a different IZeroBrowser
    // implementation (tests, etc.) — the note.* ops then fail with a clear
    // "voice-note surface unavailable" error.
    private readonly WebDevHost? _noteHost;

    public WebDevBridge(CoreWebView2 core, IZeroBrowser host)
    {
        _core = core;
        _host = host;
        _noteHost = host as WebDevHost;
        // The bridge is constructed from the UI thread that owns the
        // WebView2 host (WebDevPagePanel.InitAsync) — capture that
        // dispatcher so background events can hop back here.
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _core.WebMessageReceived += OnMessage;

        // M0029 후속 #1 — mp3.local is served by THIS handler, not the folder
        // mapping: the Chromium media stack streams <audio> with HTTP Range
        // requests, which SetVirtualHostNameToFolderMapping cannot answer —
        // scan worked but playback failed. The interceptor answers 206
        // Partial Content from a FileStream slice (real streaming + seek).
        // The folder mapping stays as a fallback for older runtimes where
        // adding the filter throws.
        try
        {
            _core.AddWebResourceRequestedFilter($"https://{Mp3VirtualHost}/*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnMp3ResourceRequested;
        }
        catch (Exception ex) { AppLogger.Log($"[WebDev:Mp3] resource filter unavailable: {ex.Message}"); }

        if (_noteHost is not null)
        {
            _noteHost.NoteTranscript        += OnNoteTranscript;
            _noteHost.NoteUtteranceStarted  += OnNoteUtteranceStarted;
            _noteHost.NoteUtteranceEnded    += OnNoteUtteranceEnded;
            _noteHost.NoteError             += OnNoteError;
            _noteHost.NoteAmplitude         += OnNoteAmplitude;
            _noteHost.NoteSpeaking          += OnNoteSpeaking;
            // M0025 — Agent Band live music ticks.
            _noteHost.MusicTick             += OnMusicTick;
            _noteHost.MusicAmplitude        += OnMusicAmplitude;
            _noteHost.MusicSpectrum         += OnMusicSpectrum;
            // M0029 — Agent Band MP3 scan job stream.
            _noteHost.Mp3ScanProgress       += OnMp3ScanProgress;
            _noteHost.Mp3TrackBatch         += OnMp3TrackBatch;
            _noteHost.Mp3ScanDone           += OnMp3ScanDone;
        }

        TokenUsageCollector.Instance.TickCompleted += OnTokenTick;
        TokenRemainingCollector.Instance.TickCompleted += OnTokenRemainingTick;
        SessionHeartbeatCollector.Instance.TickCompleted += OnSessionHeartbeatTick;
    }

    public void Detach()
    {
        try { _core.WebMessageReceived -= OnMessage; } catch { }
        try { _core.WebResourceRequested -= OnMp3ResourceRequested; } catch { }
        if (_noteHost is not null)
        {
            try { _noteHost.NoteTranscript       -= OnNoteTranscript;      } catch { }
            try { _noteHost.NoteUtteranceStarted -= OnNoteUtteranceStarted;} catch { }
            try { _noteHost.NoteUtteranceEnded   -= OnNoteUtteranceEnded;  } catch { }
            try { _noteHost.NoteError            -= OnNoteError;           } catch { }
            try { _noteHost.NoteAmplitude        -= OnNoteAmplitude;       } catch { }
            try { _noteHost.NoteSpeaking         -= OnNoteSpeaking;        } catch { }
            try { _noteHost.MusicTick            -= OnMusicTick;           } catch { }
            try { _noteHost.MusicAmplitude       -= OnMusicAmplitude;      } catch { }
            try { _noteHost.MusicSpectrum        -= OnMusicSpectrum;       } catch { }
            try { _noteHost.Mp3ScanProgress      -= OnMp3ScanProgress;     } catch { }
            try { _noteHost.Mp3TrackBatch        -= OnMp3TrackBatch;       } catch { }
            try { _noteHost.Mp3ScanDone          -= OnMp3ScanDone;         } catch { }
        }
        try { TokenUsageCollector.Instance.TickCompleted -= OnTokenTick; } catch { }
        try { TokenRemainingCollector.Instance.TickCompleted -= OnTokenRemainingTick; } catch { }
        try { SessionHeartbeatCollector.Instance.TickCompleted -= OnSessionHeartbeatTick; } catch { }
    }

    private void OnTokenTick(TokenUsageCollector.TickSummary s)
        => PostEvent("tokens.tick", new
        {
            filesScanned = s.FilesScanned,
            rowsInserted = s.RowsInserted,
            claudeRows   = s.ClaudeRows,
            codexRows    = s.CodexRows,
            finishedAt   = s.FinishedAt,
            error        = s.Error,
        });

    private void OnTokenRemainingTick(TokenRemainingCollector.TickSummary s)
        => PostEvent("tokens.remaining.tick", new
        {
            filesScanned        = s.FilesScanned,
            rowsInserted        = s.RowsInserted,
            rowsSkippedSamePct  = s.RowsSkippedSamePercent,
            finishedAt          = s.FinishedAtUtc,
            error               = s.Error,
        });

    private void OnSessionHeartbeatTick(SessionHeartbeatCollector.TickSummary s)
        => PostEvent("tokens.remaining.activeSessions.tick", new
        {
            filesScanned = s.FilesScanned,
            rowsUpserted = s.RowsUpserted,
            rowsPruned   = s.RowsPruned,
            finishedAt   = s.FinishedAtUtc,
            error        = s.Error,
        });

    /// <summary>
    /// M0024 Phase 3 — bridge payload extended with speaker fields and a
    /// partial flag. Backward compatible: pre-Phase-3 plugins ignore the new
    /// fields and behave exactly as before. New plugins read
    /// <c>speakerLabel</c> for display and <c>isPartial</c> to dim the line.
    /// </summary>
    private void OnNoteTranscript(NoteTranscriptInfo info) =>
        PostEvent("note.transcript", new
        {
            text         = info.Text,
            speakerId    = info.SpeakerId,
            speakerLabel = info.SpeakerLabel,
            isPartial    = info.IsPartial,
        });
    private void OnNoteUtteranceStarted()             => PostEvent("note.utterance-start", new { });
    private void OnNoteUtteranceEnded()               => PostEvent("note.utterance-end", new { });
    private void OnNoteError(string message)          => PostEvent("note.error", new { message });
    private void OnNoteSpeaking(bool isSpeaking)      => PostEvent("note.speaking", new { speaking = isSpeaking });
    // Threshold rides every amplitude tick so JS can draw a "needed RMS"
    // marker on the meter — the user can immediately see whether their
    // voice is above/below the line and adjust the slider accordingly.
    private void OnNoteAmplitude(float rms)           => PostEvent("note.amplitude", new { rms, threshold = _noteHost?.CurrentVadThreshold ?? 0f });

    // ─── Agent Band (M0025) — live AST tick + amplitude ─────────────
    private void OnMusicTick(WebDevHost.MusicTickInfo t) =>
        PostEvent("music.tick", new
        {
            labels = t.Labels.Select(l => new { name = l.Name, score = l.Score, index = l.Index }).ToArray(),
            spectrum = t.Spectrum,
            frames = t.Frames,
            bins = t.Bins,
            inferMs = t.InferMs,
        });

    private void OnMusicAmplitude(float rms) =>
        PostEvent("music.amplitude", new { rms });

    // 30 Hz spectrum stream — decoupled from the slow AST inference tick so
    // the plugin's visualiser can move on every audio frame, not just when
    // a new classification is ready.
    private void OnMusicSpectrum(float[] bars) =>
        PostEvent("music.spectrum", new { spectrum = bars });

    // ─── Agent Band (M0029) — MP3 scan job stream ────────────────────
    // The scan runs as a background job on the host; these events are what
    // makes the playlist update INCREMENTALLY while the scan is still
    // running (each upserted track is playable the moment it arrives).
    private void OnMp3ScanProgress(Mp3ScanProgressInfo p) => PostEvent("mp3.scan.progress", p);
    // M0030 후속#1 — 트랙 upsert는 배치(~2s/100건)로만 UI에 도달한다.
    private void OnMp3TrackBatch(IReadOnlyList<Mp3TrackDto> tracks) => PostEvent("mp3.tracks", new { tracks });
    private void OnMp3ScanDone(Mp3ScanDoneInfo d)         => PostEvent("mp3.scan.done", d);

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.WebMessageAsJson; }
        catch { raw = "{}"; }

        int id = 0;
        string? op = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var parsedId)) id = parsedId;
            if (root.TryGetProperty("op", out var opEl)) op = opEl.GetString();
            JsonElement? args = root.TryGetProperty("args", out var argsEl) ? argsEl : null;

            object? result = await DispatchAsync(op, args);
            PostResponse(id, ok: true, result: result, error: null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] bridge error op={op}: {ex.GetType().Name}: {ex.Message}");
            PostResponse(id, ok: false, result: null, error: ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(string? op, JsonElement? args)
    {
        switch (op)
        {
            case "version":
                return new { version = _host.GetAppVersion() };

            case "voice.providers":
                return _host.GetVoiceProviders();

            case "tts.speak":
            {
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                var r = await _host.SpeakAsync(text);
                return r;
            }

            case "tts.stop":
                _host.StopSpeaking();
                return new { stopped = true };

            case "chat.status":
                return _host.GetLlmStatus();

            case "chat.send":
            {
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                var r = await _host.ChatSendAsync(text);
                return r;
            }

            case "chat.stream":
            {
                // Fire-and-forget: token + done events go back via PostEvent; the
                // immediate response just acknowledges the streamId so JS can
                // correlate. JS picks up the stream via window.zero.chat.stream(...)
                // which wraps the event flow.
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                var streamId = args?.TryGetProperty("streamId", out var sid) == true
                    ? sid.GetString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");
                _ = StreamChatAsync(text, streamId);
                return new { streamId };
            }

            case "chat.reset":
                await _host.ResetChatAsync();
                return new { reset = true };

            // ─── Voice-note plugin surface ─────────────────────────────
            case "note.start":
            {
                EnsureNoteHost();
                // M0024 Phase 3.5e fix — JsonElement.TryGetInt32 throws
                // InvalidOperationException when the element's ValueKind is
                // anything other than Number (Null included). JS sends
                // {sensitivity: null, ...} on first start, so route through
                // the existing TryGetInt helper which gates on ValueKind ==
                // Number first. Same for loopbackChunkSec. Strings get an
                // analogous ValueKind == String guard inline.
                int? sens = TryGetInt(args, "sensitivity");
                string? source = args?.TryGetProperty("source", out var srcEl) == true
                                 && srcEl.ValueKind == JsonValueKind.String
                    ? srcEl.GetString() : null;
                string? loopbackDeviceId = args?.TryGetProperty("loopbackDeviceId", out var ldEl) == true
                                           && ldEl.ValueKind == JsonValueKind.String
                    ? ldEl.GetString() : null;
                int? chunkSec = TryGetInt(args, "loopbackChunkSec");
                // Returns { ok, capturing, sensitivity, threshold, source }
                // so JS can sync its slider + source dropdown to the
                // effective values.
                return await _noteHost!.StartNoteCaptureAsync(
                    new NoteStartOptions(sens, source, loopbackDeviceId, chunkSec));
            }
            case "note.stop":
            {
                EnsureNoteHost();
                await _noteHost!.StopNoteCaptureAsync();
                return new { ok = true };
            }
            case "note.pause":
                EnsureNoteHost();
                _noteHost!.PauseNoteCapture();
                return new { paused = true };
            case "note.resume":
                EnsureNoteHost();
                _noteHost!.ResumeNoteCapture();
                return new { paused = false };
            case "note.set-sensitivity":
            {
                EnsureNoteHost();
                int v = args?.TryGetProperty("value", out var sv) == true && sv.TryGetInt32(out var si)
                    ? si : 50;
                _noteHost!.SetNoteSensitivity(v);
                return new { sensitivity = v, threshold = _noteHost.CurrentVadThreshold };
            }
            case "note.status":
                EnsureNoteHost();
                return new { capturing = _noteHost!.IsNoteCapturing };
            case "summarize":
            {
                EnsureNoteHost();
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                int maxChars = args?.TryGetProperty("maxChars", out var mc) == true && mc.TryGetInt32(out var mci) && mci > 0
                    ? mci : 6000;
                return await _noteHost!.SummarizeAsync(text, maxChars);
            }

            // ─── Agent Band (M0025) — live music classifier surface ─────
            case "music.start":
            {
                EnsureNoteHost();
                string? source = args?.TryGetProperty("source", out var srcEl) == true
                                 && srcEl.ValueKind == JsonValueKind.String
                    ? srcEl.GetString() : null;
                string? deviceId = args?.TryGetProperty("deviceId", out var dEl) == true
                                   && dEl.ValueKind == JsonValueKind.String
                    ? dEl.GetString() : null;
                int? topK = TryGetInt(args, "topK");
                int? cadence = TryGetInt(args, "cadenceMs");
                return await _noteHost!.StartMusicAsync(source, deviceId, topK, cadence);
            }
            case "music.stop":
            {
                EnsureNoteHost();
                return await _noteHost!.StopMusicAsync();
            }
            case "music.status":
            {
                EnsureNoteHost();
                return _noteHost!.GetMusicStatus();
            }

            // ─── Agent Band (M0026) — YouTube oEmbed + stateless LLM classify ─
            case "youtube.oembed":
            {
                EnsureNoteHost();
                var vid = args?.TryGetProperty("videoId", out var vEl) == true && vEl.ValueKind == JsonValueKind.String
                    ? vEl.GetString() ?? "" : "";
                return await _noteHost!.YouTubeOEmbedAsync(vid);
            }
            case "llm.classify":
            {
                EnsureNoteHost();
                var title = args?.TryGetProperty("title", out var tEl) == true && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? "" : "";
                string? channel = args?.TryGetProperty("channel", out var cEl) == true && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() : null;
                var cats = new List<string>();
                if (args?.TryGetProperty("categories", out var caEl) == true && caEl.ValueKind == JsonValueKind.Array)
                    foreach (var el in caEl.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) cats.Add(s!);
                        }
                return await _noteHost!.ClassifyAsync(title, channel, cats);
            }

            // ─── Agent Band (M0028) — on-device vision (Florence-2 OD) ───
            // The frame is captured HERE (the bridge owns the plugin's
            // CoreWebView2) because the plugin's cross-origin YouTube iframe
            // can't be read from JS. `_core.CapturePreviewAsync` is safe on
            // this path — DispatchAsync runs on the WebView2's UI thread.
            case "vision.status":
                EnsureNoteHost();
                return _noteHost!.GetVisionStatus();
            case "vision.analyze":
            {
                EnsureNoteHost();
                var (ax, ay, aw, ah) = ReadRect(args);
                using var ms = new MemoryStream();
                await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return await _noteHost!.VisionAnalyzeAsync(ms.ToArray(), ax, ay, aw, ah);
            }
            case "vision.motion":
            {
                EnsureNoteHost();
                var (mx, my, mw, mh) = ReadRect(args);
                using var ms = new MemoryStream();
                await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return _noteHost!.VisionMotion(ms.ToArray(), mx, my, mw, mh);
            }
            case "vision.reset":
                EnsureNoteHost();
                _noteHost!.ResetVision();
                return new { ok = true };

            // ─── Agent Band (M0029) — local MP3 playlist (SQLite-backed) ──
            // The scan root is mapped to https://mp3.local/ HERE (the bridge
            // owns the CoreWebView2) so the plugin's <audio> element streams
            // files natively (seek/range included) — audio bytes never cross
            // the postMessage bridge.
            case "mp3.status":
            {
                EnsureNoteHost();
                EnsureMp3HostMapping(Mp3SettingsStore.Load().ScanFolder);
                return _noteHost!.GetMp3Status();
            }
            case "mp3.pickFolder":
            {
                var current = Mp3SettingsStore.Load().ScanFolder;
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "MP3 폴더 선택 — 하위 폴더까지 스캔합니다",
                };
                if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                    dlg.InitialDirectory = current;
                // DispatchAsync runs on the WebView2's UI thread — safe to block on the dialog.
                var picked = dlg.ShowDialog() == true ? dlg.FolderName : null;
                if (string.IsNullOrWhiteSpace(picked))
                    return new { ok = false, error = "cancelled" };
                return new { ok = true, folder = picked };
            }
            case "mp3.setFolder":
            {
                EnsureNoteHost();
                var folder = args?.TryGetProperty("folder", out var fEl) == true && fEl.ValueKind == JsonValueKind.String
                    ? fEl.GetString() ?? "" : "";
                var r = _noteHost!.Mp3SetFolder(folder);
                EnsureMp3HostMapping(Mp3SettingsStore.Load().ScanFolder);
                return r;
            }
            case "mp3.scan":
            {
                EnsureNoteHost();
                var cats = ReadStringArray(args, "categories");
                EnsureMp3HostMapping(Mp3SettingsStore.Load().ScanFolder);
                return _noteHost!.Mp3StartScan(cats);
            }
            case "mp3.scan.cancel":
                EnsureNoteHost();
                return _noteHost!.Mp3CancelScan();
            case "mp3.list":
            {
                EnsureNoteHost();
                EnsureMp3HostMapping(Mp3SettingsStore.Load().ScanFolder);
                return await _noteHost!.Mp3ListAsync(
                    TryGetInt(args, "offset") ?? 0, TryGetInt(args, "limit") ?? 0);
            }
            case "mp3.remove":
            {
                EnsureNoteHost();
                return await _noteHost!.Mp3RemoveAsync(TryGetInt(args, "id") ?? 0);
            }
            case "mp3.markPlayed":
            {
                EnsureNoteHost();
                return await _noteHost!.Mp3MarkPlayedAsync(TryGetInt(args, "id") ?? 0);
            }
            case "mp3.setInstruments":
            {
                EnsureNoteHost();
                var id = TryGetInt(args, "id") ?? 0;
                var keys = ReadStringArray(args, "instruments");
                return await _noteHost!.Mp3SetInstrumentsAsync(id, keys);
            }
            case "mp3.coverGender":
            {
                EnsureNoteHost();
                return await _noteHost!.Mp3CoverGenderAsync(TryGetInt(args, "id") ?? 0);
            }
            case "mp3.setMoods":
            {
                EnsureNoteHost();
                var id = TryGetInt(args, "id") ?? 0;
                var keys = ReadStringArray(args, "moods");
                return await _noteHost!.Mp3SetMoodsAsync(id, keys);
            }
            case "mp3.cards":
                EnsureNoteHost();
                return await _noteHost!.Mp3CardsAsync();
            case "mp3.cardCreate":
                EnsureNoteHost();
                return await _noteHost!.Mp3CardCreateAsync();
            case "mp3.cardRemove":
            {
                EnsureNoteHost();
                return await _noteHost!.Mp3CardRemoveAsync(TryGetInt(args, "id") ?? 0);
            }

            // ─── Token-monitor plugin surface (read-only) ───────────────
            case "tokens.summary":
            {
                var since = ParseSinceUtc(args);
                var totals  = TokenUsageQueryService.GetTotals(since);
                var byVendor = TokenUsageQueryService.GetByVendor(since);
                var state   = TokenUsageQueryService.GetCollectorState();
                return new {
                    range      = DescribeSince(since),
                    totals,
                    byVendor,
                    collector  = state,
                };
            }
            case "tokens.byVendor":
                return TokenUsageQueryService.GetByVendor(ParseSinceUtc(args));
            case "tokens.byAccount":
                return TokenUsageQueryService.GetByAccount(ParseSinceUtc(args));
            case "tokens.byProject":
            {
                var since = ParseSinceUtc(args);
                var limit = TryGetInt(args, "limit") ?? 50;
                return TokenUsageQueryService.GetByProject(since, limit);
            }
            case "tokens.timeseries":
            {
                int rangeHours    = TryGetInt(args, "rangeHours")    ?? 24;
                int bucketMinutes = TryGetInt(args, "bucketMinutes") ?? 60;
                return TokenUsageQueryService.GetTimeSeries(rangeHours, bucketMinutes);
            }
            case "tokens.sessions":
            {
                var since = ParseSinceUtc(args);
                var limit = TryGetInt(args, "limit") ?? 20;
                return TokenUsageQueryService.GetActiveSessions(since, limit);
            }
            case "tokens.recent":
            {
                var limit = TryGetInt(args, "limit") ?? 50;
                return TokenUsageQueryService.GetRecent(limit);
            }
            case "tokens.refresh":
            {
                var summary = await TokenUsageCollector.Instance.TickNowAsync();
                return new {
                    filesScanned = summary.FilesScanned,
                    rowsInserted = summary.RowsInserted,
                    claudeRows   = summary.ClaudeRows,
                    codexRows    = summary.CodexRows,
                    finishedAt   = summary.FinishedAt,
                    error        = summary.Error,
                };
            }
            case "tokens.status":
                return TokenUsageQueryService.GetCollectorState();
            case "tokens.profiles":
                return TokenUsageQueryService.GetProfiles();
            case "tokens.aliases":
                return TokenUsageQueryService.ListAliases();
            case "tokens.aliases.set":
            {
                var vendor = args?.TryGetProperty("vendor", out var ven) == true ? (ven.GetString() ?? "") : "";
                var key    = args?.TryGetProperty("accountKey", out var ak) == true ? (ak.GetString() ?? "") : "";
                var alias  = args?.TryGetProperty("alias", out var al) == true ? (al.GetString() ?? "") : "";
                return TokenUsageQueryService.SetAlias(vendor, key, alias);
            }
            case "tokens.aliases.remove":
            {
                var vendor = args?.TryGetProperty("vendor", out var ven) == true ? (ven.GetString() ?? "") : "";
                var key    = args?.TryGetProperty("accountKey", out var ak) == true ? (ak.GetString() ?? "") : "";
                return new { removed = TokenUsageQueryService.RemoveAlias(vendor, key) };
            }
            case "tokens.reset":
            {
                var summary = TokenUsageCollector.Instance.ResetData();
                return new { rowsDeleted = summary.RowsDeleted, checkpointsDeleted = summary.CheckpointsDeleted };
            }

            // ─── Token-remaining widget surface (M0011) ────────────────
            case "tokens.remaining.profiles":
            {
                // Discovered Claude profiles + their installed-state.
                // The widget Settings dialog and Install dialog both read this.
                var profiles = StatusLineWrapperInstaller.DiscoverProfiles();
                return profiles.Select(p => new {
                    accountKey            = p.AccountKey,
                    configDir             = p.ConfigDir,
                    settingsJsonPath      = p.SettingsJsonPath,
                    settingsJsonExists    = p.SettingsJsonExists,
                    currentStatusLine     = p.CurrentStatusLineCommand,
                    ourWrapperInstalled   = p.OurWrapperInstalled,
                    claudeHudDetected     = p.ClaudeHudDetected,
                    pipeTarget            = p.PipeTarget,
                }).ToList();
            }
            case "tokens.remaining.accounts":
            {
                // Per-account observation summary (DB rows seen so far),
                // for the "Active account" picker.
                return TokenRemainingQueryService.GetAccountProfiles();
            }
            case "tokens.remaining.observedModels":
            {
                var acct = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                return TokenRemainingQueryService.GetObservedModels(acct);
            }
            case "tokens.remaining.latest":
            {
                var acct = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                return new {
                    account = acct,
                    models  = TokenRemainingQueryService.GetLatestForAccount(acct),
                    collector = TokenRemainingQueryService.GetCollectorState(),
                };
            }
            case "tokens.remaining.series":
            {
                var acct  = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                var model = args?.TryGetProperty("model",   out var mEl) == true ? (mEl.GetString() ?? "") : "";
                var hours = TryGetInt(args, "hours") ?? 24;
                return TokenRemainingQueryService.GetSeries(acct, model, hours);
            }
            case "tokens.remaining.status":
                return TokenRemainingQueryService.GetCollectorState();
            case "tokens.remaining.refresh":
            {
                var summary = await TokenRemainingCollector.Instance.TickNowAsync();
                return new {
                    filesScanned       = summary.FilesScanned,
                    rowsInserted       = summary.RowsInserted,
                    rowsSkippedSamePct = summary.RowsSkippedSamePercent,
                    finishedAt         = summary.FinishedAtUtc,
                    error              = summary.Error,
                };
            }
            case "tokens.remaining.install":
            {
                var acct = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                return StatusLineWrapperInstaller.Install(acct);
            }
            case "tokens.remaining.uninstall":
            {
                var acct  = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                var force = args?.TryGetProperty("force",   out var fEl) == true && fEl.ValueKind == JsonValueKind.True;
                return StatusLineWrapperInstaller.Uninstall(acct, force);
            }
            case "tokens.remaining.reset":
            {
                var summary = TokenRemainingCollector.Instance.ResetData();
                return new { rowsDeleted = summary.RowsDeleted, snapshotFilesDeleted = summary.SnapshotFilesDeleted };
            }

            // ─── Active session panel surface (M0012) ──────────────────
            case "tokens.remaining.activeSessions":
            {
                var minutes = TryGetInt(args, "windowMinutes") ?? 5;
                var window = TimeSpan.FromMinutes(Math.Max(1, Math.Min(60, minutes)));
                return new {
                    windowMinutes = (int)window.TotalMinutes,
                    sessions  = SessionHeartbeatQueryService.GetActive(window),
                    collector = SessionHeartbeatQueryService.GetCollectorState(),
                };
            }
            case "tokens.remaining.activeSessions.refresh":
            {
                var summary = await SessionHeartbeatCollector.Instance.TickNowAsync();
                return new {
                    filesScanned = summary.FilesScanned,
                    rowsUpserted = summary.RowsUpserted,
                    rowsPruned   = summary.RowsPruned,
                    finishedAt   = summary.FinishedAtUtc,
                    error        = summary.Error,
                };
            }
            case "tokens.remaining.activeSessions.status":
                return SessionHeartbeatQueryService.GetCollectorState();

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
        }
    }

    private static DateTime? ParseSinceUtc(JsonElement? args)
    {
        if (args is null) return null;
        if (args.Value.TryGetProperty("sinceHours", out var sh) && sh.TryGetInt32(out var hours) && hours > 0)
            return DateTime.UtcNow.AddHours(-hours);
        if (args.Value.TryGetProperty("sinceMinutes", out var sm) && sm.TryGetInt32(out var min) && min > 0)
            return DateTime.UtcNow.AddMinutes(-min);
        return null;
    }

    private static string DescribeSince(DateTime? sinceUtc)
        => sinceUtc is null ? "all-time" : $"since {sinceUtc.Value:O}";

    private static int? TryGetInt(JsonElement? args, string name)
    {
        if (args is null) return null;
        if (args.Value.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            return v;
        return null;
    }

    // Vision crop rect in device pixels: { x, y, w, h }. Missing → 0 (host clamps).
    private static (int x, int y, int w, int h) ReadRect(JsonElement? args)
        => (TryGetInt(args, "x") ?? 0, TryGetInt(args, "y") ?? 0,
            TryGetInt(args, "w") ?? 0, TryGetInt(args, "h") ?? 0);

    private static List<string> ReadStringArray(JsonElement? args, string name)
    {
        var list = new List<string>();
        if (args?.TryGetProperty(name, out var el) == true && el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
        return list;
    }

    // ─── mp3.local virtual host (M0029) ─────────────────────────────
    // Maps the CURRENT scan root so <audio src="https://mp3.local/<rel>">
    // streams from disk with native range/seek. Re-mapping the same host
    // name to a new folder is supported by WebView2 — done whenever the
    // operator changes the scan folder.
    private const string Mp3VirtualHost = "mp3.local";
    private string? _mp3MappedFolder;

    private void EnsureMp3HostMapping(string? folder)
    {
        // Timeout-bounded probe — this runs on the UI thread for every mp3.*
        // bridge call (incl. the plugin's on-load mp3.status/mp3.list). A bare
        // Directory.Exists on a disconnected network scan root would freeze the
        // whole app here (see PathAvailability). Unreachable → skip the mapping;
        // tracks just show Available=false until the drive is back.
        if (!PathAvailability.DirectoryExistsFast(folder)) return;
        if (string.Equals(_mp3MappedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _core.SetVirtualHostNameToFolderMapping(
                Mp3VirtualHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            _mp3MappedFolder = folder;
            AppLogger.Log($"[WebDev:Mp3] mp3.local → {folder}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Mp3] vhost mapping failed: {ex.Message}");
        }
    }

    // Streams one MP3 under the scan root with HTTP Range support (206) —
    // what the <audio> element actually needs for playback + seeking. Path
    // is unescaped and clamped under the root; only *.mp3 is served.
    private void OnMp3ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        void Respond(int status, string reason, string headers, Stream? body = null)
        {
            try { e.Response = _core.Environment.CreateWebResourceResponse(body, status, reason, headers); }
            catch (Exception ex) { AppLogger.Log($"[WebDev:Mp3] response build failed: {ex.Message}"); }
        }

        try
        {
            var uri = new Uri(e.Request.Uri);
            if (!string.Equals(uri.Host, Mp3VirtualHost, StringComparison.OrdinalIgnoreCase)) return;

            var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (rel.Length == 0) { Respond(404, "Not Found", ""); return; }

            // Cover-art route (후속 #2): /__cover/<trackId> → ID3 APIC bytes.
            // Id-based (DB lookup), so no path handling at all on this branch.
            if (rel.StartsWith("__cover/", StringComparison.OrdinalIgnoreCase))
            {
                if (_noteHost is not null
                    && int.TryParse(rel.Substring("__cover/".Length), out var coverId)
                    && _noteHost.TryGetMp3Cover(coverId) is { } cover)
                    Respond(200, "OK",
                        $"Content-Type: {cover.Mime}\nContent-Length: {cover.Data.Length}\nCache-Control: max-age=3600",
                        new MemoryStream(cover.Data));
                else
                    Respond(404, "Not Found", "");
                return;
            }

            var root = Mp3SettingsStore.Load().ScanFolder;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            { Respond(404, "Not Found", ""); return; }
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Respond(403, "Forbidden", ""); return; }
            if (!full.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { Respond(404, "Not Found", ""); return; }

            long total = new FileInfo(full).Length;
            long start = 0, end = total - 1;
            bool ranged = false;

            var rangeRaw = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
            if (!string.IsNullOrEmpty(rangeRaw) && rangeRaw.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                // "bytes=start-end" / "bytes=start-" / "bytes=-suffix" (first range only)
                var spec = rangeRaw.Substring(6).Split(',')[0].Trim();
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    var sPart = spec.Substring(0, dash).Trim();
                    var ePart = spec.Substring(dash + 1).Trim();
                    if (sPart.Length == 0 && ePart.Length > 0 && long.TryParse(ePart, out var suffix))
                    { start = Math.Max(0, total - suffix); end = total - 1; ranged = true; }
                    else if (sPart.Length > 0 && long.TryParse(sPart, out var s))
                    {
                        start = s;
                        end = ePart.Length > 0 && long.TryParse(ePart, out var en) ? Math.Min(en, total - 1) : total - 1;
                        ranged = true;
                    }
                }
                if (ranged && (start > end || start >= total))
                { Respond(416, "Range Not Satisfiable", $"Content-Range: bytes */{total}"); return; }
            }

            long count = end - start + 1;
            var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(start, SeekOrigin.Begin);
            const string common = "Content-Type: audio/mpeg\nAccept-Ranges: bytes";
            if (ranged)
                Respond(206, "Partial Content",
                    $"{common}\nContent-Length: {count}\nContent-Range: bytes {start}-{end}/{total}",
                    new StreamSlice(fs, count));
            else
                Respond(200, "OK", $"{common}\nContent-Length: {total}", fs);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Mp3] serve failed: {ex.GetType().Name}: {ex.Message}");
            Respond(500, "Internal Error", "");
        }
    }

    // Read-only forward view of the next N bytes of an inner stream — lets a
    // 206 response hand WebView2 exactly the requested slice without copying
    // the file into memory.
    private sealed class StreamSlice : Stream
    {
        private readonly Stream _inner;
        private long _remaining;
        public StreamSlice(Stream inner, long count) { _inner = inner; _remaining = count; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private void EnsureNoteHost()
    {
        if (_noteHost is null)
            throw new InvalidOperationException("voice-note surface unavailable on this host");
    }

    private async Task StreamChatAsync(string text, string streamId)
    {
        try
        {
            await foreach (var tok in _host.ChatStreamAsync(text))
                PostEvent("chat.token", new { streamId, token = tok });

            PostEvent("chat.done", new { streamId, ok = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] chat stream failed: {ex.GetType().Name}: {ex.Message}");
            PostEvent("chat.done", new { streamId, ok = false, error = ex.Message });
        }
    }

    private void PostResponse(int id, bool ok, object? result, string? error)
    {
        var payload = new { id, ok, result, error };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        PostJsonOnUi(json, "response");
    }

    private void PostEvent(string channel, object data)
    {
        var payload = new { op = "event", channel, data };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        PostJsonOnUi(json, channel);
    }

    private void PostJsonOnUi(string json, string label)
    {
        // CoreWebView2 must be touched from the UI thread that hosts the
        // WebView2. Most callers already are (the request dispatcher
        // above runs from WebMessageReceived), but background work like
        // VoiceCaptureService events or Task.Run STT continuations isn't
        // — invoke on the captured dispatcher to make every path safe.
        if (_uiDispatcher.CheckAccess())
        {
            try { _core.PostWebMessageAsJson(json); }
            catch (Exception ex) { AppLogger.Log($"[WebDev] post failed ({label}): {ex.Message}"); }
            return;
        }
        _uiDispatcher.BeginInvoke((Action)(() =>
        {
            try { _core.PostWebMessageAsJson(json); }
            catch (Exception ex) { AppLogger.Log($"[WebDev] post failed ({label}): {ex.Message}"); }
        }));
    }
}
