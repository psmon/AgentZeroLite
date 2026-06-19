using System.IO.Pipes;

namespace Agent.Common.Platform;

/// <summary>
/// CLI 프로세스 ↔ 실행 중 GUI 간 요청/응답 채널.
/// WPF는 WM_COPYDATA + MemoryMappedFile(Win32 전용)을 썼지만, 이 추상화는
/// .NET NamedPipe로 cross-platform화한다 (Windows=named pipe, Unix=domain socket
/// — 동일 BCL API). 한 줄(JSON 등) 요청 → 한 줄 응답의 단순 프로토콜.
/// </summary>
public interface ICliIpcBridge
{
    /// <summary>GUI 측: 연결을 수신하며 각 요청을 <paramref name="handler"/>로 처리.
    /// 반환된 핸들을 Dispose하면 수신 중단.</summary>
    IDisposable StartServer(Func<string, string> handler);

    /// <summary>CLI 측: 요청 1건 전송 후 응답 반환(실패/타임아웃 시 null).</summary>
    string? SendRequest(string request, int timeoutMs = 5000);
}

/// <summary>OS별 팩토리. 현재는 모든 OS에서 NamedPipe 구현.</summary>
public static class CliIpcBridge
{
    public const string DefaultPipeName = "AgentZeroLite.cli";

    public static ICliIpcBridge Create(string pipeName = DefaultPipeName)
        => new NamedPipeIpcBridge(pipeName);
}

internal sealed class NamedPipeIpcBridge : ICliIpcBridge
{
    private readonly string _pipeName;

    public NamedPipeIpcBridge(string pipeName) => _pipeName = pipeName;

    public IDisposable StartServer(Func<string, string> handler)
    {
        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => AcceptLoopAsync(handler, cts.Token));
        return new ServerHandle(cts, loop);
    }

    private async Task AcceptLoopAsync(Func<string, string> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

                var request = await reader.ReadLineAsync(ct);
                if (request is not null)
                {
                    string response;
                    try { response = handler(request) ?? ""; }
                    catch (Exception ex) { response = $"{{\"ok\":false,\"error\":\"{ex.Message}\"}}"; }
                    await writer.WriteLineAsync(response.AsMemory(), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AppLogger.Log($"[Ipc] server loop error: {ex.Message}"); }
            finally { server?.Dispose(); }
        }
    }

    public string? SendRequest(string request, int timeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);

            writer.WriteLine(request);
            return reader.ReadLine();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Ipc] SendRequest failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private sealed class ServerHandle : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        public ServerHandle(CancellationTokenSource cts, Task loop) { _cts = cts; _loop = loop; }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }
}
