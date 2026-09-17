using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace ZeroWearable.Ble;

public sealed record ScanResult(ulong Address, string Name, short Rssi, BluetoothAddressType AddressType)
{
    public string AddressHex => Address.ToString("X12");
}

/// <summary>
/// BLE central for the Nordic UART Service the watch firmware exposes (hud_ble.cpp).
/// Ported from amoled_chat_host's BleLink with the DI/Options plumbing removed - the
/// WinRT sequencing and the retry comments are the valuable part and were paid for in
/// debugging, so they survive verbatim.
///
/// One central can hold the device, which is exactly why this host owns the link:
/// AskBot's tunnel and the Chat app's protocol share it instead of fighting over it.
/// </summary>
public sealed class BleLink : IDisposable
{
    public static readonly Guid NusService = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid NusRx = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid NusTx = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    private readonly Action<string, string> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private BluetoothLEDevice? _dev;
    private GattSession? _session;
    private GattDeviceService? _svc;
    private GattCharacteristic? _rx;
    private GattCharacteristic? _tx;
    private readonly StringBuilder _lineBuf = new();

    public bool IsConnected { get; private set; }
    public string DeviceName { get; private set; } = "";
    public ulong Address { get; private set; }
    public string AddressHex => Address.ToString("X12");
    public int MaxPdu => _session?.MaxPduSize ?? 23;
    public long Sent, Dropped, RxLines, RxFrames;
    public string LastError { get; private set; } = "";

    /// <summary>A complete text line from the device, newline stripped ("R {json}").</summary>
    public event Action<string>? LineReceived;
    /// <summary>A raw binary write from the device: 0xA5 microphone frame or 0xAB tunnel chunk.</summary>
    public event Action<byte[]>? FrameReceived;
    public event Action? Connected;
    public event Action? Disconnected;

    public BleLink(Action<string, string> log) => _log = log;

    private void Info(string m) => _log("info", m);
    private void Warn(string m) => _log("warn", m);

    // ---------------------------------------------------------------- scan

