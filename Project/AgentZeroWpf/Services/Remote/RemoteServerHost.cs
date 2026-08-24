using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;

using Akka.Actor;
using Agent.Common;
using Agent.Common.Remote;
using Agent.Common.Services;
using AgentZeroWpf.Actors;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.Services.Remote;

/// <summary>
/// The WPF-side network edge of the Remote feature. Owns an <see cref="HttpListener"/> that:
/// <list type="bullet">
///   <item>serves the static web client (xterm.js) from <c>Wasm/remote/</c>,</item>
///   <item>answers <c>POST /api/pair</c> (one-time PIN → bearer token) via <see cref="RemoteAuthService"/>,</item>
///   <item>upgrades <c>GET /ws</c> to a WebSocket, authenticates the bearer token carried in the
///     <c>Sec-WebSocket-Protocol</c> header, resolves the target <see cref="ITerminalSession"/>, and
///     hands the connection to a per-session <c>RemoteSessionActor</c> through the hub.</item>
/// </list>
/// The actor is the intermediary: this host only pumps bytes between the socket and the actor
/// (outbound via a per-connection <see cref="Channel{T}"/> drained by one send loop so WS writes
/// stay serialized; inbound by Tell-ing the session actor).
/// </summary>
public sealed class RemoteServerHost
{
    private readonly Func<IReadOnlyList<CliGroupInfo>> _groupsProvider;
    private readonly Func<(int group, int tab)?> _activeProvider;
    private readonly RemoteAuthService _auth;
    private readonly string _webRoot;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private IActorRef? _hub;

    /// <summary>Raised (on a background thread) when running state or last-error changes.</summary>
    public event Action? StatusChanged;

