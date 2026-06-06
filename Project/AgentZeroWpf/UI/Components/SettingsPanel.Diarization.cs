using System.IO;
using System.Windows;
using System.Windows.Controls;
using Agent.Common;
using Agent.Common.Voice.Diarization;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Voice tab — Speaker Diarization section handlers (M0024 Phase 2).
///
/// Sits as a sibling to the STT / TTS sections and runs ON TOP OF whichever
/// STT provider is active. The diarizer adds Speaker A/B/C labels to the
/// Voice test transcript and (Phase 3) to the voice-note web plugin.
///
/// Why a separate partial: SettingsPanel.Voice.cs is already large
/// (~1200 lines) and diarization is a distinct concern with its own
/// lifecycle (model load, downloader, merge logic). Keeping the file
/// boundary lets `voice-curator` knowledge map 1:1 to one source file.
/// </summary>
public partial class SettingsPanel
{
    private bool _diarInitializing;
    private SherpaSpeakerDiarizer? _diarizer;

    private void InitializeDiarizationTab()
    {
        _diarInitializing = true;
        try
        {
            var s = DiarizationSettingsStore.Load();

            SelectComboTag(cbDiarProvider, s.Provider);
            tbDiarSegPath.Text = string.IsNullOrWhiteSpace(s.SegmentationModelPath)
                ? DiarizationSettingsStore.DefaultSegmentationPath
                : s.SegmentationModelPath;
            tbDiarEmbPath.Text = string.IsNullOrWhiteSpace(s.EmbeddingModelPath)
                ? DiarizationSettingsStore.DefaultEmbeddingPath
                : s.EmbeddingModelPath;

            SelectComboTag(cbDiarExpectedSpeakers, s.ExpectedSpeakerCount.ToString());

            ApplyDiarProviderUi(s.Provider);
            RefreshDiarModelStatus();

            // Dispose diarizer on panel teardown (same cycle the mic capture +
            // ONNX classifier follow — kept inline rather than centralised so
            // the file owns its full lifecycle).
            Unloaded += (_, _) => DisposeDiarRuntime();
        }
        finally
        {
            _diarInitializing = false;
        }
    }

    private void DisposeDiarRuntime()
    {
        try { _diarizer?.DisposeAsync().AsTask().Wait(500); } catch { }
        _diarizer = null;
    }