    public async Task<List<ScanResult>> ScanAsync(int seconds, CancellationToken ct = default)
    {
        var found = new Dictionary<ulong, ScanResult>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            lock (found)
            {
                if (found.TryGetValue(e.BluetoothAddress, out var prev) && string.IsNullOrEmpty(name))
                    name = prev.Name;   // scan response without a name: keep the earlier one
                found[e.BluetoothAddress] = new ScanResult(e.BluetoothAddress, name ?? "",
                    e.RawSignalStrengthInDBm, e.BluetoothAddressType);
            }
        };
        watcher.Start();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), ct);
        }
        finally
        {
            watcher.Stop();
        }
        lock (found)
            return found.Values.Where(r => r.Name.Length > 0).OrderByDescending(r => r.Rssi).ToList();
    }

    public async Task<ScanResult?> FindByNameAsync(string name, int seconds, CancellationToken ct = default)
    {
        var all = await ScanAsync(seconds, ct);
        return all.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------- connect

    public async Task<bool> ConnectAsync(ulong address, string? name,
        BluetoothAddressType addressType = BluetoothAddressType.Public, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        BluetoothLEDevice? dev = null;
        GattSession? session = null;
        GattDeviceService? svc = null;
        try
        {
            if (IsConnected && Address == address) return true;
            await DisconnectCoreAsync();

            Info($"connecting to {address:X12} ({name ?? "?"}, {addressType})");
            dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
            if (dev is null)
            {
                Fail("device not reachable (FromBluetoothAddressAsync returned null)");
                return false;
            }

            session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
            var active = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs> onStatus = (_, e) =>
            {
                Info($"session status: {e.Status} (error {e.Error})");
                if (e.Status == GattSessionStatus.Active) active.TrySetResult(true);
            };
            session.SessionStatusChanged += onStatus;
            session.MaintainConnection = true;
            // Wait for the link to be Active before touching GATT, otherwise discovery races
            // the connection and fails with 0x80070016 (ERROR_BAD_COMMAND).
            if (session.SessionStatus != GattSessionStatus.Active)
            {
                var done = await Task.WhenAny(active.Task, Task.Delay(TimeSpan.FromSeconds(12), ct));
                if (done != active.Task)
                    Warn($"session did not become Active within 12 s (status={session.SessionStatus}); trying discovery anyway");
            }
            session.SessionStatusChanged -= onStatus;
            Info($"session {session.SessionStatus}, mtu={session.MaxPduSize}");

            svc = await DiscoverNusAsync(dev, ct);
            if (svc is null)
            {
                Fail("NUS service not found");
                return false;
            }

            var rxRes = await svc.GetCharacteristicsForUuidAsync(NusRx, BluetoothCacheMode.Uncached);
            var txRes = await svc.GetCharacteristicsForUuidAsync(NusTx, BluetoothCacheMode.Uncached);
            if (rxRes.Status != GattCommunicationStatus.Success || rxRes.Characteristics.Count == 0 ||
                txRes.Status != GattCommunicationStatus.Success || txRes.Characteristics.Count == 0)
            {
                Fail("NUS RX/TX characteristics not found");
                return false;
            }

            var rx = rxRes.Characteristics[0];
            var tx = txRes.Characteristics[0];
            tx.ValueChanged += OnValueChanged;
            var cccd = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccd != GattCommunicationStatus.Success)
                Warn($"subscribe to TX notifications failed: {cccd} (device->host traffic will not arrive)");

            dev.ConnectionStatusChanged += OnConnectionStatusChanged;

            _dev = dev;
            _session = session;
            _svc = svc;
            _rx = rx;
            _tx = tx;
            dev = null;
            session = null;
            svc = null;   // owned by the fields now
            Address = address;
            DeviceName = string.IsNullOrEmpty(_dev.Name) ? name ?? "" : _dev.Name;
            IsConnected = true;
            LastError = "";
            lock (_lineBuf) _lineBuf.Clear();
            Info($"connected: {DeviceName} [{AddressHex}] mtu={MaxPdu}");
            Connected?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            await DisconnectCoreAsync();
            return false;
        }
        finally
        {
            // A failed attempt must release everything: MaintainConnection would otherwise keep
            // the link alive, the device stops advertising and every later scan comes back empty.
            try
            {
                svc?.Dispose();
                session?.Dispose();
                dev?.Dispose();
            }
            catch
            {
                // already gone
            }
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Service discovery with retries. On Windows the first GetGattServices* call right after
    /// the link comes up can fail with COMException 0x80070016 (ERROR_BAD_COMMAND) - the stack
    /// is still resolving the peer - and simply needs retrying. Falls back from ForUuid/Uncached
    /// to a full Uncached and finally Cached enumeration.
    /// </summary>
    private async Task<GattDeviceService?> DiscoverNusAsync(BluetoothLEDevice dev, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var mode = attempt <= 4 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
            try
            {
                var res = attempt is 3 or 4   // full enumeration first: ForUuid fails with 0x80070016 here
                    ? await dev.GetGattServicesForUuidAsync(NusService, mode)
                    : await dev.GetGattServicesAsync(mode);
                if (res.Status == GattCommunicationStatus.Success)
                {
                    var hit = res.Services.FirstOrDefault(x => x.Uuid == NusService);
                    if (hit != null)
                    {
                        if (attempt > 1) Info($"service discovery ok on attempt {attempt} ({mode})");
                        foreach (var other in res.Services)
                            if (!ReferenceEquals(other, hit)) other.Dispose();
                        return hit;
                    }
                    Warn($"discovery attempt {attempt}: {res.Services.Count} services, NUS not among them");
                }
                else
                {
                    Warn($"discovery attempt {attempt}: status {res.Status} (protocolError {res.ProtocolError})");
                }
            }
            catch (Exception ex)
            {
                Warn($"discovery attempt {attempt} ({mode}): {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
        }
        return null;
    }

    private void Fail(string msg)
    {
        LastError = msg;
        Warn($"connect failed: {msg}");
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private Task DisconnectCoreAsync()
    {
        var was = IsConnected;
        IsConnected = false;
        try
        {
            if (_tx != null) _tx.ValueChanged -= OnValueChanged;
            if (_dev != null) _dev.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _svc?.Dispose();
            _session?.Dispose();
            _dev?.Dispose();
        }
        catch
        {
            // disposing a dead handle is not interesting
        }
        _tx = null;
        _rx = null;
        _svc = null;
        _session = null;
        _dev = null;
        if (was)
        {
            Info($"disconnected from {DeviceName}");
            Disconnected?.Invoke();
        }
        return Task.CompletedTask;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && IsConnected)
        {
            Warn($"link dropped ({DeviceName}); the reconnect loop will retry");
            _ = DisconnectAsync();
        }
    }

    // ------------------------------------------------------------- receive

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] data;
        try
        {
            data = args.CharacteristicValue.ToArray();
        }
        catch
        {
            return;
        }
        if (data.Length == 0) return;

        // Binary first: a tunnel chunk or a microphone frame must never reach the line buffer.
        if (data[0] is BleTags.Tunnel or BleTags.MicFrame)
        {
            Interlocked.Increment(ref RxFrames);
            FrameReceived?.Invoke(data);
            return;
        }

        string text;
        lock (_lineBuf)
        {
            _lineBuf.Append(Encoding.UTF8.GetString(data));
            if (_lineBuf.Length > 16384) _lineBuf.Clear();
            text = _lineBuf.ToString();
        }
        int nl;
        while ((nl = text.IndexOf('\n')) >= 0)
        {
            var line = text[..nl].TrimEnd('\r');
            text = text[(nl + 1)..];
            lock (_lineBuf)
            {
                _lineBuf.Clear();
                _lineBuf.Append(text);
            }
            if (line.Length == 0) continue;
            Interlocked.Increment(ref RxLines);
            try
            {
                LineReceived?.Invoke(line);
            }
            catch (Exception ex)
            {
                Warn($"line handler: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- send

    /// <summary>One text line, newline appended, chunked to the negotiated MTU.</summary>
    public Task<bool> SendLineAsync(string line, CancellationToken ct = default)
    {
        if (!line.EndsWith('\n')) line += "\n";
        return SendRawAsync(Encoding.UTF8.GetBytes(line), ct);
    }

    public async Task<bool> SendRawAsync(byte[] data, CancellationToken ct = default)
    {
        var rx = _rx;
        if (!IsConnected || rx is null)
        {
            Interlocked.Increment(ref Dropped);
            return false;
        }
        var chunk = Math.Clamp(MaxPdu - 3, 20, 509);
        await _writeLock.WaitAsync(ct);
        try
        {
            for (var off = 0; off < data.Length; off += chunk)
            {
                var n = Math.Min(chunk, data.Length - off);
                var buf = new byte[n];
                Buffer.BlockCopy(data, off, buf, 0, n);
                var st = await rx.WriteValueAsync(buf.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                if (st != GattCommunicationStatus.Success)
                {
                    Warn($"write failed: {st}");
                    Interlocked.Increment(ref Dropped);
                    return false;
                }
            }
            Interlocked.Increment(ref Sent);
            return true;
        }
        catch (Exception ex)
        {
            Warn($"write exception: {ex.Message}");
            Interlocked.Increment(ref Dropped);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => DisconnectCoreAsync().GetAwaiter().GetResult();
}

/// <summary>First byte of a binary write on the shared NUS link.</summary>
public static class BleTags
{
    /// <summary>device -> host microphone audio (Chat app).</summary>
    public const byte MicFrame = 0xA5;
    /// <summary>host -> device spoken answer (Chat app).</summary>
    public const byte SpeechFrame = 0xA6;
    /// <summary>either direction: a chunk of the Akka byte stream (AskBot app).</summary>
    public const byte Tunnel = 0xAB;
}
