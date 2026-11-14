using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agent.Common.Remote;
using Agent.Common.Services;
using Akka.Actor;
using AgentZeroAvalonia.Services.Remote;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// The Remote server end to end on a real loopback socket: static files, pairing, the
/// WebSocket handshake with the key as subprotocol, the attach frames, input to the terminal,
/// output to the browser, a terminal switch, and revocation.
/// </summary>
public sealed class RemoteServerTests : IAsyncLifetime
{
    private readonly ActorSystem _system = ActorSystem.Create("remote-server-tests");
    private readonly string _root = Directory.CreateTempSubdirectory("azr-web-").FullName;
    private readonly RemoteSettings _settings = new() { Port = 0, BindAddress = "127.0.0.1", MaxConnections = 2 };
    private readonly FakeSession _first = new("FIRST-SCREEN");
    private readonly FakeSession _second = new("SECOND-SCREEN");
    private RemoteServer _server = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><title>remote</title>");
        var hub = _system.ActorOf(Props.Create(() => new RemoteHubActor(2)));
        var auth = new RemoteAuthService(_settings, () => { });
        _server = new RemoteServer(auth, new Terminals(_first, _second), _root, _ => Task.FromResult<IActorRef?>(hub));
        _server.Start(_settings);
        Assert.True(_server.IsRunning, _server.LastError);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _server.Dispose();
        await _system.Terminate();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Serves_the_client_and_refuses_paths_outside_it()
    {
        var page = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>remote</title>", await page.Content.ReadAsStringAsync());
        Assert.True(page.Headers.TryGetValues("Content-Security-Policy", out var csp));
        Assert.Contains("default-src 'self'", csp.Single());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/%2e%2e/%2e%2e/secret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/missing.js")).StatusCode);
    }

