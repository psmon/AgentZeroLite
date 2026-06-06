using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
// Project also enables WinForms, so System.Windows.Shapes.Rectangle collides
// with System.Drawing.Rectangle. Alias instead of `using System.Windows.Shapes;`
// to pin to the WPF type without affecting anything else in the file.
using Rectangle = System.Windows.Shapes.Rectangle;
using Agent.Common;
using Agent.Common.Music;
using Agent.Common.Voice.Streams;
using AgentZeroWpf.Services.Music;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Music tab handlers — AST AudioSet ONNX instrument classifier + log-mel spectrum.
///
/// Mirrors <see cref="SettingsPanel.Voice"/> intentionally so the two side
/// panels feel identical: provider/model block → test block with mic capture
/// + result list. Capture goes through <see cref="VoiceCaptureService"/> at
/// 16 kHz mono — the same pipeline VoiceLLM uses — so a user with a working
/// mic on the Voice tab automatically has a working mic here.
///
/// First iteration is single-shot: press START → mic records for N seconds →
/// stop → ONNX inference → top-K labels rendered into a textbox. Continuous /
/// streaming classification is a future follow-up.
/// </summary>
public partial class SettingsPanel
{
    private bool _musicInitializing;
    private VoiceCaptureService? _musicMicCapture;
    private LoopbackCaptureService? _musicLoopbackCapture;
    private OnnxAstClassifier? _musicClassifier;
    private CancellationTokenSource? _musicCts;

    // ── Realtime mode state ────────────────────────────────────────────
    private const int SpectrumBarCount = 64;
    private const int SpectrumPaintIntervalMs = 33; // ~30 Hz
    private const int LiveInferenceCadenceMs = 1500;

    private readonly SpectrumBars _musicSpectrum = new();
    private Rectangle[]? _musicSpectrumBars;
    private DateTime _musicLastSpectrumPaint = DateTime.MinValue;

    // Sliding-window rolling buffer of the most recent N seconds of audio.
    // Drained for inference snapshots, never cleared by inference itself —
    // each AST pass sees the most recent N seconds whether or not a previous
    // pass is still running.
    private readonly List<byte> _musicRollingPcm = new();
    private int _musicRollingTargetBytes;

    private Task? _musicInferenceLoopTask;
    private int _musicInferenceInFlight; // 0/1 — interlocked single-flight gate
    private int _musicInferenceTick;

    private void InitializeMusicTab()
    {
        _musicInitializing = true;
        try
        {
            var s = MusicSettingsStore.Load();

            tbMusicModelPath.Text = string.IsNullOrWhiteSpace(s.ModelPath)
                ? MusicSettingsStore.DefaultModelPath
                : s.ModelPath;
            tbMusicLabelsPath.Text = string.IsNullOrWhiteSpace(s.LabelsPath)
                ? MusicSettingsStore.DefaultLabelsPath
                : s.LabelsPath;

            tbMusicDuration.Text = s.TestDurationSeconds.ToString();
            tbMusicTopK.Text = s.TopK.ToString();

            SelectMusicSourceTag(s.InputSource);
            RepopulateMusicDevicePicker(s);
            RefreshMusicModelStatus();

            // Dispose mic/loopback + ONNX session on panel teardown so we don't
            // leak either across hide/show cycles.
            Unloaded += (_, _) => DisposeMusicRuntime();
        }
        finally
        {
            _musicInitializing = false;
        }
    }

    private void DisposeMusicRuntime()
    {
        try { _musicCts?.Cancel(); } catch { }
        try { _musicMicCapture?.Dispose(); } catch { }
        try { _musicLoopbackCapture?.Dispose(); } catch { }
        try { _musicInferenceLoopTask?.Wait(500); } catch { }
        try { _musicClassifier?.DisposeAsync().AsTask().Wait(500); } catch { }
        _musicCts = null;
        _musicMicCapture = null;
        _musicLoopbackCapture = null;
        _musicInferenceLoopTask = null;
        _musicClassifier = null;
        lock (_musicRollingPcm) _musicRollingPcm.Clear();
    }

    private void SelectMusicSourceTag(string source)
    {
        foreach (var obj in cbMusicInputSource.Items)
        {
            if (obj is ComboBoxItem ci && (ci.Tag as string) == source)
            {
                cbMusicInputSource.SelectedItem = ci;
                return;
            }
        }
        if (cbMusicInputSource.Items.Count > 0) cbMusicInputSource.SelectedIndex = 0;
    }

