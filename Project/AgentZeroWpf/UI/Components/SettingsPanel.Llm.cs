using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Llm;
using AgentZeroWpf.Services;
using LLama.Native;

namespace AgentZeroWpf.UI.Components;

public partial class SettingsPanel
{
    private DispatcherTimer? _llmMonitorTimer;
    private ILocalChatSession? _llmSession;
    private CancellationTokenSource? _llmDownloadCts;
    private CancellationTokenSource? _llmChatCts;
    private double _llmLastTps;

    // Native llama.cpp + Vulkan doesn't cleanly support dispose-then-reload
    // inside the same process (internal global state retained across dispose).
    // Track whether we've already loaded at least once; if so, require an app
    // restart before loading again.
    private static bool _hasLoadedOnceInProcess;

    private IReadOnlyList<VulkanDeviceInfo> _vulkanDevices = Array.Empty<VulkanDeviceInfo>();

    private void InitializeLlmTab()
    {
        // Enumerate Vulkan devices once — dropdown repopulated each tab open
        _vulkanDevices = VulkanDeviceEnumerator.Enumerate();
        cbLlmGpuDevice.Items.Clear();
        cbLlmGpuDevice.Items.Add("(auto - prefer discrete)");
        foreach (var d in _vulkanDevices)
            cbLlmGpuDevice.Items.Add(d.ToString());
        if (_vulkanDevices.Count == 0)
            AppLogger.Log("[LLM] Vulkan enumeration returned no devices — vulkaninfo may be missing");
        else
            AppLogger.Log($"[LLM] Vulkan devices: {string.Join(" | ", _vulkanDevices.Select(d => d.ToString()))}");

        // Hydrate option editors from persisted settings
        var settings = LlmSettingsStore.Load();
        ApplySettingsToUi(settings);

        // Model path + download state
        RefreshLlmModelStatus();

        // Runtime state + button wiring
        LlmService.StateChanged += OnLlmServiceStateChanged;
        ApplyRuntimeState(LlmService.State);

        // Monitor poll
        _llmMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _llmMonitorTimer.Tick += (_, _) => RefreshMonitor();
        _llmMonitorTimer.Start();
        RefreshMonitor();

        Unloaded += (_, _) =>
        {
            _llmMonitorTimer?.Stop();
            _llmMonitorTimer = null;
            LlmService.StateChanged -= OnLlmServiceStateChanged;
        };
    }

    private void ApplySettingsToUi(LlmRuntimeSettings s)
    {
        cbLlmBackend.SelectedIndex = s.Backend == LocalLlmBackend.Vulkan ? 1 : 0;
        tbLlmGpuLayers.Text = s.GpuLayerCount.ToString(CultureInfo.InvariantCulture);
        tbLlmContextSize.Text = s.ContextSize.ToString(CultureInfo.InvariantCulture);
        tbLlmMaxTokens.Text = s.MaxTokens.ToString(CultureInfo.InvariantCulture);
        tbLlmTemperature.Text = s.Temperature.ToString("0.0#", CultureInfo.InvariantCulture);
        SelectGpuDeviceInUi(s.VulkanDeviceIndex);
        chkLlmFlashAttn.IsChecked = s.FlashAttention;
        chkLlmNoKqvOffload.IsChecked = s.NoKqvOffload;
        cbLlmKvTypeK.SelectedIndex = KvTypeToIndex(s.KvCacheTypeK);
        cbLlmKvTypeV.SelectedIndex = KvTypeToIndex(s.KvCacheTypeV);
        chkLlmUseMmap.IsChecked = s.UseMemoryMap;
    }

    private static int KvTypeToIndex(GGMLType t) => t switch
    {
        GGMLType.GGML_TYPE_F16 => 0,
        GGMLType.GGML_TYPE_F32 => 1,
        GGMLType.GGML_TYPE_Q8_0 => 2,
        GGMLType.GGML_TYPE_Q4_0 => 3,
        _ => 0
    };

