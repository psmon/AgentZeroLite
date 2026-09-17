using System.Net.Sockets;

namespace ZeroWearable.Ble;

/// <summary>
/// Carries the AskBot app's Akka byte stream the last hop, between the BLE link and this
/// host's own remoting port.
///
///   watch: Akka PDUs --0xAB--> NUS notify --> BleLink --> TCP 127.0.0.1:2552 --> Akka
///          Akka PDUs <--0xAB-- NUS write  <--         <--                     <--
///
/// Connecting the process to its own listener looks odd but is exactly right: Akka.Remote
/// does not care where the TCP peer is, and this way the association, the serializers and
/// the endpoint bookkeeping are all the stock, tested code paths. The alternative -
/// speaking the association protocol to ourselves in-process - would mean re-implementing
/// the server half of remoting.
/// </summary>
public sealed class BleTunnel : IAsyncDisposable
{
    private readonly BleLink _link;
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string, string> _log;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public long ToAkka, ToDevice;
    public bool Open => _stream != null;

    public BleTunnel(BleLink link, string host, int port, Action<string, string> log)
    {
        _link = link;
        _host = host;
        _port = port;
        _log = log;
        _link.FrameReceived += OnFrame;
        _link.Disconnected += () => _ = StopAsync();
    }

    /// <summary>
    /// Opens the loopback socket. Called on the first tunnel chunk rather than at BLE
    /// connect: a device with no AskBot app never needs it, and an idle socket would show
    /// up in the Akka logs as a peer that never associates.
    /// </summary>
    private async Task EnsureAsync()
    {
        if (_stream != null) return;
        await _gate.WaitAsync();
        try
        {
            if (_stream != null) return;

            var tcp = new TcpClient();
            await tcp.ConnectAsync(_host, _port);
            tcp.NoDelay = true;
            _tcp = tcp;
            _stream = tcp.GetStream();
            _cts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAkkaToDeviceAsync(_cts.Token));
            _log("info", $"tunnel open to {_host}:{_port}");
        }
        catch (Exception ex)
        {
            _log("warn", $"tunnel could not reach {_host}:{_port}: {ex.Message}");
            _stream = null;
            _tcp?.Dispose();
            _tcp = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnFrame(byte[] frame)
    {
        if (frame.Length < 2 || frame[0] != BleTags.Tunnel) return;   // mic frames belong to Chat
        _ = ForwardAsync(frame);
    }

    private async Task ForwardAsync(byte[] frame)
    {
        try
        {
            await EnsureAsync();
            var stream = _stream;
            if (stream == null) return;
            await stream.WriteAsync(frame.AsMemory(1));
            await stream.FlushAsync();
            Interlocked.Add(ref ToAkka, frame.Length - 1);
        }
        catch (Exception ex)
        {
            _log("warn", $"tunnel write to Akka failed: {ex.Message}");
            await StopAsync();
        }
    }

    private async Task PumpAkkaToDeviceAsync(CancellationToken ct)
    {
        var stream = _stream!;
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n <= 0)
                {
                    _log("info", "tunnel closed by the Akka side");
                    break;
                }
                // One BLE write per chunk; the 4-byte length prefix inside the PDU stream is
                // what reassembles messages, so any split is safe.
                var chunk = Math.Clamp(_link.MaxPdu - 4, 20, 508);
                for (var off = 0; off < n; off += chunk)
                {
                    var len = Math.Min(chunk, n - off);
                    var frame = new byte[len + 1];
                    frame[0] = BleTags.Tunnel;
                    Buffer.BlockCopy(buffer, off, frame, 1, len);
                    if (!await _link.SendRawAsync(frame, ct))
                    {
                        _log("warn", "tunnel write to the device failed");
                        return;
                    }
                }
                Interlocked.Add(ref ToDevice, n);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _log("warn", $"tunnel read from Akka failed: {ex.Message}");
        }
        finally
        {
            await StopAsync();
        }
    }

    /// <summary>
    /// Drops the loopback socket. The device notices its stream died and re-associates,
    /// which is the same recovery path as a lost TCP connection.
    /// </summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_stream == null && _tcp == null) return;
            _cts?.Cancel();
            _stream?.Dispose();
            _tcp?.Dispose();
            _stream = null;
            _tcp = null;
            _log("info", "tunnel closed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