    private string ReadMusicSourceTag()
        => (cbMusicInputSource.SelectedItem as ComboBoxItem)?.Tag as string
           ?? MusicInputSourceNames.Microphone;

    /// <summary>
    /// Repopulate the device combo with mic OR render endpoints based on the
    /// active source, then try to restore the persisted selection for that
    /// source. Called on init, source change, and Refresh click.
    /// </summary>
    private void RepopulateMusicDevicePicker(MusicSettings s)
    {
        var source = ReadMusicSourceTag();
        if (source == MusicInputSourceNames.SystemLoopback)
        {
            tbMusicDeviceLabel.Text = "Render Endpoint";
            RefreshMusicLoopbackDevices(s.LoopbackDeviceId);
        }
        else
        {
            tbMusicDeviceLabel.Text = "Input Device";
            RefreshMusicInputDevices(s.InputDeviceId);
        }
    }

    private void RefreshMusicInputDevices(string selectedDeviceId)
    {
        cbMusicInputDevice.Items.Clear();
        try
        {
            var devices = VoiceCaptureService.ListDevices();
            if (devices.Count == 0)
            {
                cbMusicInputDevice.Items.Add(new ComboBoxItem
                {
                    Content = "(no input devices detected)",
                    Tag = "",
                });
                cbMusicInputDevice.SelectedIndex = 0;
                return;
            }
            foreach (var d in devices)
                cbMusicInputDevice.Items.Add(new ComboBoxItem
                {
                    Content = $"[{d.DeviceNumber}] {d.Name}",
                    Tag = d.DeviceNumber.ToString(),
                });

            if (!string.IsNullOrEmpty(selectedDeviceId))
            {
                foreach (var obj in cbMusicInputDevice.Items)
                    if (obj is ComboBoxItem ci && (ci.Tag as string) == selectedDeviceId)
                    {
                        cbMusicInputDevice.SelectedItem = ci;
                        return;
                    }
            }
            cbMusicInputDevice.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            cbMusicInputDevice.Items.Add(new ComboBoxItem
            {
                Content = $"(enumeration failed: {ex.Message})",
                Tag = "",
            });
            cbMusicInputDevice.SelectedIndex = 0;
            AppLogger.LogError("[Music] Device enumeration failed", ex);
        }
    }

    private void RefreshMusicLoopbackDevices(string selectedDeviceId)
    {
        cbMusicInputDevice.Items.Clear();
        try
        {
            var devices = LoopbackCaptureService.ListDevices();
            if (devices.Count == 0)
            {
                cbMusicInputDevice.Items.Add(new ComboBoxItem
                {
                    Content = "(no render endpoints detected)",
                    Tag = "",
                });
                cbMusicInputDevice.SelectedIndex = 0;
                return;
            }

            // Always offer "Default" as the first row — covers the common case
            // where the user just wants whatever Windows is currently playing
            // through.
            cbMusicInputDevice.Items.Add(new ComboBoxItem
            {
                Content = "(Default — current Windows playback device)",
                Tag = "",
            });
            foreach (var d in devices)
                cbMusicInputDevice.Items.Add(new ComboBoxItem
                {
                    Content = d.IsDefault ? $"★ {d.Name}" : d.Name,
                    Tag = d.Id,
                });

            if (!string.IsNullOrEmpty(selectedDeviceId))
            {
                foreach (var obj in cbMusicInputDevice.Items)
                    if (obj is ComboBoxItem ci && (ci.Tag as string) == selectedDeviceId)
                    {
                        cbMusicInputDevice.SelectedItem = ci;
                        return;
                    }
            }
            cbMusicInputDevice.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            cbMusicInputDevice.Items.Add(new ComboBoxItem
            {
                Content = $"(enumeration failed: {ex.Message})",
                Tag = "",
            });
            cbMusicInputDevice.SelectedIndex = 0;
            AppLogger.LogError("[Music-Loopback] Render-endpoint enumeration failed", ex);
        }
    }

    private void RefreshMusicModelStatus()
    {
        var path = string.IsNullOrWhiteSpace(tbMusicModelPath.Text)
            ? MusicSettingsStore.DefaultModelPath
            : tbMusicModelPath.Text.Trim();

        if (File.Exists(path))
        {
            var sizeMb = new FileInfo(path).Length / (1024.0 * 1024.0);
            tbMusicModelStatus.Text = $"✓ Present ({sizeMb:F1} MB)";
            tbMusicModelStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            tbMusicModelStatus.Text = "✗ Missing — see install steps below";
            tbMusicModelStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
        }
    }

