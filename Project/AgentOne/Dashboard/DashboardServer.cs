using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgentOne.Dashboard;

/// <summary>What one request gets back.</summary>
public readonly record struct DashboardResponse(int Status, string ContentType, byte[] Body);

/// <summary>
/// The dashboard's web server: a loopback-only HTTP/1.1 listener, one request
/// per connection. Deliberately not HttpListener — on Windows that goes
/// through http.sys, whose URL reservations make a non-admin listen fail in
/// ways that depend on the machine — and not ASP.NET Core, which would be
/// most of the binary. A page a person reads locally needs GET, one POST, and
/// nothing else.
///
/// Three locks on the door, because the data is a record of someone's work:
/// the socket binds 127.0.0.1 only; every <c>/api</c> call must carry the
/// per-run token from the printed link (another site open in the same browser
/// cannot read it); and the Host header must name the loopback, which is what
/// stops a DNS-rebinding page from calling in under its own origin.
/// </summary>
public sealed class DashboardServer : IDisposable
{
    public const string TokenHeader = "x-agent-one-token";
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 256 * 1024;

    private readonly DashboardData _data;
    private readonly TcpListener _listener;

    public string Token { get; }
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/?t={Token}";

    /// <param name="port">0 picks a free port.</param>
    public DashboardServer(DashboardData data, int port, string? token = null)
    {
        _data = data;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Serves until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _listener.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, timeout.Token);
                var response = request is null
                    ? Text(400, "bad request")
                    : Handle(request.Value.Method, request.Value.Target, request.Value.Headers, request.Value.Body);
                await WriteAsync(stream, response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The browser went away mid-request; nothing to answer.
            }
        }
    }

    // --------------------------------------------------------------- routing

    /// <summary>The whole server minus the socket — what the tests drive.</summary>
    public DashboardResponse Handle(string method, string target, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        if (!headers.TryGetValue("host", out var host) || !IsLoopbackHost(host))
            return Text(421, "this dashboard answers only on 127.0.0.1 / localhost");

        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        var query = ParseQuery(q >= 0 ? target[(q + 1)..] : "");

        if (method == "GET" && path is "/" or "/index.html")
            return new DashboardResponse(200, "text/html; charset=utf-8", Page.Value);
        if (method == "GET" && path == "/favicon.ico")
            return new DashboardResponse(204, "text/plain", []);

        if (!path.StartsWith("/api/", StringComparison.Ordinal)) return Text(404, "not found");

        if (!headers.TryGetValue(TokenHeader, out var token)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token)))
            return Error(401, "missing or wrong token — open the link agent-one dashboard printed");

        var ws = query.GetValueOrDefault("ws", DashboardData.All);
        try
        {
            return (method, path) switch
            {
                ("GET", "/api/workspaces") => Json(_data.Workspaces(), DashboardJson.Default.WorkspaceList),
                ("GET", "/api/memory") => Found(_data.Memory(ws), DashboardJson.Default.MemoryView),
                ("GET", "/api/sessions") => Found(_data.Sessions(ws), DashboardJson.Default.SessionList),
                ("GET", "/api/session") => Found(_data.Session(ws, query.GetValueOrDefault("id", "")), DashboardJson.Default.SessionDetail),
                ("GET", "/api/graph") => Found(_data.Graph(ws, int.TryParse(query.GetValueOrDefault("limit"), out var n) ? n : DashboardData.DefaultNodeLimit),
                    DashboardJson.Default.GraphView),
                ("POST", "/api/cypher") => Cypher(body),
                _ => Error(404, "no such endpoint")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Error(500, ex.Message);
        }
    }

    private DashboardResponse Cypher(byte[] body)
    {
        CypherRequest? request;
        try { request = JsonSerializer.Deserialize(body, DashboardJson.Default.CypherRequest); }
        catch (JsonException) { return Error(400, "the body must be {\"ws\": …, \"query\": …}"); }
        if (request is null || string.IsNullOrWhiteSpace(request.Query)) return Error(400, "no query");
        return Found(_data.Cypher(request.Ws, request.Query), DashboardJson.Default.CypherResult);
    }

    private static DashboardResponse Found<T>(T? value, JsonTypeInfo<T> type) where T : class =>
        value is null ? Error(404, "no such workspace or session") : Json(value, type);

    private static DashboardResponse Json<T>(T value, JsonTypeInfo<T> type) =>
        new(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, type));

    private static DashboardResponse Error(int status, string message) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new ErrorReply { Error = message }, DashboardJson.Default.ErrorReply));

    private static DashboardResponse Text(int status, string message) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message));

    internal static bool IsLoopbackHost(string host)
    {
        var name = host.Trim();
        if (name.StartsWith('['))
        {
            var end = name.IndexOf(']');
            name = end > 0 ? name[1..end] : name;
        }
        else if (name.LastIndexOf(':') is var colon and > 0) name = name[..colon];
        return name.Equals("127.0.0.1", StringComparison.Ordinal)
               || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("::1", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
            var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            map[key] = value;
        }
        return map;
    }

    // ------------------------------------------------------------------ wire

    private readonly record struct Request(string Method, string Target, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0) return null;
            filled += read;
            headerEnd = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var start = head[0].Split(' ');
        if (start.Length < 3) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        // Whatever arrived after the header block is the start of the body.
        var pending = new MemoryStream();
        pending.Write(buffer, headerEnd + 4, filled - (headerEnd + 4));

        byte[]? body;
        if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            body = await ReadChunkedAsync(stream, pending, ct);   // HttpClient's JSON helpers send this; a browser's fetch does not
        else
        {
            var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
            body = length is < 0 or > MaxBodyBytes ? null : await ReadExactAsync(stream, pending, length, ct);
        }

        return body is null ? null : new Request(start[0].ToUpperInvariant(), start[1], headers, body);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, MemoryStream pending, int length, CancellationToken ct)
    {
        var chunk = new byte[8192];
        while (pending.Length < length)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) return null;
            pending.Write(chunk, 0, read);
        }
        return pending.GetBuffer().AsSpan(0, length).ToArray();
    }

    /// <summary>Decodes a chunked body: hex size line, data, CRLF — until the zero-size chunk.</summary>
    private static async Task<byte[]?> ReadChunkedAsync(NetworkStream stream, MemoryStream pending, CancellationToken ct)
    {
        var body = new MemoryStream();
        var input = new byte[8192];
        var pos = 0;

        async Task<bool> Fill()
        {
            var read = await stream.ReadAsync(input, ct);
            if (read == 0) return false;
            pending.Write(input, 0, read);
            return true;
        }

        while (true)
        {
            int lineEnd;
            while ((lineEnd = pending.GetBuffer().AsSpan(pos, (int)pending.Length - pos).IndexOf("\r\n"u8)) < 0)
                if (!await Fill()) return null;

            var sizeText = Encoding.ASCII.GetString(pending.GetBuffer(), pos, lineEnd).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0) return null;
            pos += lineEnd + 2;
            if (size == 0) return body.ToArray();
            if (body.Length + size > MaxBodyBytes) return null;

            while (pending.Length - pos < size + 2)
                if (!await Fill()) return null;
            body.Write(pending.GetBuffer(), pos, size);
            pos += size + 2;
        }
    }

    private static async Task WriteAsync(NetworkStream stream, DashboardResponse response, CancellationToken ct)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(response.Body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            // The page is self-contained: no CDN, no fonts, nothing fetched but its own API.
            .Append("Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'\r\n")
            .Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await stream.WriteAsync(response.Body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found",
        421 => "Misdirected Request", 500 => "Internal Server Error", _ => "Status"
    };

    /// <summary>The page, embedded in the binary (Dashboard/dashboard.html) so it works offline and under AOT.</summary>
    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var stream = typeof(DashboardServer).Assembly.GetManifestResourceStream("agent-one.dashboard.html")
                           ?? throw new InvalidOperationException("dashboard.html is not embedded in this build");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    public void Dispose() => _listener.Stop();
}
