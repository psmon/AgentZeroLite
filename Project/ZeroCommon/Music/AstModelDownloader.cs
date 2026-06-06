using Agent.Common.Voice;

namespace Agent.Common.Music;

/// <summary>
/// One-shot downloader for the AST AudioSet ONNX bundle. Pulls two files
/// from public HuggingFace + GitHub URLs straight into
/// <see cref="MusicSettingsStore.DefaultModelDirectory"/>:
///
///   1. <c>model.onnx</c>             (347 MB, onnx-community pre-export)
///   2. <c>class_labels_indices.csv</c> (~14 KB, AudioSet 527 labels — AST author's repo)
///
/// Designed for the <c>ModelDownloadDialog</c> contract:
///   - Reports periodic <see cref="ModelDownloadStatus"/> with %-complete + ETA.
///   - Streams to a <c>.part</c> file, then atomic-renames so a cancelled
///     download leaves no half-baked model behind to confuse the loader.
///   - Resume is intentionally NOT implemented yet — the model is one file at
///     347 MB, retry-from-scratch is simpler than range-request bookkeeping
///     and matches the dialog's "Start fresh" wipe semantics.
/// </summary>
public static class AstModelDownloader
{
    private const string ModelUrl =
        "https://huggingface.co/onnx-community/ast-finetuned-audioset-10-10-0.4593-ONNX/resolve/main/onnx/model.onnx";

    // YuanGongND/ast is the AST paper authors' own preprocessing repo; their
    // class_labels_indices.csv is the canonical AudioSet 527-class file with
    // the exact index ordering AST trains against.
    private const string LabelsUrl =
        "https://raw.githubusercontent.com/YuanGongND/ast/master/egs/audioset/data/class_labels_indices.csv";

    /// <summary>
    /// Fetch model.onnx + class_labels_indices.csv into the default model
    /// directory. Returns true on full success, false if either file fails
    /// (callers should re-check the model status afterwards either way —
    /// partial downloads are cleaned up).
    /// </summary>
    public static async Task<bool> DownloadAsync(
        IProgress<ModelDownloadStatus> progress,
        CancellationToken ct)
    {
        var dir = MusicSettingsStore.DefaultModelDirectory;
        Directory.CreateDirectory(dir);

        var modelDst = MusicSettingsStore.DefaultModelPath;
        var labelsDst = MusicSettingsStore.DefaultLabelsPath;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-Music/1.0");

        // ── 1) Model — the big one. Worth eager-skipping if it's already
        // sitting on disk at the expected size (>300 MB) so a retry after a
        // labels-only failure doesn't re-pull 347 MB. The dialog's "Start
        // fresh" checkbox calls ClearCacheDirectory which wipes both files,
        // so the user always has a way to force a re-download.
        bool modelOk;
        if (File.Exists(modelDst) && new FileInfo(modelDst).Length > 300_000_000)
        {
            progress.Report(new ModelDownloadStatus(
                Caption: "Model already present — skipping",
                Detail: modelDst,
                PercentComplete: 100,
                IsTerminal: false,
                IsSuccess: true));
            modelOk = true;
        }
        else
        {
            modelOk = await StreamToFileAsync(
                http, ModelUrl, modelDst,
                caption: "Downloading model.onnx",
                progress, ct).ConfigureAwait(false);
        }

        if (!modelOk)
        {
            progress.Report(new ModelDownloadStatus(
                Caption: "Model download failed",
                Detail: $"See app log; partial files cleaned. URL: {ModelUrl}",
                PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }

        // ── 2) Labels — tiny. Treat a labels failure as non-fatal: the
        // classifier still runs, just emits numeric class indices. Surface
        // that as a warning in the terminal status so the operator knows.
        var labelsOk = await StreamToFileAsync(
            http, LabelsUrl, labelsDst,
            caption: "Downloading class_labels_indices.csv",
            progress, ct).ConfigureAwait(false);

        if (!labelsOk)
        {
            progress.Report(new ModelDownloadStatus(
                Caption: "Model OK, labels failed",
                Detail: "Top-K output will use numeric indices. Retry to fetch labels.",
                PercentComplete: 100, IsTerminal: true, IsSuccess: true));
            return true;
        }

        progress.Report(new ModelDownloadStatus(
            Caption: "✓ All files downloaded",
            Detail: $"Model + labels cached to {dir}",
            PercentComplete: 100, IsTerminal: true, IsSuccess: true));
        return true;
    }

    /// <summary>
    /// Wipe the default model directory. Wired to <c>ModelDownloadDialog</c>'s
    /// "Start fresh" checkbox so a corrupted partial leftover can be forced
    /// out without the user hunting through %LOCALAPPDATA% in Explorer.
    /// </summary>
    public static void ClearCacheDirectory()
    {
        var dir = MusicSettingsStore.DefaultModelDirectory;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static async Task<bool> StreamToFileAsync(
        HttpClient http,
        string url,
        string destination,
        string caption,
        IProgress<ModelDownloadStatus> progress,
        CancellationToken ct)
    {
        var tmp = destination + ".part";

        try
        {
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                progress.Report(new ModelDownloadStatus(
                    caption,
                    $"HTTP {(int)response.StatusCode} — {response.ReasonPhrase}",
                    PercentComplete: null, IsTerminal: false, IsSuccess: false));
                TryDelete(tmp);
                return false;
            }

            long? total = response.Content.Headers.ContentLength;

            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16]; // 64 KB
                long copied = 0;
                var lastReport = DateTime.UtcNow;
                var start = DateTime.UtcNow;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    copied += n;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds < 200) continue;
                    lastReport = now;

                    var elapsed = (now - start).TotalSeconds;
                    var mbps = elapsed > 0 ? (copied / 1024.0 / 1024.0) / elapsed : 0;
                    int? pct = total is long t && t > 0 ? (int)(copied * 100 / t) : (int?)null;
                    var detail = total is long tt
                        ? $"{copied / 1024.0 / 1024.0:F1} / {tt / 1024.0 / 1024.0:F1} MB · {mbps:F1} MB/s"
                        : $"{copied / 1024.0 / 1024.0:F1} MB · {mbps:F1} MB/s";

                    progress.Report(new ModelDownloadStatus(
                        caption, detail, pct, IsTerminal: false, IsSuccess: false));
                }
            }

            // Atomic move — drop the prior file if present so re-downloads
            // overwrite cleanly instead of throwing IOException.
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(tmp, destination);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new ModelDownloadStatus(
                caption, $"Error: {ex.Message}",
                PercentComplete: null, IsTerminal: false, IsSuccess: false));
            TryDelete(tmp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
