using System.IO;
using Agent.Common;
using Agent.Common.Music;
using AgentZeroWpf.Services.Music;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Live music classification surface for the WebDev plugin sandbox (M0025 —
/// Agent Band). Spins up the same WASAPI-loopback + AST AudioSet pipeline the
/// Settings/Music tab drives, but emits ticks through the WebView2 bridge
/// instead of repainting WPF.
///
/// Each tick carries:
///   • Top-K AudioSet labels with sigmoid scores (instrument identification)
///   • A bar-domain spectrum snapshot (16 log-banded bins, 0..1 normalized)
/// The Agent Band plugin uses the labels to spawn / animate performer sprites
/// and the spectrum bars to drive the bottom visualizer.
///
/// One live session per host. <see cref="StartMusicAsync"/> is idempotent: a
/// second call while already capturing returns the live status without
/// restart.
/// </summary>
public sealed partial class WebDevHost
{
    /// <summary>
    /// One inference cycle's payload. <see cref="Labels"/> is sorted high-to-low
    /// by score; <see cref="Spectrum"/> is the bar-band snapshot taken at the
    /// same moment so the plugin can correlate "what's playing now" with
    /// "what the model heard".
    /// </summary>
    public sealed record MusicTickInfo(
        IReadOnlyList<MusicLabel> Labels,
        float[] Spectrum,
        int Frames,
        int Bins,
        int InferMs);

    private const int MusicSpectrumBars = 32;
    private const int MusicLiveCadenceMs = 1500;
    private static readonly int MusicRollingTargetBytes =
        10 * SpectrumBars.SampleRate * 2; // 10 s of 16 kHz mono PCM16

    private readonly SemaphoreSlim _musicLock = new(1, 1);
    private LoopbackCaptureService? _musicLoopback;
    private VoiceCaptureService? _musicMic;
    private OnnxAstClassifier? _musicClassifier;
    private CancellationTokenSource? _musicCts;
    private Task? _musicLoopTask;
    private readonly SpectrumBars _musicSpectrum = new();
    private readonly List<byte> _musicRollingPcm = new();
    private int _musicInferenceInFlight; // 0/1 single-flight gate
    private int _musicLiveTopK = 5;
    private string _musicSource = MusicInputSourceNames.SystemLoopback;
    private long _lastMusicAmpTick;

    public event Action<MusicTickInfo>? MusicTick;
    public event Action<float>? MusicAmplitude; // ~10 Hz throttled

    public bool IsMusicLive =>
        _musicLoopback is { IsCapturing: true }
        || _musicMic is { IsCapturing: true };

    /// <summary>
    /// Begin live music capture + AST inference loop. Returns a payload with
    /// {ok, capturing, source, topK, cadenceMs} so the plugin can sync its
    /// header. The model file is checked up-front — missing model returns
    /// {ok:false, error:"model-missing", modelPath} so the plugin can render
    /// the install hint instead of silently failing.
    /// </summary>
    public async Task<object> StartMusicAsync(
        string? source,
        string? deviceId,
        int? topK,
        int? cadenceMs)
    {
        await _musicLock.WaitAsync();
        try
        {
            if (IsMusicLive)
            {
                return new
                {
                    ok = true,
                    capturing = true,
                    source = _musicSource,
                    topK = _musicLiveTopK,
                    cadenceMs = MusicLiveCadenceMs,
                };
            }

            // Normalize args. Default to SystemLoopback because the mission
            // is "watch what's playing on the speakers" — mic is opt-in.
            var src = string.IsNullOrWhiteSpace(source)
                ? MusicInputSourceNames.SystemLoopback
                : source!;
            if (src != MusicInputSourceNames.Microphone && src != MusicInputSourceNames.SystemLoopback)
                src = MusicInputSourceNames.SystemLoopback;

            var k = topK is int kk && kk > 0 ? Math.Clamp(kk, 1, 20) : 5;

            var settings = MusicSettingsStore.Load();
            settings.TopK = k;
            settings.InputSource = src;
            var modelPath = MusicSettingsStore.ResolveModelPath(settings);
            if (!File.Exists(modelPath))
            {
                AppLogger.Log($"[WebDev:Music] start aborted — model missing at {modelPath}");
                return new { ok = false, error = "model-missing", modelPath };
            }

            _musicSource = src;
            _musicLiveTopK = k;
            lock (_musicRollingPcm) _musicRollingPcm.Clear();

            try
            {
                if (src == MusicInputSourceNames.SystemLoopback)
                {
                    var lb = new LoopbackCaptureService { BufferPcm = false };
                    lb.AmplitudeChanged += OnMusicAmplitude;
                    lb.PcmFrameAvailable += OnMusicPcmFrame;
                    lb.Start(deviceId ?? "");
                    _musicLoopback = lb;
                }
                else
                {
                    var mic = new VoiceCaptureService
                    {
                        BufferPcm = false,
                        VadThreshold = 0f,
                    };
                    mic.AmplitudeChanged += OnMusicAmplitude;
                    mic.FrameAvailable += OnMusicMicFrame;
                    var num = AgentZeroWpf.Services.Voice.VoiceRuntimeFactory.ParseDeviceNumber(deviceId ?? "");
                    mic.Start(num);
                    _musicMic = mic;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Music] capture start failed: {ex.GetType().Name}: {ex.Message}");
                TearDownMusicLocked();
                return new { ok = false, error = ex.Message };
            }

            _musicCts = new CancellationTokenSource();
            var token = _musicCts.Token;
            _musicLoopTask = Task.Run(() => MusicInferenceLoopAsync(settings, token));

            AppLogger.Log($"[WebDev:Music] live start | source={src} topK={k} model={Path.GetFileName(modelPath)}");
            return new
            {
                ok = true,
                capturing = true,
                source = src,
                topK = k,
                cadenceMs = MusicLiveCadenceMs,
            };
        }
        finally { _musicLock.Release(); }
    }

    public async Task<object> StopMusicAsync()
    {
        await _musicLock.WaitAsync();
        try
        {
            TearDownMusicLocked();
            return new { ok = true };
        }
        finally { _musicLock.Release(); }
    }

    public object GetMusicStatus()
    {
        return new
        {
            capturing = IsMusicLive,
            source = _musicSource,
            topK = _musicLiveTopK,
            cadenceMs = MusicLiveCadenceMs,
        };
    }

    private void OnMusicMicFrame(Agent.Common.Voice.Streams.MicFrame frame) =>
        OnMusicPcmFrame(frame.Pcm16k);

    private void OnMusicPcmFrame(byte[] pcm)
    {
        if (pcm is null || pcm.Length == 0) return;

        lock (_musicRollingPcm)
        {
            _musicRollingPcm.AddRange(pcm);
            int over = _musicRollingPcm.Count - MusicRollingTargetBytes;
            if (over > 0) _musicRollingPcm.RemoveRange(0, over);
        }
        _musicSpectrum.Push(pcm);
    }

    private void OnMusicAmplitude(float rms)
    {
        // ~10 Hz throttle — matches the voice-note amplitude pattern.
        var now = Environment.TickCount64;
        if (now - _lastMusicAmpTick < 90) return;
        _lastMusicAmpTick = now;
        MusicAmplitude?.Invoke(rms);
    }

    private async Task MusicInferenceLoopAsync(MusicSettings settings, CancellationToken ct)
    {
        try
        {
            _musicClassifier ??= new OnnxAstClassifier(settings);
            var ready = await _musicClassifier.EnsureReadyAsync(null, ct).ConfigureAwait(false);
            if (!ready)
            {
                AppLogger.Log("[WebDev:Music] AST model failed to load");
                return;
            }

            // Give the rolling buffer ~2s to fill before first inference so
            // we don't classify pure silence padding.
            await Task.Delay(2000, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                if (System.Threading.Interlocked.CompareExchange(ref _musicInferenceInFlight, 1, 0) == 0)
                {
                    try
                    {
                        byte[] snapshot;
                        lock (_musicRollingPcm) { snapshot = _musicRollingPcm.ToArray(); }

                        if (snapshot.Length >= SpectrumBars.SampleRate * 2)
                        {
                            var result = await _musicClassifier
                                .ClassifyAsync(snapshot, _musicLiveTopK, ct)
                                .ConfigureAwait(false);
                            var bars = _musicSpectrum.ComputeBars(MusicSpectrumBars);
                            MusicTick?.Invoke(new MusicTickInfo(
                                Labels: result.TopLabels,
                                Spectrum: bars,
                                Frames: result.SpectrogramFrames,
                                Bins: result.SpectrogramBins,
                                InferMs: (int)result.InferenceTime.TotalMilliseconds));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[WebDev:Music] inference iteration failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _musicInferenceInFlight, 0);
                    }
                }
                await Task.Delay(MusicLiveCadenceMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Music] inference loop crashed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TearDownMusicLocked()
    {
        try { _musicCts?.Cancel(); } catch { }
        try { _musicLoopTask?.Wait(500); } catch { }

        var lb = _musicLoopback; _musicLoopback = null;
        if (lb is not null)
        {
            lb.AmplitudeChanged -= OnMusicAmplitude;
            lb.PcmFrameAvailable -= OnMusicPcmFrame;
            try { lb.Stop(); } catch { }
            try { lb.Dispose(); } catch { }
        }

        var mic = _musicMic; _musicMic = null;
        if (mic is not null)
        {
            mic.AmplitudeChanged -= OnMusicAmplitude;
            mic.FrameAvailable -= OnMusicMicFrame;
            try { mic.Stop(); } catch { }
            try { mic.Dispose(); } catch { }
        }

        try { _musicCts?.Dispose(); } catch { }
        _musicCts = null;
        _musicLoopTask = null;
        // Keep _musicClassifier alive across Start/Stop cycles so a second
        // Start doesn't re-load the ~347 MB ONNX session.
        lock (_musicRollingPcm) _musicRollingPcm.Clear();
        AppLogger.Log("[WebDev:Music] live stopped");
    }
}
