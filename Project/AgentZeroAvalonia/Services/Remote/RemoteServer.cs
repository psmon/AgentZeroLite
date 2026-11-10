using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Agent.Common;
using Agent.Common.Remote;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Services.Remote;

/// <summary>
/// What the remote server needs from the GUI. Called from pool threads; the GUI's
/// implementation marshals to the UI thread itself, so the server never touches a view model.
/// </summary>
internal interface IRemoteTerminals
{
    /// <summary>The <c>terminal-list</c> JSON (the catalog the web client's picker shows).</summary>
    string CatalogJson();

    /// <summary>The live session of workspace <paramref name="group"/>, tab <paramref name="tab"/>; null if absent or not started.</summary>
    ITerminalSession? Resolve(int group, int tab);

    /// <summary>The terminal a fresh connection attaches to (the one in front in the GUI), or null.</summary>
    (int Group, int Tab)? Active();

    /// <summary>The pseudo-console's size as the GUI's renderer last fitted it, or null when unknown.</summary>
    (int Cols, int Rows)? SizeOf(ITerminalSession session);
}

/// <summary>
/// The network edge of the Remote feature in the Avalonia host — the WPF host's
/// <c>RemoteServerHost</c>, rebuilt on a <see cref="TcpListener"/> instead of
/// <see cref="HttpListener"/>. It serves the web client from <c>Wasm/remote/</c>, answers
/// <c>POST /api/pair</c> (one-time PIN → bearer token, <see cref="RemoteAuthService"/>), and
/// upgrades <c>GET /ws</c> to a WebSocket whose bearer token rides in
/// <c>Sec-WebSocket-Protocol</c> (browsers cannot set headers on a WebSocket).
///
/// <para>Why not HttpListener: on Windows a LAN prefix needs an http.sys URL reservation, which
/// the WPF host gets by self-elevating <c>netsh</c> behind a UAC prompt; a plain socket needs
/// none of that and runs the same on macOS and Linux. The HTTP it speaks is the minimum the
/// client needs: one request per connection, a head capped at 16 KB, a body at 4 KB.</para>
///
/// <para>The actors are shared with WPF (ZeroCommon <c>RemoteHubActor</c> / <c>RemoteSessionActor</c>,
/// same wire protocol): this class only pumps frames between a socket and a session actor.
/// The Store (MSIX) and Mac App Store builds leave this whole folder out
/// (<c>-p:IncludeRemote=false</c>) — a listening socket is a review question those builds do not
/// need to answer.</para>
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    public const int MaxHeadBytes = 16 * 1024;
    public const int MaxBodyBytes = 4 * 1024;
    private const int MaxMessageChars = 1024 * 1024;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CatalogInterval = TimeSpan.FromMilliseconds(2500);

    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
    };

    private readonly RemoteAuthService _auth;
    private readonly IRemoteTerminals _terminals;
    private readonly string _webRoot;
    private readonly Func<int, Task<IActorRef?>> _hubFactory;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task<IActorRef?>? _hub;

    /// <summary>Raised (on any thread) when running state or the last error changes.</summary>
    public event Action? StatusChanged;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public bool IsLan { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The auth core — the Remote page issues PINs and revokes tokens through it.</summary>
    public RemoteAuthService Auth => _auth;

    /// <param name="hubFactory">Resolves the hub for a connection cap; null = ask the app's stage actor.</param>
    public RemoteServer(RemoteAuthService auth, IRemoteTerminals terminals, string webRoot,
        Func<int, Task<IActorRef?>>? hubFactory = null)
    {
        _auth = auth;
        _terminals = terminals;
        _webRoot = Path.GetFullPath(webRoot);
        _hubFactory = hubFactory ?? StageHubAsync;
    }

    /// <summary>The addresses a browser can use while the server runs (LAN first, then this PC).</summary>
    public IReadOnlyList<string> Urls
    {
        get
        {
            if (!IsRunning) return [];
            var list = new List<string>();
            if (IsLan) list.AddRange(LanAddresses().Select(ip => $"http://{ip}:{Port}/"));
            list.Add($"http://127.0.0.1:{Port}/");
            return list;
        }
    }

    // ── lifecycle ────────────────────────────────────────────────────────────

    /// <summary>Start (or restart) on the given settings. Never throws: a failure is <see cref="LastError"/>.</summary>
    public void Start(RemoteSettings settings)
    {
        lock (_gate)
        {
            StopCore();
            LastError = null;
            IsLan = settings.BindAddress != "127.0.0.1";
            var address = IsLan
                ? (IPAddress.TryParse(settings.BindAddress, out var a) ? a : IPAddress.Any)
                : IPAddress.Loopback;
            try
            {
                var listener = new TcpListener(address, settings.Port);
                listener.Start();
                _listener = listener;
                _cts = new CancellationTokenSource();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                IsRunning = true;
                _hub = ResolveHubAsync(settings.MaxConnections);
                var ct = _cts.Token;
                _ = Task.Run(() => AcceptLoopAsync(listener, ct));
                AppLogger.Log($"[Remote] listening on {address}:{Port} (max={settings.MaxConnections})");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Port {settings.Port} is already in use — pick another port."
                    : ex.Message;
                AppLogger.Log($"[Remote] start failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        StatusChanged?.Invoke();
    }

    /// <summary>Stop listening and drop every connection.</summary>
    public void Stop()
    {
        bool was;
        lock (_gate)
        {
            was = IsRunning;
            StopCore();
        }
        if (was) StatusChanged?.Invoke();
    }

    private void StopCore()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        if (IsRunning) AppLogger.Log("[Remote] stopped");
        IsRunning = false;
    }

    public void Dispose() => Stop();

    /// <summary>Push a new connection cap to the hub (settings changed).</summary>
    public void UpdateMaxConnections(int max)
    {
        var hub = _hub;
        if (hub is { IsCompletedSuccessfully: true, Result: { } h }) h.Tell(new SetMaxConnections(max));
    }

    /// <summary>Live connection count from the hub, or null when it does not answer.</summary>
    public async Task<RemoteStatusReply?> GetStatusAsync()
    {
        var hubTask = _hub;
        if (hubTask is null) return null;
        try
        {
            var hub = await hubTask.ConfigureAwait(false);
            return hub is null ? null : await hub.Ask<RemoteStatusReply>(new QueryRemoteStatus(), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private async Task<IActorRef?> ResolveHubAsync(int max)
    {
        try
        {
            var hub = await _hubFactory(max).ConfigureAwait(false);
            // The stage creates the hub once and hands back the same one afterwards, cap and all.
            hub?.Tell(new SetMaxConnections(max));
            return hub;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Remote] hub resolve failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<IActorRef?> StageHubAsync(int max)
    {
        var reply = await Actors.ActorSystemManager.Stage
            .Ask<RemoteHubCreated>(new CreateRemoteHub(max), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return reply.Hub;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }   // stopped
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var (head, leftover) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
                if (head is null) return;
                if (!RemoteHttp.TryParseHead(head, out var req))
                {
                    await RemoteHttp.WriteTextAsync(stream, 400, "bad request", ct).ConfigureAwait(false);
                    return;
                }

                if (req.Path == "/ws")
                {
                    if (req.IsWebSocketUpgrade) await HandleWebSocketAsync(stream, req, ct).ConfigureAwait(false);
                    else await RemoteHttp.WriteTextAsync(stream, 400, "websocket upgrade expected", ct).ConfigureAwait(false);
                    return;
                }

                if (req.Path == "/api/pair")
                {
                    if (req.Method != "POST") { await RemoteHttp.WriteTextAsync(stream, 405, "POST only", ct).ConfigureAwait(false); return; }
                    var body = await ReadBodyAsync(stream, req, leftover, ct).ConfigureAwait(false);
                    await HandlePairAsync(stream, body, ct).ConfigureAwait(false);
                    return;
                }

                if (req.Path == "/api/session")
                {
                    // Lets the client tell "my key was revoked" (pair again) from "the server is
                    // gone" (retry): a refused WebSocket handshake looks the same as a dropped one.
                    var bearer = req.Header("Authorization") is { } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
                    var valid = _auth.ValidateToken(bearer);
                    await RemoteHttp.WriteJsonAsync(stream, valid ? 200 : 401, valid ? "{\"ok\":true}" : "{\"ok\":false}", ct).ConfigureAwait(false);
                    return;
                }

                if (req.Method is not ("GET" or "HEAD"))
                {
                    await RemoteHttp.WriteTextAsync(stream, 405, "method not allowed", ct).ConfigureAwait(false);
                    return;
                }
                await ServeStaticAsync(stream, req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                AppLogger.Log($"[Remote] request error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Read up to the blank line that ends the request head. Returns the head and whatever body bytes came with it.</summary>
    private static async Task<(string? Head, byte[] Leftover)> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeadTimeout);
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (null, []); }
            if (n == 0) return (null, []);
            var scanFrom = Math.Max(0, filled - 3);
            filled += n;
            var end = buffer.AsSpan(scanFrom, filled - scanFrom).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                end += scanFrom;
                var head = Encoding.ASCII.GetString(buffer, 0, end);
                var rest = buffer.AsSpan(end + 4, filled - end - 4).ToArray();
                return (head, rest);
            }
        }
        return (null, []);   // head larger than the cap
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream, RemoteHttpRequest req, byte[] leftover, CancellationToken ct)
    {
        var length = int.TryParse(req.Header("Content-Length"), out var l) ? Math.Clamp(l, 0, MaxBodyBytes) : 0;
        var body = new byte[length];
        var have = Math.Min(leftover.Length, length);
        Array.Copy(leftover, body, have);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeadTimeout);
        while (have < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(have), timeout.Token).ConfigureAwait(false);
            if (n == 0) break;
            have += n;
        }
        return Encoding.UTF8.GetString(body, 0, have);
    }

    private async Task HandlePairAsync(NetworkStream stream, string body, CancellationToken ct)
    {
        string? pin = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("pin", out var p) && p.ValueKind == JsonValueKind.String) pin = p.GetString();
        }
        catch { }

        var result = _auth.TryPair(pin);
        if (result.Outcome == PairOutcome.Success)
        {
            AppLogger.Log("[Remote] a browser paired");
            await RemoteHttp.WriteJsonAsync(stream, 200, $"{{\"ok\":true,\"token\":\"{result.Token}\"}}", ct).ConfigureAwait(false);
        }
        else
        {
            var code = result.Outcome == PairOutcome.LockedOut ? 429 : 401;
            await RemoteHttp.WriteJsonAsync(stream, code, $"{{\"ok\":false,\"error\":\"{result.Outcome}\"}}", ct).ConfigureAwait(false);
        }
    }

    private async Task ServeStaticAsync(NetworkStream stream, RemoteHttpRequest req, CancellationToken ct)
    {
        var file = RemoteHttp.ResolveStatic(_webRoot, req.Path);
        if (file is null || !ContentTypes.TryGetValue(Path.GetExtension(file), out var type))
        {
            await RemoteHttp.WriteTextAsync(stream, 404, "not found", ct).ConfigureAwait(false);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        var extra = file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            // Everything the page loads is ours and same-origin; the WebSocket is 'self' too.
            ? "Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'\r\n"
            : "";
        await RemoteHttp.WriteAsync(stream, 200, type, req.Method == "HEAD" ? [] : bytes, ct, extra, contentLength: bytes.Length).ConfigureAwait(false);
    }

    // ── WebSocket ────────────────────────────────────────────────────────────

    private async Task HandleWebSocketAsync(NetworkStream stream, RemoteHttpRequest req, CancellationToken ct)
    {
        var token = req.Header("Sec-WebSocket-Protocol")?.Split(',')[0].Trim();
        if (!_auth.ValidateToken(token))
        {
            await RemoteHttp.WriteTextAsync(stream, 401, "pair first", ct).ConfigureAwait(false);
            return;
        }
        var hub = _hub is null ? null : await _hub.ConfigureAwait(false);
        if (hub is null)
        {
            await RemoteHttp.WriteTextAsync(stream, 503, "remote hub unavailable", ct).ConfigureAwait(false);
            return;
        }

        var accept = RemoteHttp.WebSocketAccept(req.Header("Sec-WebSocket-Key") ?? "");
        var handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: {token}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);

        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = token,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        await RunSessionAsync(ws, hub, req, ct).ConfigureAwait(false);
    }

    /// <summary>One browser connection: attach, then pump frames until either side goes away.</summary>
    private async Task RunSessionAsync(WebSocket ws, IActorRef hub, RemoteHttpRequest req, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N");
        var outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        void SendToWeb(string payload) => outbound.Writer.TryWrite(payload);
        var link = new Attachment();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendTask = SendLoopAsync(ws, outbound, linked.Token);
        try
        {
            var target = int.TryParse(req.QueryValue("group"), out var g) && int.TryParse(req.QueryValue("tab"), out var t)
                ? (g, t)
                : _terminals.Active();
            if (target is not { } tgt || !await AttachAsync(hub, key, tgt.Item1, tgt.Item2, link, SendToWeb).ConfigureAwait(false))
                SendToWeb(RemoteProtocol.Error("No running terminal to attach to — open one in AgentZero, then pick it above."));

            SendToWeb(RemoteProtocol.Terminals(_terminals.CatalogJson()));

            var recvTask = ReceiveLoopAsync(ws, hub, key, link, SendToWeb, linked.Token);
            var catalogTask = CatalogLoopAsync(link, SendToWeb, linked.Token);
            await Task.WhenAny(sendTask, recvTask).ConfigureAwait(false);
            linked.Cancel();
            await Task.WhenAll(Swallow(recvTask), Swallow(catalogTask)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Remote] session error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            linked.Cancel();
            outbound.Writer.TryComplete();
            await Swallow(sendTask).ConfigureAwait(false);
            hub.Tell(new CloseRemoteSession(key));
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", bye.Token).ConfigureAwait(false);
                }
                catch { }
            }
            AppLogger.Log($"[Remote] connection closed: {key[..8]}");
        }
    }

    /// <summary>The terminal a connection is attached to — read by the catalog loop, swapped by an attach.</summary>
    private sealed class Attachment
    {
        public volatile IActorRef? Actor;
        public volatile ITerminalSession? Session;
        public (int Cols, int Rows)? SentSize;
        public int Generation;
    }

    /// <summary>
    /// Bind the connection to a terminal: the size first (the client sizes its xterm to the
    /// pseudo-console's, or a TUI's frame wraps), then the hub spawns a session actor that sends
    /// the screen snapshot and streams output. Reusing the key replaces the previous actor.
    /// </summary>
    private async Task<bool> AttachAsync(IActorRef hub, string key, int group, int tab, Attachment link, Action<string> sendToWeb)
    {
        var session = _terminals.Resolve(group, tab);
        if (session is null) return false;

        // Frames from the actor this attach replaces may still be in flight until it has
        // unsubscribed; only the newest attachment's frames reach the browser.
        var gen = Interlocked.Increment(ref link.Generation);
        void Gated(string payload) { if (Volatile.Read(ref link.Generation) == gen) sendToWeb(payload); }

        link.SentSize = _terminals.SizeOf(session);
        sendToWeb(RemoteFrames.Attached(group, tab, link.SentSize));

        var created = await hub.Ask<object>(new CreateRemoteSession(key, session, Gated), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (created is RemoteSessionRejected rej)
        {
            sendToWeb(RemoteProtocol.Error(rej.Reason));
            return true;   // the reason is already on its way; not "no terminal"
        }
        link.Actor = (created as RemoteSessionCreated)?.SessionRef;
        link.Session = session;
        return true;
    }

    private async Task ReceiveLoopAsync(WebSocket ws, IActorRef hub, string key, Attachment link, Action<string> sendToWeb, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var acc = new StringBuilder();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;
            acc.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (acc.Length > MaxMessageChars) break;   // nothing the client sends is this large
            if (!result.EndOfMessage) continue;

            var json = acc.ToString();
            acc.Clear();
            if (!RemoteProtocol.TryParseClient(json, out var msg)) continue;

            switch (msg.Type)
            {
                case "input" when msg.Data is not null:
                    link.Actor?.Tell(new RemoteInputText(msg.Data));
                    break;
                case "key" when msg.Data is not null:
                    link.Actor?.Tell(new RemoteControlKey(msg.Data));
                    break;
                case "attach" when msg.Group is { } g && msg.Tab is { } t:
                    try
                    {
                        if (!await AttachAsync(hub, key, g, t, link, sendToWeb).ConfigureAwait(false))
                            sendToWeb(RemoteProtocol.Error("That terminal has not started yet — open it in AgentZero first."));
                    }
                    catch (Exception ex) { sendToWeb(RemoteProtocol.Error($"attach failed: {ex.Message}")); }
                    break;
            }
        }
    }

    /// <summary>
    /// Re-send the catalog every few seconds so terminals opened after the browser connected
    /// appear in its picker (the client redraws only on change), and the size whenever the GUI
    /// re-fits the attached terminal.
    /// </summary>
    private async Task CatalogLoopAsync(Attachment link, Action<string> sendToWeb, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CatalogInterval, ct).ConfigureAwait(false); }
            catch { break; }
            try
            {
                sendToWeb(RemoteProtocol.Terminals(_terminals.CatalogJson()));
                if (link.Session is { } s && _terminals.SizeOf(s) is { } size && size != link.SentSize)
                {
                    link.SentSize = size;
                    sendToWeb(RemoteFrames.Size(size));
                }
            }
            catch { /* the UI thread was busy; next tick */ }
        }
    }

    private static async Task SendLoopAsync(WebSocket ws, Channel<string> outbound, CancellationToken ct)
    {
        var reader = outbound.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (reader.TryRead(out var payload))
                await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task Swallow(Task task) { try { await task.ConfigureAwait(false); } catch { } }

    // ── addresses ────────────────────────────────────────────────────────────

    /// <summary>This machine's IPv4 addresses a phone could reach — interfaces with a gateway (the real LAN) first.</summary>
    public static IReadOnlyList<string> LanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => (Props: n.GetIPProperties(), Nic: n))
                .OrderByDescending(x => x.Props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .SelectMany(x => x.Props.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                            && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch { return []; }
    }
}

