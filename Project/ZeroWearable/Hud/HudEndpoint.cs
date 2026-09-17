using System.Net;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using ZeroWearable.Actors;

namespace ZeroWearable.Hud;

/// <summary>
/// The local endpoint Claude Code's hooks already post to.
///
/// Contract kept byte-for-byte from claude_hud_amoled/pc/ble_bridge.py, because the scripts in
/// ~/.claude/hud_amoled are installed and pointed at it:
///
///   POST /status  -> "S {json}"  to the watch
///   POST /event   -> "E {json}"  to the watch
///   GET  /health  -> a line of text
///
/// So taking the HUD over needs no change to settings.json, no reinstall, and nothing on the
/// device. Only one process can hold the port (and the BLE link): run this or ble_bridge.py,
/// not both.
///
/// HttpListener rather than ASP.NET: three routes, no middleware, no DI, and one less thing
/// between the hooks and the radio.
/// </summary>
public sealed class HudEndpoint : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly IActorRef _hud;
    private readonly Action<string, string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;

    public HudEndpoint(IActorRef hud, int port, Action<string, string> log)
    {
        _hud = hud;
        _port = port;
        _log = log;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public bool Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Port in use is the interesting case: ble_bridge.py is probably still running, and
            // it also holds the BLE link, so say so plainly instead of failing quietly.
            _log("warn", $"cannot listen on 127.0.0.1:{_port}: {ex.Message}. " +
                         "Is claude_hud_amoled's ble_bridge.py running?");
            return false;
        }
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log("info", $"HUD endpoint on http://127.0.0.1:{_port} (POST /status, POST /event, GET /health)");
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log("warn", $"accept failed: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleAsync(context), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "GET")
            {
                await ReplyAsync(context, 200,
                    path == "/health"
                        ? "ok"
                        : "ZeroWearable HUD endpoint - POST /status, POST /event, GET /health");
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                await ReplyAsync(context, 405, "use POST");
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = (await reader.ReadToEndAsync()).Trim();

            char tag;
            switch (path)
            {
                case "/status":
                    tag = 'S';
                    break;
                case "/event":
                    tag = 'E';
                    break;
                default:
                    await ReplyAsync(context, 404, "unknown endpoint");
                    return;
            }

            // Validate before forwarding: a malformed line would reach the device's JSON parser
            // and be dropped there, which is a worse place to find out.
            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                await ReplyAsync(context, 400, "bad json");
                return;
            }

            _hud.Tell(tag == 'S' ? new HudActor.Status(body) : new HudActor.Event(body));
            await ReplyAsync(context, 200, "ok");
        }
        catch (Exception ex)
        {
            _log("warn", $"request failed: {ex.Message}");
            try
            {
                await ReplyAsync(context, 500, "error");
            }
            catch
            {
                // the client is already gone
            }
        }
    }

    private static async Task ReplyAsync(HttpListenerContext context, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Close();
        }
        catch
        {
            // already closed
        }
    }
}
