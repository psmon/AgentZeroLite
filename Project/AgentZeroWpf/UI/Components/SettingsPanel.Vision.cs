using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// WinForms is also enabled, so pin the WPF shapes/types explicitly.
using Rectangle = System.Windows.Shapes.Rectangle;
using Agent.Common;
using Agent.Common.Vision;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Vision;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Vision tab handlers (M0028) — the app's first on-device vision model.
/// Florence-2 object detection over frames captured from a playing YouTube video.
///
/// Mirrors <see cref="SettingsPanel"/>'s Music tab: model block (status + Download
/// via the shared <see cref="ModelDownloadDialog"/>) then a test block. Test flow:
/// paste a YouTube URL → START → <see cref="YouTubeFrameCaptureService"/> plays a
/// muted embed and captures frames on a timer → each frame is single-flighted
/// through <see cref="Florence2VisionInterpreter"/> → detections are drawn as a
/// bbox overlay on the last captured frame plus a text list.
/// </summary>
public partial class SettingsPanel
{
    private YouTubeFrameCaptureService? _visionCapture;
    private Florence2VisionInterpreter? _visionInterpreter;
    private CancellationTokenSource? _visionCts;
    private int _visionInferenceInFlight; // 0/1 interlocked single-flight gate
    private int _visionTick;

    // Natural pixel size of the last captured frame — used to scale detection
    // boxes (which are in captured-image pixels) onto the on-screen Image.
    private int _visionFrameW;
    private int _visionFrameH;

    private void InitializeVisionTab()
    {
        var s = VisionSettingsStore.Load();
        tbVisionModelPath.Text = VisionSettingsStore.ResolveModelDir(s);
        tbVisionUrl.Text = s.LastYouTubeUrl;
        tbVisionInterval.Text = s.CaptureIntervalMs.ToString();
        RefreshVisionModelStatus();

        Unloaded += (_, _) => DisposeVisionRuntime();
    }

    private void DisposeVisionRuntime()
    {
        try { _visionCts?.Cancel(); } catch { }
        try { _visionCapture?.Stop(); } catch { }
        try { _visionInterpreter?.DisposeAsync().AsTask().Wait(1000); } catch { }
        _visionCts = null;
        _visionCapture = null;
        _visionInterpreter = null;
    }

    private void RefreshVisionModelStatus()
    {
        var dir = string.IsNullOrWhiteSpace(tbVisionModelPath.Text)
            ? VisionSettingsStore.DefaultModelDirectory
            : tbVisionModelPath.Text.Trim();

        if (VisionSettingsStore.IsModelPresent(dir))
        {
            tbVisionModelStatus.Text = "✓ Present";
            tbVisionModelStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            tbVisionModelStatus.Text = "✗ Missing — click Download";
            tbVisionModelStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
        }
    }

    private VisionSettings ReadVisionFromUi()
    {
        var s = VisionSettingsStore.Load();
        s.LastYouTubeUrl = tbVisionUrl.Text?.Trim() ?? "";
        if (int.TryParse(tbVisionInterval.Text, out var ms))
            s.CaptureIntervalMs = Math.Clamp(ms, 400, 10_000);
        return s;
    }