    public bool IsRunning { get; private set; }
    public string? BoundUrl { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The auth core, exposed so the Remote panel can issue PINs and revoke tokens.</summary>
    public RemoteAuthService Auth => _auth;

    public RemoteServerHost(
        Func<IReadOnlyList<CliGroupInfo>> groupsProvider,
        Func<(int group, int tab)?> activeProvider,
        RemoteAuthService auth,
        string webRoot)
    {
        _groupsProvider = groupsProvider;
        _activeProvider = activeProvider;
        _auth = auth;
        _webRoot = webRoot;
    }

    /// <summary>Start (or restart) the listener with the given settings. Safe to call on the UI thread.</summary>
    public void Start(RemoteSettings settings) => Start(settings, allowElevation: true);

    /// <param name="allowElevation">When a LAN bind fails for a missing URL ACL, whether to
    /// self-elevate (UAC) a one-time <c>netsh</c> reservation and retry. Set false on the retry
    /// pass so a persistent failure can't loop.</param>
    private void Start(RemoteSettings settings, bool allowElevation)
    {
        Stop();
        LastError = null;

        // "0.0.0.0" → "+" (all interfaces) which needs a URL ACL; loopback needs neither.
        bool isLan = settings.BindAddress == "0.0.0.0";
        string host = isLan ? "+" : settings.BindAddress;
        string prefix = $"http://{host}:{settings.Port}/";

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _cts = new CancellationTokenSource();
            IsRunning = true;
            BoundUrl = $"http://{(isLan ? LocalIpHint() : settings.BindAddress)}:{settings.Port}/";

            // Resolve (idempotently create) the hub with the current cap.
            _ = EnsureHubAsync(settings.MaxConnections);

            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
            AppLogger.Log($"[Remote] listening on {prefix} (max={settings.MaxConnections})");
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
        {
            AppLogger.Log($"[Remote] start failed (access denied): {ex.Message}");

            // The app runs non-elevated. A LAN prefix ("+") needs a URL ACL reservation, a
            // one-time admin step. Rather than make the user run netsh, spawn an elevated
            // helper (UAC consent prompt) to add the reservation for the CURRENT user, then
            // retry the bind once.
            if (isLan && allowElevation)
            {
                var reserve = TryReserveUrlAclElevated(settings.Port);
                if (reserve == ElevationResult.Reserved)
                {
                    Start(settings, allowElevation: false); // retry after reservation
                    return;
                }

                IsRunning = false;
                LastError = reserve == ElevationResult.Cancelled
                    ? $"권한 설정이 취소되었습니다. 수동 설정: netsh http add urlacl url=http://+:{settings.Port}/ user=\"{CurrentUser()}\""
                    : $"URL ACL 예약에 실패했습니다. 수동 설정(관리자): netsh http add urlacl url=http://+:{settings.Port}/ user=\"{CurrentUser()}\"";
            }
            else
            {
                IsRunning = false;
                LastError = $"권한 거부: netsh http add urlacl url=http://+:{settings.Port}/ user=\"{CurrentUser()}\"";
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            LastError = ex.Message;
            AppLogger.Log($"[Remote] start failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            StatusChanged?.Invoke();
        }
    }

    private enum ElevationResult { Reserved, Cancelled, Failed }

    private static string CurrentUser()
    {
        try { return WindowsIdentity.GetCurrent().Name; }
        catch { return Environment.UserName; }
    }

    /// <summary>
    /// Launch an elevated <c>netsh http add urlacl</c> to reserve the LAN prefix for the current
    /// user. Uses ShellExecute "runas" so Windows shows the normal UAC consent dialog — this does
    /// NOT bypass UAC. Returns how it went so the caller can retry or surface guidance.
    /// </summary>
    private static ElevationResult TryReserveUrlAclElevated(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{port}/ user=\"{CurrentUser()}\"",
                Verb = "runas",            // triggers UAC consent
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return ElevationResult.Failed;
            if (!p.WaitForExit(30000)) return ElevationResult.Failed;

            bool ok = p.ExitCode == 0;
            AppLogger.Log($"[Remote] elevated urlacl reservation exit={p.ExitCode}");
            return ok ? ElevationResult.Reserved : ElevationResult.Failed;
        }
        catch (Win32Exception wex) when (wex.NativeErrorCode == 1223) // ERROR_CANCELLED (UAC declined)
        {
            AppLogger.Log("[Remote] urlacl elevation cancelled by user");
            return ElevationResult.Cancelled;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Remote] urlacl elevation failed: {ex.GetType().Name}: {ex.Message}");
            return ElevationResult.Failed;
        }
    }

    /// <summary>Stop the listener and drop all connections.</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
        if (IsRunning)
        {
            IsRunning = false;
            BoundUrl = null;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Push a new connection cap to the running hub (settings changed).</summary>
    public void UpdateMaxConnections(int max) => _hub?.Tell(new SetMaxConnections(max));

    /// <summary>Query the hub for live connection counts (for the panel).</summary>
    public async Task<RemoteStatusReply?> GetStatusAsync()
    {
        if (_hub is null) return null;
        try { return await _hub.Ask<RemoteStatusReply>(new QueryRemoteStatus(), TimeSpan.FromSeconds(2)); }
        catch { return null; }
    }

    private async Task EnsureHubAsync(int maxConnections)
    {
        try
        {
            var reply = await ActorSystemManager.Stage.Ask<RemoteHubCreated>(
                new CreateRemoteHub(maxConnections), TimeSpan.FromSeconds(5));
            _hub = reply.Hub;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Remote] hub resolve failed: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener stopped
            _ = Task.Run(() => HandleRequestAsync(ctx, ct));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (ctx.Request.IsWebSocketRequest && path == "/ws")
            {
                await HandleWebSocketAsync(ctx, ct);
                return;
            }

            if (path == "/api/pair" && ctx.Request.HttpMethod == "POST")
            {
                HandlePair(ctx);
                return;
            }

            ServeStatic(ctx, path);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Remote] request error: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    // ── Pairing ──────────────────────────────────────────────

    private void HandlePair(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = reader.ReadToEnd();

        string? pin = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("pin", out var p)) pin = p.GetString();
        }
        catch { }

        var result = _auth.TryPair(pin);
        if (result.Outcome == PairOutcome.Success)
        {
            WriteJson(ctx, 200, $"{{\"ok\":true,\"token\":\"{result.Token}\"}}");
        }
        else
        {
            int code = result.Outcome == PairOutcome.LockedOut ? 429 : 401;
            WriteJson(ctx, code, $"{{\"ok\":false,\"error\":\"{result.Outcome}\"}}");
        }
    }

