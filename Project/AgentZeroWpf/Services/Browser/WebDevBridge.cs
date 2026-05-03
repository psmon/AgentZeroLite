using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Browser;
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

        if (_noteHost is not null)
        {
            _noteHost.NoteTranscript        += OnNoteTranscript;
            _noteHost.NoteUtteranceStarted  += OnNoteUtteranceStarted;
            _noteHost.NoteUtteranceEnded    += OnNoteUtteranceEnded;
            _noteHost.NoteError             += OnNoteError;
            _noteHost.NoteAmplitude         += OnNoteAmplitude;
            _noteHost.NoteSpeaking          += OnNoteSpeaking;
        }

        TokenUsageCollector.Instance.TickCompleted += OnTokenTick;
    }

    public void Detach()
    {
        try { _core.WebMessageReceived -= OnMessage; } catch { }
        if (_noteHost is not null)
        {
            try { _noteHost.NoteTranscript       -= OnNoteTranscript;      } catch { }
            try { _noteHost.NoteUtteranceStarted -= OnNoteUtteranceStarted;} catch { }
            try { _noteHost.NoteUtteranceEnded   -= OnNoteUtteranceEnded;  } catch { }
            try { _noteHost.NoteError            -= OnNoteError;           } catch { }
            try { _noteHost.NoteAmplitude        -= OnNoteAmplitude;       } catch { }
            try { _noteHost.NoteSpeaking         -= OnNoteSpeaking;        } catch { }
        }
        try { TokenUsageCollector.Instance.TickCompleted -= OnTokenTick; } catch { }
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

    private void OnNoteTranscript(string text)        => PostEvent("note.transcript", new { text });
    private void OnNoteUtteranceStarted()             => PostEvent("note.utterance-start", new { });
    private void OnNoteUtteranceEnded()               => PostEvent("note.utterance-end", new { });
    private void OnNoteError(string message)          => PostEvent("note.error", new { message });
    private void OnNoteSpeaking(bool isSpeaking)      => PostEvent("note.speaking", new { speaking = isSpeaking });
    // Threshold rides every amplitude tick so JS can draw a "needed RMS"
    // marker on the meter — the user can immediately see whether their
    // voice is above/below the line and adjust the slider accordingly.
    private void OnNoteAmplitude(float rms)           => PostEvent("note.amplitude", new { rms, threshold = _noteHost?.CurrentVadThreshold ?? 0f });

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
                int? sens = args?.TryGetProperty("sensitivity", out var sv) == true && sv.TryGetInt32(out var si)
                    ? si : (int?)null;
                // Returns { ok, capturing, sensitivity, threshold } so JS
                // can sync its slider to the effective value (especially
                // important when sens=null and host falls back to
                // Settings/Voice's stored VadThreshold).
                return await _noteHost!.StartNoteCaptureAsync(sens);
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