    private MusicSettings ReadMusicFromUi()
    {
        var s = MusicSettingsStore.Load();
        s.Provider = MusicClassifierProviderNames.AstAudioSet;
        s.ModelPath = tbMusicModelPath.Text?.Trim() ?? "";
        s.LabelsPath = tbMusicLabelsPath.Text?.Trim() ?? "";

        var source = ReadMusicSourceTag();
        s.InputSource = source;
        var deviceTag = (cbMusicInputDevice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (source == MusicInputSourceNames.SystemLoopback)
            s.LoopbackDeviceId = deviceTag;
        else
            s.InputDeviceId = deviceTag;

        if (int.TryParse(tbMusicDuration.Text, out var dur))
            s.TestDurationSeconds = Math.Clamp(dur, 1, 30);
        if (int.TryParse(tbMusicTopK.Text, out var k))
            s.TopK = Math.Clamp(k, 1, 20);

        return s;
    }

    private void SetMusicTestStatus(string text, System.Windows.Media.Brush brush)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tbMusicTestStatus.Text = text;
            tbMusicTestStatus.Foreground = brush;
        }));
    }

    private void AppendMusicResult(string line)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tbMusicResults.AppendText(line + Environment.NewLine);
            tbMusicResults.ScrollToEnd();
        }), DispatcherPriority.Background);
    }

    private void ClearMusicResults()
    {
        Dispatcher.BeginInvoke(new Action(() => tbMusicResults.Clear()));
    }

    // ── event handlers ───────────────────────────────────────────────────

    private void OnMusicCheckModel(object sender, RoutedEventArgs e)
    {
        RefreshMusicModelStatus();
    }

    /// <summary>
    /// "Download" — opens the shared <see cref="ModelDownloadDialog"/> wired to
    /// <see cref="AstModelDownloader.DownloadAsync"/>. After the dialog closes
    /// the existing classifier session is dropped + status refreshed so the
    /// next Test re-loads the freshly written model file.
    /// </summary>
    private void OnMusicDownloadModel(object sender, RoutedEventArgs e)
    {
        var dlg = new ModelDownloadDialog(
            title: "Download AST AudioSet Model",
            description: "Streams model.onnx (~347 MB) from onnx-community + " +
                         "class_labels_indices.csv from the AST authors' repo. " +
                         "One-time; cached locally.",
            cachePathHint: MusicSettingsStore.DefaultModelDirectory,
            download: (progress, ct) => AstModelDownloader.DownloadAsync(progress, ct),
            clearCache: AstModelDownloader.ClearCacheDirectory)
        {
            Owner = Window.GetWindow(this),
        };
        dlg.ShowDialog();

        // Cached session is stale once the file on disk changes — drop it so
        // the next Test re-loads.
        try { _musicClassifier?.DisposeAsync().AsTask().Wait(500); } catch { }
        _musicClassifier = null;
        RefreshMusicModelStatus();
        AppLogger.Log($"[Music] Download dialog closed | modelPresent={System.IO.File.Exists(MusicSettingsStore.DefaultModelPath)}");
    }

    private void OnMusicRefreshDevices(object sender, RoutedEventArgs e)
    {
        RepopulateMusicDevicePicker(MusicSettingsStore.Load());
        SetMusicTestStatus(
            $"Device list refreshed ({cbMusicInputDevice.Items.Count} item(s)).",
            System.Windows.Media.Brushes.SkyBlue);
    }

    /// <summary>
    /// Source ComboBox changed (Microphone ↔ System Output). Repopulate the
    /// device picker with the matching device pool. Persisted device id for
    /// each source is restored from the on-disk settings so toggling back and
    /// forth doesn't lose the user's prior pick.
    /// </summary>
    private void OnMusicInputSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_musicInitializing) return;
        var s = MusicSettingsStore.Load();
        // Reflect the *new* source choice when restoring the per-source device id.
        s.InputSource = ReadMusicSourceTag();
        RepopulateMusicDevicePicker(s);
        tbMusicSaveStatus.Text = "● Unsaved — click Save Music.";
        tbMusicSaveStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
    }

    private void OnMusicSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ReadMusicFromUi();
            MusicSettingsStore.Save(s);
            tbMusicSaveStatus.Text = "✓ Saved.";
            tbMusicSaveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            AppLogger.Log($"[Music] Saved provider={s.Provider} source={s.InputSource} micDev='{s.InputDeviceId}' loopDev='{s.LoopbackDeviceId}' modelPath='{s.ModelPath}' duration={s.TestDurationSeconds}s topK={s.TopK}");

            // Existing session is stale — drop it so the next Test re-loads
            // with the saved paths.
            try { _musicClassifier?.DisposeAsync().AsTask().Wait(500); } catch { }
            _musicClassifier = null;

            RefreshMusicModelStatus();
        }
        catch (Exception ex)
        {
            tbMusicSaveStatus.Text = $"✗ {ex.Message}";
            tbMusicSaveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Music] Save failed", ex);
        }
    }

    /// <summary>
    /// START/STOP for the live test. START spins up the chosen capture source,
    /// subscribes a frame handler that feeds (a) the spectrum analyzer and
    /// (b) a sliding rolling-PCM buffer, then launches a background loop that
    /// snapshots the buffer every <see cref="LiveInferenceCadenceMs"/> ms and
    /// runs AST inference. UI updates every cycle — labels refresh in place,
    /// spectrum repaints at ~30 Hz.
    /// </summary>
    private void OnMusicTestStartStop(object sender, RoutedEventArgs e)
    {
        var isStopping = (btnMusicTest.Content as string) == "STOP";
        if (isStopping)
        {
            StopMusicTest("user pressed STOP");
            return;
        }

        var s = ReadMusicFromUi();
        MusicSettingsStore.Save(s);
        ClearMusicResults();
        ResetSpectrumBars();

        var modelPath = MusicSettingsStore.ResolveModelPath(s);
        if (!File.Exists(modelPath))
        {
            SetMusicTestStatus($"✗ Model file missing: {modelPath}", System.Windows.Media.Brushes.OrangeRed);
            AppendMusicResult($"Model not found at: {modelPath}");
            AppendMusicResult("Click the Download button above to fetch it (~347 MB, one-time).");
            return;
        }

        // Rolling buffer = max(window, 10s) of 16 kHz mono PCM16. Trim on
        // every append so memory stays bounded regardless of test length.
        _musicRollingTargetBytes = Math.Max(2, s.TestDurationSeconds) * SpectrumBars.SampleRate * 2;
        lock (_musicRollingPcm) _musicRollingPcm.Clear();

        _musicCts = new CancellationTokenSource();
        var ct = _musicCts.Token;

        try
        {
            string sourceLabel;
            if (s.InputSource == MusicInputSourceNames.SystemLoopback)
            {
                try { _musicLoopbackCapture?.Dispose(); } catch { }
                _musicLoopbackCapture = new LoopbackCaptureService { BufferPcm = false };
                _musicLoopbackCapture.AmplitudeChanged -= OnMusicAmplitudeChanged;
                _musicLoopbackCapture.AmplitudeChanged += OnMusicAmplitudeChanged;
                _musicLoopbackCapture.PcmFrameAvailable -= OnMusicLivePcmFrame;
                _musicLoopbackCapture.PcmFrameAvailable += OnMusicLivePcmFrame;
                _musicLoopbackCapture.Start(s.LoopbackDeviceId);
                sourceLabel = "system output (loopback)";
            }
            else
            {
                _musicMicCapture ??= new VoiceCaptureService();
                _musicMicCapture.BufferPcm = false; // we accumulate via FrameAvailable directly
                _musicMicCapture.VadThreshold = 0f;
                _musicMicCapture.AmplitudeChanged -= OnMusicAmplitudeChanged;
                _musicMicCapture.AmplitudeChanged += OnMusicAmplitudeChanged;
                _musicMicCapture.FrameAvailable -= OnMusicLiveMicFrame;
                _musicMicCapture.FrameAvailable += OnMusicLiveMicFrame;
                var deviceNumber = ParseMusicDeviceNumber(s.InputDeviceId);
                _musicMicCapture.Start(deviceNumber);
                sourceLabel = "microphone";
            }

            btnMusicTest.Content = "STOP";
            SetMusicTestStatus(
                $"Live · {sourceLabel} · {s.TestDurationSeconds}s window · re-infer every {LiveInferenceCadenceMs} ms",
                System.Windows.Media.Brushes.LightGreen);

            _musicInferenceTick = 0;
            _musicInferenceLoopTask = Task.Run(() => MusicLiveInferenceLoopAsync(s, ct));
        }
        catch (Exception ex)
        {
            btnMusicTest.Content = "START TEST";
            SetMusicTestStatus($"✗ Capture start failed: {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
            AppLogger.LogError("[Music] Capture start failed", ex);
        }
    }

    // Mic path bridges Agent.Common.Voice.Streams.MicFrame → byte[].
    private void OnMusicLiveMicFrame(MicFrame frame) => OnMusicLivePcmFrame(frame.Pcm16k);

    /// <summary>
    /// Frame handler — called from the capture thread. Three jobs:
    ///   1. Append to the rolling sliding-window buffer (trimmed to N seconds).
    ///   2. Feed the spectrum analyzer's ring buffer.
    ///   3. If 33 ms have passed since the last paint, dispatcher-marshal a
    ///      bar-height refresh. Throttling keeps the dispatcher quiet during
    ///      heavy inference passes.
    /// </summary>
    private void OnMusicLivePcmFrame(byte[] pcm)
    {
        if (pcm is null || pcm.Length == 0) return;

        lock (_musicRollingPcm)
        {
            _musicRollingPcm.AddRange(pcm);
            int over = _musicRollingPcm.Count - _musicRollingTargetBytes;
            if (over > 0) _musicRollingPcm.RemoveRange(0, over);
        }

        _musicSpectrum.Push(pcm);

        var now = DateTime.UtcNow;
        if ((now - _musicLastSpectrumPaint).TotalMilliseconds < SpectrumPaintIntervalMs) return;
        _musicLastSpectrumPaint = now;

        var bars = _musicSpectrum.ComputeBars(SpectrumBarCount);
        Dispatcher.BeginInvoke(new Action(() => UpdateSpectrumBars(bars)), DispatcherPriority.Render);
    }

    /// <summary>
    /// Background loop that periodically snapshots the rolling buffer and runs
    /// AST inference. Single-flight via interlocked gate — if the previous
    /// inference is still running when the cadence fires, we just skip the
    /// tick. AST typically finishes in 200–400 ms on CPU, so a 1.5 s cadence
    /// is comfortable headroom.
    /// </summary>
    private async Task MusicLiveInferenceLoopAsync(MusicSettings s, CancellationToken ct)
    {
        try
        {
            _musicClassifier ??= new OnnxAstClassifier(s);
            SetMusicTestStatus("Loading model…", System.Windows.Media.Brushes.SkyBlue);
            var progress = new Progress<string>(msg => SetMusicTestStatus(msg, System.Windows.Media.Brushes.SkyBlue));
            var ready = await _musicClassifier.EnsureReadyAsync(progress, ct).ConfigureAwait(false);
            if (!ready)
            {
                SetMusicTestStatus("✗ Model failed to load (see status).", System.Windows.Media.Brushes.OrangeRed);
                return;
            }

            // Give the rolling buffer ~2 s to fill before the first pass, else
            // AST sees a clip mostly made of silence padding.
            await Task.Delay(2000, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                if (Interlocked.CompareExchange(ref _musicInferenceInFlight, 1, 0) == 0)
                {
                    try
                    {
                        byte[] snapshot;
                        lock (_musicRollingPcm) { snapshot = _musicRollingPcm.ToArray(); }

                        if (snapshot.Length >= SpectrumBars.SampleRate * 2)
                        {
                            var result = await _musicClassifier
                                .ClassifyAsync(snapshot, s.TopK, ct)
                                .ConfigureAwait(false);
                            _musicInferenceTick++;
                            // BeginInvoke returns a DispatcherOperation that's awaitable on .NET 6+;
                            // discard explicitly to silence CS4014 — fire-and-forget is intentional.
                            _ = Dispatcher.BeginInvoke(new Action(() => RenderMusicResultLive(result, snapshot.Length)));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLogger.LogError("[Music] Live inference iteration failed", ex);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _musicInferenceInFlight, 0);
                    }
                }
                await Task.Delay(LiveInferenceCadenceMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            AppLogger.LogError("[Music] Live inference loop crashed", ex);
            SetMusicTestStatus($"✗ {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
        }
    }

    /// <summary>UI-thread: rewrite the result panel in place. Replaces the prior tick's labels so the panel doesn't grow unbounded.</summary>
    private void RenderMusicResultLive(MusicInferenceResult result, int snapshotBytes)
    {
        tbMusicResults.Clear();
        var sb = new System.Text.StringBuilder();
        sb.Append("tick #").Append(_musicInferenceTick)
          .Append(" · window ").Append(snapshotBytes / 32_000.0).Append("s")
          .Append(" · mel ").Append(result.SpectrogramFrames).Append("×").Append(result.SpectrogramBins)
          .Append(" · pre ").Append((int)result.PreprocessTime.TotalMilliseconds).Append(" ms")
          .Append(" · inf ").Append((int)result.InferenceTime.TotalMilliseconds).Append(" ms")
          .AppendLine();
        sb.AppendLine(new string('─', 60));
        foreach (var lbl in result.TopLabels)
        {
            var bar = new string('█', (int)(lbl.Score * 30));
            sb.Append(lbl.Score.ToString("P1").PadLeft(6)).Append("  ")
              .Append(bar.PadRight(30)).Append("  ")
              .AppendLine(lbl.Name);
        }
        tbMusicResults.Text = sb.ToString();
        SetMusicTestStatus($"✓ Live · top: {result.TopLabels.FirstOrDefault()?.Name ?? "(none)"} ({result.TopLabels.FirstOrDefault()?.Score:P1})",
            System.Windows.Media.Brushes.LightGreen);
    }

    private void StopMusicTest(string reason)
    {
        AppLogger.Log($"[Music] Stop test ({reason})");
        try { _musicCts?.Cancel(); } catch { }
        try { _musicMicCapture?.Stop(); } catch { }
        try { _musicLoopbackCapture?.Stop(); } catch { }
        btnMusicTest.Content = "START TEST";
        SetMusicTestStatus("Stopped.", System.Windows.Media.Brushes.SkyBlue);
    }

    private void OnMusicAmplitudeChanged(float rms)
    {
        var pct = Math.Min(100, rms * 300);
        Dispatcher.BeginInvoke(new Action(() => pbMusicLevel.Value = pct), DispatcherPriority.Render);
    }

    // ── Spectrum canvas ──────────────────────────────────────────────────

    private void OnMusicSpectrumSizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildSpectrumBars();
    }

    /// <summary>
    /// Lazy-build the <see cref="SpectrumBarCount"/> Rectangle children that
    /// represent each frequency band. Recomputes child widths on every canvas
    /// resize so the bars fill the available width without gaps. Colours go
    /// cyan (lows) → magenta (highs) for a quick visual sense of where energy
    /// is sitting.
    /// </summary>
    private void BuildSpectrumBars()
    {
        if (cvMusicSpectrum is null) return;
        if (cvMusicSpectrum.ActualWidth <= 0 || cvMusicSpectrum.ActualHeight <= 0) return;

        cvMusicSpectrum.Children.Clear();
        _musicSpectrumBars = new Rectangle[SpectrumBarCount];

        double w = cvMusicSpectrum.ActualWidth;
        double h = cvMusicSpectrum.ActualHeight;
        double barW = Math.Max(1.0, w / SpectrumBarCount - 1);

        for (int i = 0; i < SpectrumBarCount; i++)
        {
            double t = (double)i / (SpectrumBarCount - 1);
            // Cyan (#00E5FF) → magenta (#FF2D95) gradient across bands.
            byte r = (byte)(0 + t * 255);
            byte g = (byte)(229 + t * (45 - 229));
            byte b = (byte)(255 + t * (149 - 255));
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();

            var rect = new Rectangle
            {
                Width = barW,
                Height = 1,
                Fill = brush,
            };
            Canvas.SetLeft(rect, i * (w / SpectrumBarCount));
            Canvas.SetTop(rect, h - 1);
            cvMusicSpectrum.Children.Add(rect);
            _musicSpectrumBars[i] = rect;
        }
    }

    private void UpdateSpectrumBars(float[] values)
    {
        if (_musicSpectrumBars is null || _musicSpectrumBars.Length != values.Length)
        {
            BuildSpectrumBars();
            if (_musicSpectrumBars is null) return;
        }
        double h = cvMusicSpectrum.ActualHeight;
        for (int i = 0; i < _musicSpectrumBars.Length; i++)
        {
            double barH = Math.Max(1.0, values[i] * h);
            _musicSpectrumBars[i].Height = barH;
            Canvas.SetTop(_musicSpectrumBars[i], h - barH);
        }
    }

    private void ResetSpectrumBars()
    {
        if (_musicSpectrumBars is null) return;
        double h = cvMusicSpectrum.ActualHeight;
        foreach (var r in _musicSpectrumBars)
        {
            r.Height = 1;
            Canvas.SetTop(r, h - 1);
        }
    }

    private static int ParseMusicDeviceNumber(string id)
        => VoiceRuntimeFactory.ParseDeviceNumber(id);
}
