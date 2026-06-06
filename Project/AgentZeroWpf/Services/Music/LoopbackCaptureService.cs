using Agent.Common;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AgentZeroWpf.Services.Music;

/// <summary>
/// One render endpoint that <see cref="LoopbackCaptureService"/> can target.
/// <see cref="Id"/> is the WASAPI MMDevice ID — opaque GUID-like string,
/// persist it; do NOT persist <see cref="Name"/>.
/// </summary>
public sealed record LoopbackDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// System-output (WASAPI loopback) capture, exposed in the same shape as
/// <see cref="Voice.VoiceCaptureService"/> so the Music tab can swap between
/// mic and "what's playing on the speakers" without code-path forks.
///
/// Pipeline:
///   <c>WasapiLoopbackCapture</c> (float32 / mixer format, typically 48 kHz stereo)
///     → <c>BufferedWaveProvider</c>           (raw bytes)
///     → <c>ToSampleProvider</c>               (float samples)
///     → <c>StereoToMonoSampleProvider</c>     (channel reduction; bypassed if already mono)
///     → <c>WdlResamplingSampleProvider</c>    (down to 16 kHz)
///     → <c>SampleToWaveProvider16</c>         (float → int16)
///   → 16 kHz mono PCM16 bytes consumed into <see cref="_pcmBuffer"/>.
///
/// One-shot pull per <c>DataAvailable</c> event keeps latency at one WASAPI
/// buffer (~10 ms). The chain is rebuilt every <see cref="Start"/> because
/// the loopback's WaveFormat is only known after we pick the device.
/// </summary>
public sealed class LoopbackCaptureService : IDisposable
{
    private const int TargetSampleRate = 16_000;

    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _rawBuffer;
    private IWaveProvider? _pcm16Provider;
    private byte[] _pullScratch = new byte[TargetSampleRate * 2]; // 1 s of PCM16 mono

    private readonly List<byte> _pcmBuffer = new();
    private bool _isCapturing;

    public bool BufferPcm { get; set; }
    public bool Muted { get; set; }
    public bool IsCapturing => _isCapturing;

    public event Action<float>? AmplitudeChanged;

    /// <summary>
    /// Raw 16 kHz mono PCM16 byte chunk emitted alongside
    /// <see cref="AmplitudeChanged"/>. Subscribed by the Music tab's live
    /// spectrum + sliding-window inference loop. Fires from the WASAPI
    /// capture thread — handler must not block.
    /// </summary>
    public event Action<byte[]>? PcmFrameAvailable;

    public static IReadOnlyList<LoopbackDevice> ListDevices()
    {
        var list = new List<LoopbackDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                defaultId = def.ID;
            }
            catch { /* no render endpoint at all — laptop with audio service down */ }

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new LoopbackDevice(d.ID, d.FriendlyName, d.ID == defaultId));
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Music-Loopback] ListDevices failed: {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    public byte[] ConsumePcmBuffer()
    {
        lock (_pcmBuffer)
        {
            var copy = _pcmBuffer.ToArray();
            _pcmBuffer.Clear();
            return copy;
        }
    }

    public void Start(string? deviceId = null)
    {
        if (_isCapturing) return;

        var enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
            : enumerator.GetDevice(deviceId);

        _capture = new WasapiLoopbackCapture(_device);

        var srcFormat = _capture.WaveFormat;
        _rawBuffer = new BufferedWaveProvider(srcFormat)
        {
            // 32 MB ≈ 80 s of 48 kHz stereo float — well above the 10 s test
            // window. Loopback never blocks the render thread; the headroom is
            // there for sluggish dispatcher pulls during ONNX warm-up.
            BufferLength = 32 * 1024 * 1024,
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        ISampleProvider sampleProvider = _rawBuffer.ToSampleProvider();
        if (srcFormat.Channels > 1)
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider) { LeftVolume = 0.5f, RightVolume = 0.5f };

        if (srcFormat.SampleRate != TargetSampleRate)
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

        _pcm16Provider = new SampleToWaveProvider16(sampleProvider);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) =>
        {
            _isCapturing = false;
            if (e.Exception is not null)
                AppLogger.Log($"[Music-Loopback] Recording stopped with error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            else
                AppLogger.Log("[Music-Loopback] Recording stopped (clean)");
        };

        _isCapturing = true;
        _capture.StartRecording();
        AppLogger.Log($"[Music-Loopback] Capture start | device='{_device.FriendlyName}' srcFmt={srcFormat.SampleRate}Hz/{srcFormat.Channels}ch/{srcFormat.BitsPerSample}bit → 16000/mono/16");
    }

    public void Stop()
    {
        if (_capture is null) return;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        _capture = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
        _rawBuffer = null;
        _pcm16Provider = null;
        _isCapturing = false;
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (Muted || _rawBuffer is null || _pcm16Provider is null) return;
        if (e.BytesRecorded <= 0) return;

        _rawBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Drain everything the converter can give us in this batch — keeps
        // _pcmBuffer in lock-step with real-time playback even when ONNX is
        // mid-warm-up and the UI dispatcher is sluggish.
        while (true)
        {
            int read = _pcm16Provider.Read(_pullScratch, 0, _pullScratch.Length);
            if (read <= 0) break;

            // RMS for the level meter — operates on the int16 mono output so
            // the bar reflects what the model will actually see.
            double sumSquares = 0;
            int samples = read / 2;
            for (int i = 0; i < read; i += 2)
            {
                short s = (short)(_pullScratch[i] | (_pullScratch[i + 1] << 8));
                double norm = s / 32768.0;
                sumSquares += norm * norm;
            }
            if (samples > 0)
            {
                float rms = (float)Math.Sqrt(sumSquares / samples);
                try { AmplitudeChanged?.Invoke(rms); }
                catch (Exception ex) { AppLogger.Log($"[Music-Loopback] Amplitude handler threw: {ex.Message}"); }
            }

            if (BufferPcm)
            {
                lock (_pcmBuffer)
                {
                    for (int i = 0; i < read; i++) _pcmBuffer.Add(_pullScratch[i]);
                }
            }

            // Fan out the chunk to live subscribers (spectrum bars, rolling
            // inference). Copy out of the scratch buffer because subscribers
            // run async on the dispatcher thread.
            if (PcmFrameAvailable is not null)
            {
                var chunk = new byte[read];
                Buffer.BlockCopy(_pullScratch, 0, chunk, 0, read);
                try { PcmFrameAvailable.Invoke(chunk); }
                catch (Exception ex) { AppLogger.Log($"[Music-Loopback] PcmFrame handler threw: {ex.Message}"); }
            }
        }
    }
}