    private void SetVisionTestStatus(string text, System.Windows.Media.Brush brush)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tbVisionTestStatus.Text = text;
            tbVisionTestStatus.Foreground = brush;
        }));
    }

    // ── event handlers ────────────────────────────────────────────────────

    private void OnVisionCheckModel(object sender, RoutedEventArgs e) => RefreshVisionModelStatus();

    /// <summary>
    /// Download Florence-2-base ONNX via the shared dialog. After it closes the
    /// interpreter is dropped so the next test re-loads the freshly written model.
    /// </summary>
    private void OnVisionDownloadModel(object sender, RoutedEventArgs e)
    {
        var dlg = new ModelDownloadDialog(
            title: "Download Florence-2 Vision Model",
            description: "Streams the Florence-2-base ONNX graphs from HuggingFace " +
                         "(one-time, several hundred MB). No Python / pip. Cached locally.",
            cachePathHint: VisionSettingsStore.DefaultModelDirectory,
            download: (progress, ct) => VisionModelDownloader.DownloadAsync(progress, ct),
            clearCache: VisionModelDownloader.ClearCacheDirectory)
        {
            Owner = Window.GetWindow(this),
        };
        dlg.ShowDialog();

        try { _visionInterpreter?.DisposeAsync().AsTask().Wait(1000); } catch { }
        _visionInterpreter = null;
        RefreshVisionModelStatus();
        AppLogger.Log($"[Vision] Download dialog closed | modelPresent={VisionSettingsStore.IsModelPresent(VisionSettingsStore.DefaultModelDirectory)}");
    }

    private void OnVisionSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ReadVisionFromUi();
            VisionSettingsStore.Save(s);
            tbVisionSaveStatus.Text = "✓ Saved.";
            tbVisionSaveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            AppLogger.Log($"[Vision] Saved url='{s.LastYouTubeUrl}' interval={s.CaptureIntervalMs}ms");
        }
        catch (Exception ex)
        {
            tbVisionSaveStatus.Text = $"✗ {ex.Message}";
            tbVisionSaveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Vision] Save failed", ex);
        }
    }

    /// <summary>START/STOP the YouTube capture + detection test.</summary>
    private async void OnVisionTestStartStop(object sender, RoutedEventArgs e)
    {
        var isStopping = (btnVisionTest.Content as string) == "STOP";
        if (isStopping)
        {
            StopVisionTest("user pressed STOP");
            return;
        }

        var s = ReadVisionFromUi();
        VisionSettingsStore.Save(s);

        var dir = VisionSettingsStore.ResolveModelDir(s);
        if (!VisionSettingsStore.IsModelPresent(dir))
        {
            SetVisionTestStatus("✗ Model missing — click Download first.", System.Windows.Media.Brushes.OrangeRed);
            return;
        }
        if (!YouTubeFrameCaptureService.TryParseVideoId(s.LastYouTubeUrl, out _))
        {
            SetVisionTestStatus("✗ Enter a valid YouTube URL.", System.Windows.Media.Brushes.OrangeRed);
            return;
        }

        _visionCts = new CancellationTokenSource();
        _visionTick = 0;
        cvVisionOverlay.Children.Clear();
        tbVisionResults.Clear();

        try
        {
            _visionInterpreter ??= new Florence2VisionInterpreter(s);
            SetVisionTestStatus("Loading model…", System.Windows.Media.Brushes.SkyBlue);
            var progress = new Progress<string>(msg => SetVisionTestStatus(msg, System.Windows.Media.Brushes.SkyBlue));
            var ready = await _visionInterpreter.EnsureReadyAsync(progress, _visionCts.Token);
            if (!ready)
            {
                SetVisionTestStatus("✗ Model failed to load.", System.Windows.Media.Brushes.OrangeRed);
                return;
            }

            _visionCapture = new YouTubeFrameCaptureService(wv2VisionPlayer);
            _visionCapture.FrameCaptured += OnVisionFrameCaptured;
            _visionCapture.Status += msg => SetVisionTestStatus(msg, System.Windows.Media.Brushes.Goldenrod);
            await _visionCapture.StartAsync(s.LastYouTubeUrl, s.CaptureIntervalMs);

            btnVisionTest.Content = "STOP";
            SetVisionTestStatus($"Live · capturing every {s.CaptureIntervalMs} ms", System.Windows.Media.Brushes.LightGreen);
        }
        catch (Exception ex)
        {
            btnVisionTest.Content = "START TEST";
            SetVisionTestStatus($"✗ Start failed: {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
            AppLogger.LogError("[Vision] Test start failed", ex);
        }
    }

    /// <summary>
    /// Frame arrived (UI thread, from the capture timer). Show it, then single-flight
    /// it through the interpreter on a background thread — if a prior inference is
    /// still running we drop this frame rather than queue it.
    /// </summary>
    private void OnVisionFrameCaptured(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return;

        // Decode + display on the UI thread; stash natural dims for box scaling.
        try
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(pngBytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            _visionFrameW = bmp.PixelWidth;
            _visionFrameH = bmp.PixelHeight;
            imgVisionFrame.Source = bmp;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Vision] frame decode failed: {ex.Message}");
            return;
        }

        if (_visionCts is null || _visionCts.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _visionInferenceInFlight, 1, 0) != 0) return;

        var ct = _visionCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _visionInterpreter!.InterpretAsync(pngBytes, ct).ConfigureAwait(false);
                _visionTick++;
                _ = Dispatcher.BeginInvoke(new Action(() => RenderVisionResult(result)));
            }
            catch (OperationCanceledException) { /* expected on stop */ }
            catch (Exception ex)
            {
                AppLogger.LogError("[Vision] inference failed", ex);
                SetVisionTestStatus($"✗ {ex.Message}", System.Windows.Media.Brushes.OrangeRed);
            }
            finally
            {
                Interlocked.Exchange(ref _visionInferenceInFlight, 0);
            }
        });
    }

    /// <summary>UI thread — draw the detection boxes over the frame + refresh the list.</summary>
    private void RenderVisionResult(VisionResult result)
    {
        cvVisionOverlay.Children.Clear();

        double containerW = imgVisionFrame.ActualWidth;
        double containerH = imgVisionFrame.ActualHeight;
        if (_visionFrameW > 0 && _visionFrameH > 0 && containerW > 0 && containerH > 0)
        {
            // Image uses Stretch=Uniform → letterboxed. Compute the rendered
            // content rect so boxes land on the actual pixels, not the padding.
            double scale = Math.Min(containerW / _visionFrameW, containerH / _visionFrameH);
            double contentW = _visionFrameW * scale;
            double contentH = _visionFrameH * scale;
            double offX = (containerW - contentW) / 2.0;
            double offY = (containerH - contentH) / 2.0;

            foreach (var d in result.Detections)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(1, d.Width * scale),
                    Height = Math.Max(1, d.Height * scale),
                    Stroke = System.Windows.Media.Brushes.Cyan,
                    StrokeThickness = 1.5,
                    Fill = System.Windows.Media.Brushes.Transparent,
                };
                Canvas.SetLeft(rect, offX + d.XMin * scale);
                Canvas.SetTop(rect, offY + d.YMin * scale);
                cvVisionOverlay.Children.Add(rect);

                var tag = new TextBlock
                {
                    Text = d.Label,
                    Foreground = System.Windows.Media.Brushes.Black,
                    Background = System.Windows.Media.Brushes.Cyan,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Padding = new Thickness(2, 0, 2, 0),
                };
                Canvas.SetLeft(tag, offX + d.XMin * scale);
                Canvas.SetTop(tag, Math.Max(0, offY + d.YMin * scale - 13));
                cvVisionOverlay.Children.Add(tag);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("tick #").Append(_visionTick)
          .Append(" · ").Append(result.Detections.Count).Append(" objects")
          .Append(" · inf ").Append((int)result.InferenceTime.TotalMilliseconds).Append(" ms")
          .AppendLine();
        sb.AppendLine(new string('─', 44));
        foreach (var g in result.Detections
                     .GroupBy(d => d.Label)
                     .OrderByDescending(g => g.Count()))
        {
            sb.Append(g.Count().ToString().PadLeft(3)).Append("  ").AppendLine(g.Key);
        }
        tbVisionResults.Text = sb.ToString();

        SetVisionTestStatus(
            $"✓ Live · {result.Detections.Count} objects ({(int)result.InferenceTime.TotalMilliseconds} ms)",
            System.Windows.Media.Brushes.LightGreen);
    }

    private void StopVisionTest(string reason)
    {
        AppLogger.Log($"[Vision] Stop test ({reason})");
        try { _visionCts?.Cancel(); } catch { }
        try { _visionCapture?.Stop(); } catch { }
        if (_visionCapture is not null) _visionCapture.FrameCaptured -= OnVisionFrameCaptured;
        _visionCapture = null;
        btnVisionTest.Content = "START TEST";
        SetVisionTestStatus("Stopped.", System.Windows.Media.Brushes.SkyBlue);
    }
}
