using Agent.Common.Voice;
using Florence2;

namespace Agent.Common.Vision;

/// <summary>
/// Adapts the <c>Florence2</c> wrapper's own downloader to the shared
/// <see cref="ModelDownloadStatus"/> contract so the vision model reuses the same
/// <c>ModelDownloadDialog</c> UX as Supertonic / AST / Sherpa. Pip-free: the
/// wrapper pulls the Florence-2-base ONNX graphs straight from HuggingFace into
/// <see cref="VisionSettingsStore.DefaultModelDirectory"/>. A <c>.florence2-ready</c>
/// sentinel is written only on full success (see <see cref="VisionSettingsStore.IsModelPresent"/>).
/// </summary>
public static class VisionModelDownloader
{
    public static async Task<bool> DownloadAsync(
        IProgress<ModelDownloadStatus> progress,
        CancellationToken ct)
    {
        var dir = VisionSettingsStore.DefaultModelDirectory;
        Directory.CreateDirectory(dir);

        try
        {
            var source = new FlorenceModelDownloader(dir);
            await source.DownloadModelsAsync(
                st =>
                {
                    if (!string.IsNullOrEmpty(st.Error))
                    {
                        progress.Report(new ModelDownloadStatus(
                            "Downloading Florence-2", st.Error,
                            PercentComplete: null, IsTerminal: false, IsSuccess: false));
                        return;
                    }

                    // IStatus.Progress units aren't documented — treat <=1 as a
                    // fraction, otherwise as an already-scaled percent.
                    int? pct = st.Progress > 0f
                        ? (int)Math.Clamp(st.Progress <= 1f ? st.Progress * 100f : st.Progress, 0f, 100f)
                        : (int?)null;

                    progress.Report(new ModelDownloadStatus(
                        "Downloading Florence-2",
                        string.IsNullOrEmpty(st.Message) ? "…" : st.Message,
                        pct, IsTerminal: false, IsSuccess: false));
                },
                null,
                ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                VisionSettingsStore.ReadyMarkerPath(dir),
                DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);

            progress.Report(new ModelDownloadStatus(
                "✓ Florence-2 downloaded", $"Cached to {dir}",
                PercentComplete: 100, IsTerminal: true, IsSuccess: true));
            return true;
        }
        catch (OperationCanceledException)
        {
            progress.Report(new ModelDownloadStatus(
                "Download cancelled", "Partial files kept — click Download to resume.",
                PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }
        catch (Exception ex)
        {
            progress.Report(new ModelDownloadStatus(
                "Download failed", ex.Message,
                PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }
    }

    /// <summary>Wipe the cache dir (incl. the ready marker) — wired to "Start fresh".</summary>
    public static void ClearCacheDirectory()
    {
        var dir = VisionSettingsStore.DefaultModelDirectory;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