    private static GGMLType IndexToKvType(int i) => i switch
    {
        1 => GGMLType.GGML_TYPE_F32,
        2 => GGMLType.GGML_TYPE_Q8_0,
        3 => GGMLType.GGML_TYPE_Q4_0,
        _ => GGMLType.GGML_TYPE_F16
    };

    private void SelectGpuDeviceInUi(int persistedIndex)
    {
        // Item 0 = auto. Items 1..N = device list in enumerator order.
        if (persistedIndex < 0)
        {
            cbLlmGpuDevice.SelectedIndex = 0;
            return;
        }
        for (int i = 0; i < _vulkanDevices.Count; i++)
            if (_vulkanDevices[i].Index == persistedIndex)
            {
                cbLlmGpuDevice.SelectedIndex = i + 1;
                return;
            }
        cbLlmGpuDevice.SelectedIndex = 0; // fallback to auto
    }

    private int ReadSelectedGpuDeviceIndex()
    {
        var ci = cbLlmGpuDevice.SelectedIndex;
        if (ci <= 0) // 0 = auto
            return VulkanDeviceEnumerator.PickDefaultIndex(_vulkanDevices);
        var arrIdx = ci - 1;
        if (arrIdx >= 0 && arrIdx < _vulkanDevices.Count)
            return _vulkanDevices[arrIdx].Index;
        return -1;
    }

    private LlmRuntimeSettings? ReadSettingsFromUi()
    {
        try
        {
            var backend = (cbLlmBackend.SelectedIndex == 1) ? LocalLlmBackend.Vulkan : LocalLlmBackend.Cpu;
            var gpuLayers = int.Parse(tbLlmGpuLayers.Text, CultureInfo.InvariantCulture);
            var ctx = uint.Parse(tbLlmContextSize.Text, CultureInfo.InvariantCulture);
            var maxTok = int.Parse(tbLlmMaxTokens.Text, CultureInfo.InvariantCulture);
            var temp = float.Parse(tbLlmTemperature.Text, CultureInfo.InvariantCulture);
            var devIdx = ReadSelectedGpuDeviceIndex();
            return new LlmRuntimeSettings
            {
                Backend = backend,
                GpuLayerCount = gpuLayers,
                ContextSize = ctx,
                MaxTokens = maxTok,
                Temperature = temp,
                VulkanDeviceIndex = devIdx,
                FlashAttention = chkLlmFlashAttn.IsChecked == true,
                NoKqvOffload = chkLlmNoKqvOffload.IsChecked == true,
                KvCacheTypeK = IndexToKvType(cbLlmKvTypeK.SelectedIndex),
                KvCacheTypeV = IndexToKvType(cbLlmKvTypeV.SelectedIndex),
                UseMemoryMap = chkLlmUseMmap.IsChecked == true
            };
        }
        catch
        {
            return null;
        }
    }

    private void RefreshLlmModelStatus()
    {
        var path = LlmModelLocator.ResolveExistingOrTarget();
        tbLlmModelPath.Text = path;

        if (File.Exists(path))
        {
            var sizeMb = new FileInfo(path).Length / (1024 * 1024);
            tbLlmModelState.Text = $"[Ready {sizeMb} MB]";
            tbLlmModelState.Foreground = Brushes.Cyan;
            pbLlmDownload.Value = 100;
            btnLlmDownload.IsEnabled = false;
            btnLlmDownload.Content = "Downloaded";
            tbLlmDownloadStatus.Text = "Model present. Ready to load.";
        }
        else
        {
            tbLlmModelState.Text = "[Missing]";
            tbLlmModelState.Foreground = Brushes.Magenta;
            pbLlmDownload.Value = 0;
            btnLlmDownload.IsEnabled = true;
            btnLlmDownload.Content = "Download";
            tbLlmDownloadStatus.Text = $"Will download to {LlmModelLocator.UserPath} (~4.9 GB)";
        }
    }

    // ── Download ─────────────────────────────────────────────────────────

