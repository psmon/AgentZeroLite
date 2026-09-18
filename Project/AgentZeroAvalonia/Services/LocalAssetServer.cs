using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Agent.Common;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// Serves the xterm.js bundle to the WebView over loopback (M0035). The WPF host maps a
/// folder to a virtual host through a WebView2-only API; <c>NativeWebView</c> exposes no
/// equivalent on either engine (<c>WebResourceRequested</c> shows the request but cannot
/// answer it), so the renderer fetches its files from here instead.
///
/// A hand-rolled HTTP/1.1 responder on a <see cref="TcpListener"/> rather than
/// <see cref="HttpListener"/>: no http.sys URL reservations on Windows, identical code on
/// macOS, and the six files it serves need nothing more. Safety is by construction:
/// bound to 127.0.0.1 on an ephemeral port, every URL carries a 32-byte random token,
/// paths are resolved under the root and rejected if they escape it, only a short
/// extension whitelist is served, and every response is <c>no-store</c>.
/// </summary>
internal sealed class LocalAssetServer : IDisposable
{
    private static readonly Lazy<LocalAssetServer> _shared = new(() =>
        new LocalAssetServer(Path.Combine(AppContext.BaseDirectory, "Wasm", "xterm")));

    /// <summary>The one server every terminal shares — one port, one token, for the app's lifetime.</summary>
    public static LocalAssetServer Shared => _shared.Value;

    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
    };

    private readonly string _root;
    private readonly string _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public int Port { get; }

    /// <summary><c>http://127.0.0.1:{port}/{token}/</c> — everything the renderer loads hangs off this.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/{_token}/";

    public LocalAssetServer(string root)
    {
        _root = Path.GetFullPath(root);
        _token = RandomNumberGenerator.GetHexString(64, lowercase: true);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
        AppLogger.Log($"[Assets] serving {_root} at 127.0.0.1:{Port}");
    }

    public string UrlFor(string relativePath, string? query = null)
        => BaseUrl + relativePath.TrimStart('/') + (string.IsNullOrEmpty(query) ? "" : "?" + query);

    // ── request resolution — pure, so the tests can pin the rules ──

    public enum Outcome { Ok, BadToken, Forbidden, NotFound }

    /// <summary>Resolve a request path (<c>/{token}/index.html?x=1</c>) to a file under the root.</summary>
    public static Outcome Resolve(string root, string token, string requestPath, out string? fullPath, out string? contentType)
    {
        fullPath = null;
        contentType = null;
        var q = requestPath.IndexOf('?');
        if (q >= 0) requestPath = requestPath[..q];
        requestPath = Uri.UnescapeDataString(requestPath);

        var prefix = "/" + token + "/";
        if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)) return Outcome.BadToken;
        var rel = requestPath[prefix.Length..];
        if (rel.Length == 0) rel = "index.html";
        if (rel.Contains("..") || rel.Contains('\\') || rel.Contains(':') || rel.Contains('\0')) return Outcome.Forbidden;

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Outcome.Forbidden;

        if (!ContentTypes.TryGetValue(Path.GetExtension(candidate), out var ct)) return Outcome.Forbidden;
        if (!File.Exists(candidate)) return Outcome.NotFound;

        fullPath = candidate;
        contentType = ct;
        return Outcome.Ok;
    }

    // ── the responder ──

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                AppLogger.Log($"[Assets] accept failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, ct);
                if (requestLine is null) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) { await WriteAsync(stream, 400, "text/plain", "bad request"u8.ToArray(), ct); return; }
                var method = parts[0];
                var target = parts[1];
                if (method is not ("GET" or "HEAD")) { await WriteAsync(stream, 405, "text/plain", "method not allowed"u8.ToArray(), ct); return; }

                var outcome = Resolve(_root, _token, target, out var path, out var contentType);
                switch (outcome)
                {
                    case Outcome.Ok:
                        var bytes = await File.ReadAllBytesAsync(path!, ct);
                        await WriteAsync(stream, 200, contentType!, method == "HEAD" ? Array.Empty<byte>() : bytes, ct, bytes.Length);
                        break;
                    case Outcome.NotFound:
                        await WriteAsync(stream, 404, "text/plain", "not found"u8.ToArray(), ct);
                        break;
                    default:
                        // A wrong token and an escaped path look the same from outside: nothing.
                        await WriteAsync(stream, 403, "text/plain", "forbidden"u8.ToArray(), ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // Client went away — the renderer navigates, cancels, retries; normal.
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Assets] serve failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Read the request line and drain the headers (we ignore them). Null on EOF.</summary>
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n <= 0) break;
            total += n;
            var span = buf.AsSpan(0, total);
            if (span.IndexOf("\r\n\r\n"u8) >= 0 || span.IndexOf("\n\n"u8) >= 0) break;
        }
        if (total == 0) return null;
        var text = Encoding.ASCII.GetString(buf, 0, total);
        var eol = text.IndexOf('\n');
        return (eol >= 0 ? text[..eol] : text).TrimEnd('\r');
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct, int? contentLength = null)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength ?? body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