/// <summary>The frames this host adds to the shared protocol (WPF's client ignores unknown types).</summary>
internal static class RemoteFrames
{
    /// <summary><c>{"t":"attached","group":G,"tab":T,"cols":C,"rows":R}</c> — which terminal, and its size when known.</summary>
    public static string Attached(int group, int tab, (int Cols, int Rows)? size)
        => $"{{\"t\":\"attached\",\"group\":{group},\"tab\":{tab}" +
           (size is { } s ? $",\"cols\":{s.Cols},\"rows\":{s.Rows}" : "") + "}";

    /// <summary><c>{"t":"size","cols":C,"rows":R}</c> — the GUI re-fitted the attached terminal.</summary>
    public static string Size((int Cols, int Rows) size) => $"{{\"t\":\"size\",\"cols\":{size.Cols},\"rows\":{size.Rows}}}";
}

/// <summary>A parsed request head.</summary>
internal sealed record RemoteHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string? QueryValue(string name) => Query.TryGetValue(name, out var v) ? v : null;

    public bool IsWebSocketUpgrade =>
        Method == "GET"
        && (Header("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) ?? false)
        && (Header("Connection")?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) ?? false)
        && !string.IsNullOrEmpty(Header("Sec-WebSocket-Key"));
}

/// <summary>The HTTP the remote server speaks — pure helpers, so the tests can pin the rules.</summary>
internal static class RemoteHttp
{
    public static bool TryParseHead(string head, out RemoteHttpRequest request)
    {
        request = null!;
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return false;

        var target = first[1];
        if (!target.StartsWith('/')) return false;
        var q = target.IndexOf('?');
        string path;
        try { path = Uri.UnescapeDataString(q < 0 ? target : target[..q]); }
        catch { return false; }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = eq < 0 ? pair : pair[..eq];
                var v = eq < 0 ? "" : pair[(eq + 1)..];
                try { query[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        request = new RemoteHttpRequest(first[0].ToUpperInvariant(), path, query, headers);
        return true;
    }

    /// <summary>The file a path names under <paramref name="root"/>, or null when it escapes the root or does not exist.</summary>
    public static string? ResolveStatic(string root, string path)
    {
        if (path is "" or "/") path = "/index.html";
        if (path.Contains('\0') || path.Contains('\\')) return null;
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, path.TrimStart('/')));
        if (!full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>RFC 6455 §4.2.2: base64(SHA-1(key + GUID)).</summary>
    public static string WebSocketAccept(string key)
        => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

    public static Task WriteTextAsync(Stream stream, int status, string text, CancellationToken ct)
        => WriteAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), ct);

    public static Task WriteJsonAsync(Stream stream, int status, string json, CancellationToken ct)
        => WriteAsync(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), ct);

    public static async Task WriteAsync(Stream stream, int status, string contentType, byte[] body, CancellationToken ct,
        string extraHeaders = "", int? contentLength = null)
    {
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {contentLength ?? body.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   "Referrer-Policy: no-referrer\r\n" +
                   extraHeaders +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => "Status",
    };
}