    private async void OnLlmDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_llmDownloadCts is not null) // currently downloading -> treat as cancel
        {
            _llmDownloadCts.Cancel();
            return;
        }

        var dest = LlmModelLocator.UserPath;
        _llmDownloadCts = new CancellationTokenSource();
        btnLlmDownload.Content = "Cancel";
        pbLlmDownload.Value = 0;

        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = p.TotalBytes > 0 ? (p.BytesReceived * 100.0 / p.TotalBytes) : 0;
            pbLlmDownload.Value = pct;
            var mbps = p.BytesPerSecond / (1024 * 1024);
            var receivedMb = p.BytesReceived / (1024 * 1024);
            var totalMb = p.TotalBytes / (1024 * 1024);
            tbLlmDownloadStatus.Text = $"{receivedMb} / {totalMb} MB  @ {mbps:0.0} MB/s  ({pct:0.0}%)";
        });

        try
        {
            await LlmModelDownloader.DownloadAsync(
                LlmModelLocator.DownloadUrl, dest, progress, _llmDownloadCts.Token);
            tbLlmDownloadStatus.Text = "Download complete.";
        }
        catch (OperationCanceledException)
        {
            tbLlmDownloadStatus.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            tbLlmDownloadStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _llmDownloadCts?.Dispose();
            _llmDownloadCts = null;
            RefreshLlmModelStatus();
        }
    }

    // ── Options / Load / Unload ─────────────────────────────────────────

    private void OnLlmAutoRecommendClick(object sender, RoutedEventArgs e)
    {
        long modelBytes = 0;
        var modelPath = LlmModelLocator.ResolveExistingOrTarget();
        try
        {
            if (File.Exists(modelPath))
                modelBytes = new FileInfo(modelPath).Length;
        }
        catch { }

        var snap = LlmSystemMonitor.Snapshot();
        var totalRamMb = (long)(Environment.WorkingSet + GC.GetTotalMemory(false)) / (1024 * 1024); // not accurate, replaced below
        try
        {
            var ci = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
            var freeMb = (long)ci.NextValue();
            var recommendation = LlmAutoRecommend.Compute(new LlmSystemSnapshotInput(
                ModelFileBytes: modelBytes,
                FreeGpuMb: snap.GpuTotalMb.HasValue && snap.GpuUsedMb.HasValue
                    ? snap.GpuTotalMb.Value - snap.GpuUsedMb.Value
                    : 0,
                TotalGpuMb: snap.GpuTotalMb ?? 0,
                FreeRamMb: freeMb,
                TotalRamMb: totalRamMb,
                VulkanDevices: _vulkanDevices,
                ModelFileName: Path.GetFileName(modelPath)));

            ApplySettingsToUi(recommendation.Settings);
            AppLogger.Log($"[LLM] Auto-recommend applied. freeGpuMb={(snap.GpuTotalMb - snap.GpuUsedMb)?.ToString() ?? "?"} freeRamMb={freeMb} modelMb={modelBytes / (1024 * 1024)}");
            foreach (var r in recommendation.Reasons)
                AppLogger.Log("  · " + r);
            tbLlmRuntimeState.Text = $"State: Auto-recommend applied — {recommendation.Reasons.Count} reasons in log";
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[LLM] Auto-recommend failed", ex);
            tbLlmRuntimeState.Text = $"State: Auto-recommend failed — {ex.Message}";
        }
    }

    private void OnLlmSaveOptionsClick(object sender, RoutedEventArgs e)
    {
        var s = ReadSettingsFromUi();
        if (s is null)
        {
            tbLlmRuntimeState.Text = "State: Invalid options (check numeric fields)";
            return;
        }
        LlmSettingsStore.Save(s);
        tbLlmRuntimeState.Text = "State: Options saved";
    }

    private async void OnLlmLoadClick(object sender, RoutedEventArgs e)
    {
        if (LlmService.State != LlmServiceState.Unloaded)
        {
            AppLogger.Log($"[LLM] Load click ignored — current state={LlmService.State}");
            return;
        }

        if (_hasLoadedOnceInProcess)
        {
            tbLlmRuntimeState.Text = "State: Restart the app before loading again (native state cannot be cleanly reset in-process).";
            AppLogger.Log("[LLM] Reload blocked — native reload within same process is unsafe, restart required");
            return;
        }

        if (!LlmModelLocator.IsAvailable())
        {
            tbLlmRuntimeState.Text = "State: Model file missing";
            return;
        }

        var s = ReadSettingsFromUi();
        if (s is null)
        {
            tbLlmRuntimeState.Text = "State: Invalid options";
            return;
        }

        // Vulkan + large context sometimes crashes native during CreateContext
        // on laptop NVIDIA drivers — auto-clamp until llama.cpp upstream resolves it.
        if (s.Backend == LocalLlmBackend.Vulkan && s.ContextSize > 2048)
        {
            AppLogger.Log($"[LLM] Clamping Vulkan ContextSize {s.ContextSize} → 2048 (native instability above this)");
            s.ContextSize = 2048;
            tbLlmContextSize.Text = "2048";
        }

        AppLogger.Log($"[LLM] Options pre-load: fa={s.FlashAttention} noKqv={s.NoKqvOffload} typeK={s.KvCacheTypeK} typeV={s.KvCacheTypeV}");
        LlmSettingsStore.Save(s);

        var modelPath = LlmModelLocator.ResolveExistingOrTarget();
        AppLogger.Log($"[LLM] Load start backend={s.Backend} vkDev={s.VulkanDeviceIndex} ctx={s.ContextSize} gpuLayers={s.GpuLayerCount} model={Path.GetFileName(modelPath)}");

        btnLlmLoad.IsEnabled = false;  // immediate feedback, don't wait for state event
        tbLlmRuntimeState.Text = "State: Loading...";

        // Laptop NVIDIA Optimus + AMD switchable-graphics layer cold-start:
        // first vkCreateInstance / vkCreateDevice on a fresh process tends to
        // fail because those layers synchronize GPU handoff on demand. Typical
        // warm-up window is 1-2 seconds, so we need generous retry spacing.
        const int maxAttempts = 3;
        const int retryDelayMs = 1500;
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                tbLlmRuntimeState.Text = $"State: Retry {attempt}/{maxAttempts} — warming GPU drivers...";
                AppLogger.Log($"[LLM] Retry {attempt}/{maxAttempts} after {retryDelayMs}ms");
                await Task.Delay(retryDelayMs);
            }

            try
            {
                await LlmService.LoadAsync(s, modelPath);
                _hasLoadedOnceInProcess = true;
                AppLogger.Log($"[LLM] Load complete (attempt {attempt})");
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                AppLogger.LogError($"[LLM] Load failed (attempt {attempt}/{maxAttempts})", ex);
            }
        }

        tbLlmRuntimeState.Text = $"State: Load failed after {maxAttempts} attempts — {lastEx?.Message}";
    }

    private async void OnLlmUnloadClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Log("[LLM] Unload start");
        btnLlmUnload.IsEnabled = false;
        await DisposeSessionAsync();
        await LlmService.UnloadAsync();
        AppLogger.Log("[LLM] Unload complete");
    }

    private void OnLlmServiceStateChanged(LlmServiceState s)
    {
        Dispatcher.BeginInvoke(new Action(() => ApplyRuntimeState(s)));
    }

    private void ApplyRuntimeState(LlmServiceState s)
    {
        tbLlmRuntimeState.Text = $"State: {s}";
        var loaded = s == LlmServiceState.Loaded;
        btnLlmLoad.IsEnabled = s == LlmServiceState.Unloaded;
        btnLlmUnload.IsEnabled = loaded;
        btnLlmSaveOptions.IsEnabled = s is LlmServiceState.Unloaded or LlmServiceState.Loaded;
        btnLlmNewSession.IsEnabled = loaded;
        btnLlmChatSend.IsEnabled = loaded && _llmChatCts is null;
        tbLlmChatInput.IsEnabled = loaded;
        cbLlmBackend.IsEnabled = s == LlmServiceState.Unloaded;

        // Session creation is intentionally NOT auto-triggered here.
        // On Vulkan the session's first KV-cache allocation can push the driver
        // into unstable states when executed on the UI thread right after a
        // weights load. Users now click "New Session" or just start chatting —
        // session is created lazily from OnLlmChatSendClick on the async path.
        if (!loaded)
            _ = DisposeSessionAsync();
    }

    // ── Chat Test ────────────────────────────────────────────────────────

    private async Task<bool> EnsureSessionAsync()
    {
        if (_llmSession is not null) return true;
        if (LlmService.State != LlmServiceState.Loaded) return false;

        AppLogger.Log("[LLM] Opening chat session (creating LLamaContext)");
        try
        {
            // Session creation (KV-cache alloc) can be expensive on GPU — run
            // off the UI thread so Vulkan allocations don't run inline with
            // dispatcher work.
            _llmSession = await Task.Run(LlmService.OpenSession);
            tbLlmChatHistory.Text = "";
            tbLlmTurnCount.Text = "turns: 0";
            AppLogger.Log("[LLM] Chat session opened");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[LLM] Session open failed", ex);
            tbLlmRuntimeState.Text = $"State: Session failed — {ex.Message}";
            return false;
        }
    }

    private async void OnLlmNewSessionClick(object sender, RoutedEventArgs e)
    {
        await DisposeSessionAsync();
        await EnsureSessionAsync();
    }

    private async Task DisposeSessionAsync()
    {
        var s = _llmSession;
        _llmSession = null;
        if (s is not null)
        {
            try { await s.DisposeAsync(); } catch { }
        }
    }

    private void OnLlmChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            OnLlmChatSendClick(sender, new RoutedEventArgs());
        }
    }

    private async void OnLlmChatSendClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(tbLlmChatInput.Text)) return;
        if (!await EnsureSessionAsync()) return;
        var session = _llmSession!;

        var userMsg = tbLlmChatInput.Text.Trim();
        tbLlmChatInput.Text = "";
        tbLlmChatInput.IsEnabled = false;
        btnLlmChatSend.IsEnabled = false;

        tbLlmChatHistory.AppendText((tbLlmChatHistory.Text.Length == 0 ? "" : "\n") + $"▸ You: {userMsg}\n◂ Gemma: ");
        svLlmChat.ScrollToEnd();

        _llmChatCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var tokenCount = 0;

        try
        {
            await foreach (var tok in session.SendStreamAsync(userMsg, _llmChatCts.Token))
            {
                tokenCount++;
                tbLlmChatHistory.AppendText(tok);
                svLlmChat.ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            tbLlmChatHistory.AppendText($"\n[error: {ex.Message}]");
        }
        finally
        {
            sw.Stop();
            var secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            _llmLastTps = tokenCount / secs;
            tbLlmTurnCount.Text = $"turns: {session.TurnCount}";
            tbLlmTps.Text = $"{_llmLastTps:0.0} t/s";
            _llmChatCts?.Dispose();
            _llmChatCts = null;
            tbLlmChatInput.IsEnabled = LlmService.State == LlmServiceState.Loaded;
            btnLlmChatSend.IsEnabled = LlmService.State == LlmServiceState.Loaded;
            tbLlmChatInput.Focus();
        }
    }

    // ── Monitor ──────────────────────────────────────────────────────────

    private void RefreshMonitor()
    {
        try
        {
            var snap = LlmSystemMonitor.Snapshot();
            tbLlmRam.Text = $"{snap.ProcessWorkingSetMb} MB";
            tbLlmGpu.Text = snap.GpuUsedMb.HasValue && snap.GpuTotalMb.HasValue
                ? $"{snap.GpuUsedMb} / {snap.GpuTotalMb} MB"
                : "N/A";
        }
        catch { /* transient — ignore */ }
    }

    private static readonly System.Windows.Media.SolidColorBrush Brushes_Cyan =
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00FFF0")!);

    private static readonly System.Windows.Media.SolidColorBrush Brushes_Magenta =
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF2D95")!);

    private static class Brushes
    {
        public static System.Windows.Media.SolidColorBrush Cyan => Brushes_Cyan;
        public static System.Windows.Media.SolidColorBrush Magenta => Brushes_Magenta;
    }
}