    // ── WebSocket ────────────────────────────────────────────

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        // Bearer token rides in Sec-WebSocket-Protocol (browsers can't set WS headers).
        string? proto = ctx.Request.Headers["Sec-WebSocket-Protocol"];
        string? token = proto?.Split(',')[0].Trim();

        if (!_auth.ValidateToken(token))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }

        if (_hub is null)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        HttpListenerWebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: token); }
        catch { try { ctx.Response.StatusCode = 400; ctx.Response.Close(); } catch { } return; }

        var ws = wsCtx.WebSocket;
        string sessionKey = Guid.NewGuid().ToString("N");
        var outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        void SendToWeb(string payload) => outbound.Writer.TryWrite(payload);

        IActorRef? sessionRef = null;
        try
        {
            // Resolve the initial target (query ?group=&tab= wins, else the active terminal).
            var (session, ok) = ResolveTarget(ctx.Request.QueryString);
            if (!ok || session is null)
            {
                SendToWeb(RemoteProtocol.Error("no terminal available"));
                await DrainOnceAsync(ws, outbound, ct);
                await CloseAsync(ws, "no terminal");
                return;
            }

            var created = await _hub.Ask<object>(
                new CreateRemoteSession(sessionKey, session, SendToWeb), TimeSpan.FromSeconds(5));
            if (created is RemoteSessionRejected rej)
            {
                SendToWeb(RemoteProtocol.Error(rej.Reason));
                await DrainOnceAsync(ws, outbound, ct);
                await CloseAsync(ws, rej.Reason);
                return;
            }
            sessionRef = (created as RemoteSessionCreated)?.SessionRef;

            // Send the terminal catalog so the client can offer a switcher.
            SendToWeb(RemoteProtocol.Terminals(BuildTerminalListJsonOnUi()));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sendTask = SendLoopAsync(ws, outbound, linked.Token);
            var recvTask = ReceiveLoopAsync(ws, () => sessionRef, k => sessionRef = k, sessionKey, SendToWeb, linked.Token);
            var catalogTask = CatalogLoopAsync(SendToWeb, linked.Token);
            await Task.WhenAny(sendTask, recvTask);
            linked.Cancel();
            outbound.Writer.TryComplete();
            await Task.WhenAll(SwallowAsync(sendTask), SwallowAsync(recvTask), SwallowAsync(catalogTask));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Remote] ws session error: {ex.Message}");
        }
        finally
        {
            _hub?.Tell(new CloseRemoteSession(sessionKey));
            try { ws.Dispose(); } catch { }
            AppLogger.Log($"[Remote] ws closed: {sessionKey}");
        }
    }

    /// <summary>
    /// Periodically re-send the terminal catalog so terminals created (or renamed/removed)
    /// after the client connected show up in the switcher without a reconnect. The client
    /// diffs the list and only rebuilds when it actually changed, so this never disrupts the
    /// current selection.
    /// </summary>
    private async Task CatalogLoopAsync(Action<string> sendToWeb, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2500, ct); }
            catch { break; }
            try { sendToWeb(RemoteProtocol.Terminals(BuildTerminalListJsonOnUi())); }
            catch { /* transient UI-thread hiccup; retry next tick */ }
        }
    }

    private static async Task SendLoopAsync(WebSocket ws, Channel<string> outbound, CancellationToken ct)
    {
        var reader = outbound.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var payload))
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket ws,
        Func<IActorRef?> getSession,
        Action<IActorRef?> setSession,
        string sessionKey,
        Action<string> sendToWeb,
        CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var acc = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buffer, ct); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;

            acc.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            string json = acc.ToString();
            acc.Clear();

            if (!RemoteProtocol.TryParseClient(json, out var msg)) continue;

            switch (msg.Type)
            {
                case "input" when msg.Data is not null:
                    getSession()?.Tell(new RemoteInputText(msg.Data));
                    break;
                case "key" when msg.Data is not null:
                    getSession()?.Tell(new RemoteControlKey(msg.Data));
                    break;
                case "attach":
                    await ReattachAsync(msg, sessionKey, sendToWeb, setSession);
                    break;
                case "ping":
                    break;
            }
        }
    }

    private async Task ReattachAsync(RemoteClientMessage msg, string sessionKey, Action<string> sendToWeb, Action<IActorRef?> setSession)
    {
        if (_hub is null || msg.Group is null || msg.Tab is null) return;
        var (session, ok) = ResolveTarget(msg.Group.Value, msg.Tab.Value);
        if (!ok || session is null)
        {
            sendToWeb(RemoteProtocol.Error("target terminal not available"));
            return;
        }
        try
        {
            // Same key → hub stops the old actor and spawns a new one bound to the new session,
            // which re-sends a fresh snapshot for the newly attached terminal.
            var created = await _hub.Ask<object>(
                new CreateRemoteSession(sessionKey, session, sendToWeb), TimeSpan.FromSeconds(5));
            if (created is RemoteSessionCreated ok2) setSession(ok2.SessionRef);
            else if (created is RemoteSessionRejected rej) sendToWeb(RemoteProtocol.Error(rej.Reason));
        }
        catch (Exception ex)
        {
            sendToWeb(RemoteProtocol.Error($"attach failed: {ex.Message}"));
        }
    }

    // ── Target resolution (marshalled to the UI thread) ──────

    private (ITerminalSession? session, bool ok) ResolveTarget(System.Collections.Specialized.NameValueCollection query)
    {
        if (int.TryParse(query["group"], out var g) && int.TryParse(query["tab"], out var t))
            return ResolveTarget(g, t);

        var active = _activeProvider();
        if (active is { } a) return ResolveTarget(a.group, a.tab);
        return (null, false);
    }

    private (ITerminalSession? session, bool ok) ResolveTarget(int group, int tab)
    {
        ITerminalSession? session = null;
        bool ok = false;
        RunOnUi(() =>
        {
            var groups = _groupsProvider();
            if (group < 0 || group >= groups.Count) return;
            var g = groups[group];
            if (tab < 0 || tab >= g.Tabs.Count) return;
            var t = g.Tabs[tab];

            // Sessions are created lazily when a tab is first focused in the GUI. A
            // freshly-created terminal that the web is trying to attach to may not have
            // one yet — create it on demand (same seam the GUI uses), so newly-made
            // terminals are reachable rather than reported "not started".
            if (t.Session is null)
                CliSessionAccessHelper.EnsureSession(t, t.Terminal, g.DisplayName);

            session = t.Session;
            ok = session is not null;
        });
        return (session, ok);
    }

    private string BuildTerminalListJsonOnUi()
    {
        string json = "{\"groups\":[]}";
        RunOnUi(() => json = CliTerminalIpcHelper.BuildTerminalListJson(_groupsProvider(), EscapeJson));
        return json;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    // ── Static file serving ──────────────────────────────────

    private void ServeStatic(HttpListenerContext ctx, string path)
    {
        if (path == "/" || string.IsNullOrEmpty(path)) path = "/index.html";

        // Prevent path traversal: only allow files that resolve inside _webRoot.
        string rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_webRoot, rel));
        string rootFull = Path.GetFullPath(_webRoot);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        byte[] bytes = File.ReadAllBytes(full);
        ctx.Response.ContentType = ContentTypeFor(full);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static string ContentTypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };

    // ── helpers ──────────────────────────────────────────────

    private static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static async Task DrainOnceAsync(WebSocket ws, Channel<string> outbound, CancellationToken ct)
    {
        // Best-effort: flush any queued error frame before closing.
        while (outbound.Reader.TryRead(out var payload))
        {
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct); }
            catch { break; }
        }
    }

    private static async Task CloseAsync(WebSocket ws, string reason)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { }
    }

    private static async Task SwallowAsync(Task task) { try { await task; } catch { } }

    private static string LocalIpHint()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch { }
        return "localhost";
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