    [Fact]
    public async Task Pairing_needs_the_issued_code_and_gives_a_key_once()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Pair("12345678")).StatusCode);

        var pin = _server.Auth.IssuePin().Pin;
        var ok = await Pair(pin);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var token = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // Single use.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Pair(pin)).StatusCode);
    }

    [Fact]
    public async Task A_socket_without_a_paired_key_is_refused()
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("not-a-key");
        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/ws"), CancellationToken.None));
    }

    [Fact]
    public async Task A_paired_browser_drives_the_terminal_and_can_switch()
    {
        var token = await PairedToken();
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/ws"), CancellationToken.None);
        Assert.Equal(token, ws.SubProtocol);

        // Attached to the active terminal with its size, then its screen, then the catalog.
        var attached = await Expect(ws, "attached");
        Assert.Equal(0, attached.GetProperty("tab").GetInt32());
        Assert.Equal(132, attached.GetProperty("cols").GetInt32());
        Assert.Contains("FIRST-SCREEN", (await Expect(ws, "snapshot")).GetProperty("d").GetString());
        await Expect(ws, "terminals");

        // Browser → terminal.
        await SendJson(ws, "{\"t\":\"input\",\"d\":\"ls\\r\"}");
        await SendJson(ws, "{\"t\":\"key\",\"d\":\"ctrlc\"}");
        await Until(() => _first.Writes.Contains("ls\r") && _first.Controls.Contains(TerminalControl.Interrupt));

        // Terminal → browser.
        _first.RaiseOutput("hello-remote");
        Assert.Contains("hello-remote", (await Expect(ws, "output")).GetProperty("d").GetString());

        // Switch terminals: the second one's screen arrives and input goes there.
        await SendJson(ws, "{\"t\":\"attach\",\"group\":0,\"tab\":1}");
        Assert.Equal(1, (await Expect(ws, "attached")).GetProperty("tab").GetInt32());
        Assert.Contains("SECOND-SCREEN", (await Expect(ws, "snapshot")).GetProperty("d").GetString());
        await SendJson(ws, "{\"t\":\"input\",\"d\":\"pwd\"}");
        await Until(() => _second.Writes.Contains("pwd"));
        Assert.DoesNotContain("pwd", _first.Writes);

        var status = await _server.GetStatusAsync();
        Assert.Equal(1, status!.Active);
    }

    [Fact]
    public async Task The_session_check_tells_a_revoked_key_from_a_dropped_connection()
    {
        var token = await PairedToken();
        Assert.Equal(HttpStatusCode.OK, (await SessionCheck(token)).StatusCode);

        _server.Auth.RevokeAll();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SessionCheck(token)).StatusCode);
    }

    [Fact]
    public void A_busy_port_is_reported_not_thrown()
    {
        var other = new RemoteServer(new RemoteAuthService(new RemoteSettings(), () => { }), new Terminals(_first), _root,
            _ => Task.FromResult<IActorRef?>(null));
        other.Start(new RemoteSettings { Port = _server.Port, BindAddress = "127.0.0.1" });
        Assert.False(other.IsRunning);
        Assert.Contains("already in use", other.LastError);
    }

    [Fact]
    public void Http_helpers_follow_the_rules()
    {
        // RFC 6455 §1.3's worked example.
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", RemoteHttp.WebSocketAccept("dGhlIHNhbXBsZSBub25jZQ=="));

        Assert.True(RemoteHttp.TryParseHead("GET /ws?group=1&tab=2 HTTP/1.1\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: abc", out var req));
        Assert.Equal("/ws", req.Path);
        Assert.Equal("2", req.QueryValue("tab"));
        Assert.True(req.IsWebSocketUpgrade);
        Assert.False(RemoteHttp.TryParseHead("GARBAGE", out _));

        Assert.EndsWith("index.html", RemoteHttp.ResolveStatic(_root, "/"));
        Assert.Null(RemoteHttp.ResolveStatic(_root, "/../index.html"));
        Assert.Null(RemoteHttp.ResolveStatic(_root, "/..\\index.html"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> Pair(string pin)
        => _http.PostAsync("/api/pair", new StringContent($"{{\"pin\":\"{pin}\"}}", Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> SessionCheck(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(req);
    }

    private async Task<string> PairedToken()
    {
        var resp = await Pair(_server.Auth.IssuePin().Pin);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }

    private static Task SendJson(WebSocket ws, string json)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Read frames until one of <paramref name="type"/> arrives (others are skipped).</summary>
    private static async Task<JsonElement> Expect(WebSocket ws, string type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);
            var root = JsonDocument.Parse(sb.ToString()).RootElement;
            if (root.GetProperty("t").GetString() == type) return root.Clone();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private sealed class Terminals(params FakeSession[] tabs) : IRemoteTerminals
    {
        public string CatalogJson() => "{\"groups\":[{\"group_index\":0,\"group_name\":\"ws\",\"tabs\":[]}]}";
        public ITerminalSession? Resolve(int group, int tab) => group == 0 && tab >= 0 && tab < tabs.Length ? tabs[tab] : null;
        public (int Group, int Tab)? Active() => (0, 0);
        public (int Cols, int Rows)? SizeOf(ITerminalSession session) => (132, 40);
    }

    private sealed class FakeSession(string screen) : ITerminalSession
    {
        private readonly StringBuilder _log = new(screen);
        public System.Collections.Concurrent.ConcurrentBag<string> Writes { get; } = new();
        public System.Collections.Concurrent.ConcurrentBag<TerminalControl> Controls { get; } = new();
        public string SessionId => "fake";
        public string InternalId { get; } = Guid.NewGuid().ToString("N")[..8];
        public bool IsRunning => true;
        public int OutputLength { get { lock (_log) return _log.Length; } }
        public TerminalHealthState HealthState => TerminalHealthState.Alive;
        public event Action<TerminalOutputFrame>? OutputReceived;
        public event Action<TerminalHealthState>? HealthChanged { add { } remove { } }
        public void Write(ReadOnlySpan<char> text) => Writes.Add(text.ToString());
        public void WriteAndSubmit(string text) => Writes.Add(text);
        public void WriteAndEnter(string text) => Writes.Add(text);
        public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default) { Writes.Add(text.ToString()); return Task.CompletedTask; }
        public void SendControl(TerminalControl control) => Controls.Add(control);
        public string ReadOutput(int start, int length) { lock (_log) return _log.ToString(start, length); }
        public string GetConsoleText() { lock (_log) return _log.ToString(); }
        public void NoteInputAttempt(string source) { }
        public void RaiseOutput(string text)
        {
            lock (_log) _log.Append(text);
            OutputReceived?.Invoke(new TerminalOutputFrame(text, DateTimeOffset.UtcNow));
        }
    }
}
