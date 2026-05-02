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
///   event    → { op: "event", channel: string, data: any }   (host-pushed; not used yet)
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

    public WebDevBridge(CoreWebView2 core, IZeroBrowser host)
    {
        _core = core;
        _host = host;
        _core.WebMessageReceived += OnMessage;
    }

    public void Detach()
    {
        try { _core.WebMessageReceived -= OnMessage; } catch { }
    }

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

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
        }
    }

    private void PostResponse(int id, bool ok, object? result, string? error)
    {
        var payload = new { id, ok, result, error };
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts)); }
        catch (Exception ex) { AppLogger.Log($"[WebDev] post failed: {ex.Message}"); }
    }
}
