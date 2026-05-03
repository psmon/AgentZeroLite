using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Common;
using Agent.Common.Browser;
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
        _core.WebMessageReceived += OnMessage;

        if (_noteHost is not null)
        {
            _noteHost.NoteTranscript        += OnNoteTranscript;
            _noteHost.NoteUtteranceStarted  += OnNoteUtteranceStarted;
            _noteHost.NoteUtteranceEnded    += OnNoteUtteranceEnded;
            _noteHost.NoteError             += OnNoteError;
            _noteHost.NoteAmplitude         += OnNoteAmplitude;
        }
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
        }
    }

    private void OnNoteTranscript(string text)        => PostEvent("note.transcript", new { text });
    private void OnNoteUtteranceStarted()             => PostEvent("note.utterance-start", new { });
    private void OnNoteUtteranceEnded()               => PostEvent("note.utterance-end", new { });
    private void OnNoteError(string message)          => PostEvent("note.error", new { message });
    private void OnNoteAmplitude(float rms)           => PostEvent("note.amplitude", new { rms });

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
                var ok = await _noteHost!.StartNoteCaptureAsync(sens);
                return new { ok, capturing = _noteHost.IsNoteCapturing };
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
                return new { sensitivity = v };
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

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
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
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts)); }
        catch (Exception ex) { AppLogger.Log($"[WebDev] post failed: {ex.Message}"); }
    }

    private void PostEvent(string channel, object data)
    {
        var payload = new { op = "event", channel, data };
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts)); }
        catch (Exception ex) { AppLogger.Log($"[WebDev] event post failed ({channel}): {ex.Message}"); }
    }
}
