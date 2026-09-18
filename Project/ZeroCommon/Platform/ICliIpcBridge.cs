using System.IO.Pipes;
using System.Text;

namespace Agent.Common.Platform;

/// <summary>
/// The CLI process ↔ running GUI channel, cross-platform (M0033). The WPF host uses
/// WM_COPYDATA plus named memory-mapped files, which only exist on Windows; this
/// bridge is a named pipe (Unix domain socket underneath on macOS/Linux, same BCL
/// API). One connection carries one request line and one response line, both UTF-8
/// JSON — the very same <c>{"command":…}</c> objects and reply payloads the WPF host
/// already speaks, so the CLI printers and the <c>-cli help agentzero</c> guide need
/// no change.
/// </summary>
public interface ICliIpcBridge
{
    /// <summary>
    /// GUI side: accept connections and answer each request through
    /// <paramref name="handler"/>. Handlers run off the accept loop, so a slow verb
    /// (a page load) does not block the next caller. Dispose the handle to stop.
    /// </summary>
    IDisposable StartServer(Func<string, CancellationToken, Task<string>> handler);

    /// <summary>CLI side: one request, one reply. Null when no GUI answers within the timeout.</summary>
    string? SendRequest(string request, int timeoutMs = 5000);

    /// <inheritdoc cref="SendRequest"/>
    Task<string?> SendRequestAsync(string request, int timeoutMs, CancellationToken ct);
}

public static class CliIpcProtocol
{
    /// <summary>Requests and replies larger than this are refused — a page read is tens of KB, not a megabyte.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static string ErrorJson(string message)
        => "{\"ok\":false,\"error\":\"" + Escape(message) + "\"}";

    public static string Escape(string s)
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

public static class CliIpcBridge
{
    public const string DefaultPipeName = "AgentZeroLite.cli";

    public static ICliIpcBridge Create(string pipeName = DefaultPipeName) => new NamedPipeIpcBridge(pipeName);
}

/// <summary>Named-pipe implementation; the only one so far, used on every OS.</summary>
public sealed class NamedPipeIpcBridge : ICliIpcBridge
{
    private readonly string _pipeName;

    public NamedPipeIpcBridge(string pipeName) => _pipeName = pipeName;

    public string PipeName => _pipeName;

    public IDisposable StartServer(Func<string, CancellationToken, Task<string>> handler)
    {
        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => AcceptLoopAsync(handler, cts.Token));
        return new ServerHandle(cts, loop);
    }

    private async Task AcceptLoopAsync(Func<string, CancellationToken, Task<string>> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                AppLogger.Log($"[Ipc] accept failed: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            // Off the accept loop: the next CLI call must not wait for this one's verb.
            var connection = server;
            _ = Task.Run(() => ServeAsync(connection, handler, ct), ct);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream server,
        Func<string, CancellationToken, Task<string>> handler, CancellationToken ct)
    {
        try
        {
            using (server)
            {
                var request = await ReadLineAsync(server, CliIpcProtocol.MaxMessageBytes, ct);
                string response;
                if (request is null)
                    response = CliIpcProtocol.ErrorJson("request too large or empty");
                else
                {
                    try { response = await handler(request, ct) ?? ""; }
                    catch (Exception ex) { response = CliIpcProtocol.ErrorJson(ex.Message); }
                }
                await WriteLineAsync(server, response, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Log($"[Ipc] serve failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public string? SendRequest(string request, int timeoutMs = 5000)
        => SendRequestAsync(request, timeoutMs, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<string?> SendRequestAsync(string request, int timeoutMs, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(request) > CliIpcProtocol.MaxMessageBytes)
            return CliIpcProtocol.ErrorJson("request too large");
        try
        {
            using var timeout = new CancellationTokenSource(Math.Max(100, timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(linked.Token);
            await WriteLineAsync(client, request, linked.Token);
            return await ReadLineAsync(client, CliIpcProtocol.MaxMessageBytes, linked.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Ipc] SendRequest failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── one line in, one line out ──────────────────────────────────────────

    private static async Task WriteLineAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\r", "").Replace("\n", " ") + "\n");
        if (bytes.Length > CliIpcProtocol.MaxMessageBytes)
            bytes = Encoding.UTF8.GetBytes(CliIpcProtocol.ErrorJson("response too large") + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads up to the first newline; null when the cap is hit or the peer sent nothing.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n <= 0) break;
            var newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
            if (newline >= 0)
            {
                buffer.Write(chunk, 0, newline);
                break;
            }
            buffer.Write(chunk, 0, n);
            if (buffer.Length > maxBytes) return null;
        }
        if (buffer.Length == 0) return null;
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimEnd('\r');
    }

    private sealed class ServerHandle : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;

        public ServerHandle(CancellationTokenSource cts, Task loop)
        {
            _cts = cts;
            _loop = loop;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }
}