    private void ApplyDiarProviderUi(string provider)
    {
        spDiarSherpa.Visibility = provider == DiarizationProviderNames.SherpaPyannote3D
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDiarModelStatus()
    {
        var segPath = string.IsNullOrWhiteSpace(tbDiarSegPath.Text)
            ? DiarizationSettingsStore.DefaultSegmentationPath
            : tbDiarSegPath.Text.Trim();
        var embPath = string.IsNullOrWhiteSpace(tbDiarEmbPath.Text)
            ? DiarizationSettingsStore.DefaultEmbeddingPath
            : tbDiarEmbPath.Text.Trim();

        bool segOk = File.Exists(segPath);
        bool embOk = File.Exists(embPath);

        if (segOk && embOk)
        {
            var sMb = new FileInfo(segPath).Length / (1024.0 * 1024.0);
            var eMb = new FileInfo(embPath).Length / (1024.0 * 1024.0);
            tbDiarStatus.Text = $"✓ Models present (seg {sMb:F1} MB + emb {eMb:F1} MB)";
            tbDiarStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            var missing = (!segOk ? "segmentation" : "") + (!segOk && !embOk ? " + " : "") + (!embOk ? "embedding" : "");
            tbDiarStatus.Text = $"✗ Missing {missing} — click Download";
            tbDiarStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
        }
    }

    private DiarizationSettings ReadDiarFromUi()
    {
        var s = DiarizationSettingsStore.Load();
        s.Provider = ReadComboTag(cbDiarProvider, DiarizationProviderNames.Off);
        s.SegmentationModelPath = tbDiarSegPath.Text?.Trim() ?? "";
        s.EmbeddingModelPath = tbDiarEmbPath.Text?.Trim() ?? "";
        var spkTag = ReadComboTag(cbDiarExpectedSpeakers, "0");
        s.ExpectedSpeakerCount = int.TryParse(spkTag, out var n) ? Math.Max(0, n) : 0;
        return s;
    }

    // ── event handlers ────────────────────────────────────────────────

    private void OnDiarProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_diarInitializing) return;
        var prov = ReadComboTag(cbDiarProvider, DiarizationProviderNames.Off);
        ApplyDiarProviderUi(prov);
        tbDiarSaveStatus.Text = "Unsaved — click Save Diarization.";
        tbDiarSaveStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
    }

    private void OnDiarCheckModel(object sender, RoutedEventArgs e) => RefreshDiarModelStatus();

    private void OnDiarSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ReadDiarFromUi();
            DiarizationSettingsStore.Save(s);
            tbDiarSaveStatus.Text = "✓ Saved.";
            tbDiarSaveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            AppLogger.Log($"[Diar] Saved provider={s.Provider} expectedSpkrs={s.ExpectedSpeakerCount} segPath='{s.SegmentationModelPath}' embPath='{s.EmbeddingModelPath}'");

            // Existing diarizer is stale if paths/settings changed — drop so
            // the next Voice Test re-loads.
            try { _diarizer?.DisposeAsync().AsTask().Wait(500); } catch { }
            _diarizer = null;

            RefreshDiarModelStatus();
        }
        catch (Exception ex)
        {
            tbDiarSaveStatus.Text = $"✗ {ex.Message}";
            tbDiarSaveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AppLogger.LogError("[Diar] Save failed", ex);
        }
    }

    /// <summary>
    /// "Download" — opens the shared <see cref="ModelDownloadDialog"/> wired to
    /// <see cref="SherpaDiarizationModelDownloader.DownloadAsync"/>. Same UX
    /// the Music tab uses for AST AudioSet. ~50 MB total (segmentation tarball
    /// + embedding ONNX) into the convention cache directory.
    /// </summary>
    private void OnDiarDownloadModel(object sender, RoutedEventArgs e)
    {
        var dlg = new ModelDownloadDialog(
            title: "Download Speaker Diarization Models",
            description: "Streams pyannote-segmentation-3-0 (~6 MB tarball) + " +
                         "3D-Speaker ERes2Net embedding (~40 MB ONNX) from " +
                         "k2-fsa GitHub releases. One-time; cached locally.",
            cachePathHint: DiarizationSettingsStore.DefaultModelDirectory,
            download: (progress, ct) => SherpaDiarizationModelDownloader.DownloadAsync(progress, ct),
            clearCache: SherpaDiarizationModelDownloader.ClearCacheDirectory)
        {
            Owner = Window.GetWindow(this),
        };
        dlg.ShowDialog();

        // Cached session is stale once files on disk change — drop it so
        // the next Voice Test re-loads.
        try { _diarizer?.DisposeAsync().AsTask().Wait(500); } catch { }
        _diarizer = null;
        RefreshDiarModelStatus();
        AppLogger.Log($"[Diar] Download dialog closed | segPresent={File.Exists(DiarizationSettingsStore.DefaultSegmentationPath)} embPresent={File.Exists(DiarizationSettingsStore.DefaultEmbeddingPath)}");
    }

    /// <summary>
    /// Get-or-create the cached diarizer instance. Returns null when the
    /// provider is Off or models aren't present. Called by the Voice test
    /// pipeline; safe to invoke on a thread-pool thread.
    /// </summary>
    internal async Task<SherpaSpeakerDiarizer?> GetReadyDiarizerAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var s = DiarizationSettingsStore.Load();
        if (s.Provider == DiarizationProviderNames.Off) return null;

        _diarizer ??= new SherpaSpeakerDiarizer(s);
        var ok = await _diarizer.EnsureReadyAsync(progress, ct).ConfigureAwait(false);
        return ok ? _diarizer : null;
    }
}
