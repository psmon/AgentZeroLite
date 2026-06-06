using System.IO.Compression;
using Agent.Common.Voice;

namespace Agent.Common.Voice.Diarization;

/// <summary>
/// One-shot downloader for the Sherpa-ONNX speaker-diarization bundle.
/// Pulls two files from k2-fsa's GitHub releases:
///
///   1. <c>sherpa-onnx-pyannote-segmentation-3-0</c>  (~6 MB) — speaker
///       segmentation, pyannote v3 architecture.
///   2. <c>3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx</c>
///       (~40 MB) — 3D-Speaker embedding for clustering.
///
/// Designed for the shared <c>ModelDownloadDialog</c> contract — same UX
/// the AST AudioSet downloader (Music tab) uses. Segmentation arrives as
/// a tarball; we extract just the <c>model.onnx</c> we need and discard the
/// rest (license file etc. stay in cache for compliance).
/// </summary>
public static class SherpaDiarizationModelDownloader
{
    private const string SegmentationTarUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";

    private const string EmbeddingUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

    /// <summary>
    /// Fetch segmentation + embedding into <see cref="DiarizationSettingsStore.DefaultModelDirectory"/>.
    /// Returns true on full success.
    /// </summary>
    public static async Task<bool> DownloadAsync(IProgress<ModelDownloadStatus> progress, CancellationToken ct)
    {
        var dir = DiarizationSettingsStore.DefaultModelDirectory;
        Directory.CreateDirectory(dir);

        var segDst = DiarizationSettingsStore.DefaultSegmentationPath;
        var embDst = DiarizationSettingsStore.DefaultEmbeddingPath;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-Diarization/1.0");

        // ── 1) Embedding (direct .onnx) ────────────────────────────────
        bool embOk;
        if (File.Exists(embDst) && new FileInfo(embDst).Length > 1_000_000)
        {
            progress.Report(new ModelDownloadStatus(
                "Embedding model already present — skipping", embDst,
                PercentComplete: 50, IsTerminal: false, IsSuccess: true));
            embOk = true;
        }
        else
        {
            embOk = await StreamToFileAsync(http, EmbeddingUrl, embDst,
                "Downloading 3D-Speaker embedding (~40 MB)", progress, ct).ConfigureAwait(false);
        }

        if (!embOk)
        {
            progress.Report(new ModelDownloadStatus(
                "Embedding download failed", $"URL: {EmbeddingUrl}",
                PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }

        // ── 2) Segmentation (tar.bz2 → extract model.onnx) ────────────
        if (File.Exists(segDst) && new FileInfo(segDst).Length > 1_000_000)
        {
            progress.Report(new ModelDownloadStatus(
                "Segmentation model already present — skipping", segDst,
                PercentComplete: 100, IsTerminal: true, IsSuccess: true));
            return true;
        }

        var tarTmp = Path.Combine(dir, "segmentation.tar.bz2");
        var tarOk = await StreamToFileAsync(http, SegmentationTarUrl, tarTmp,
            "Downloading pyannote segmentation (~6 MB)", progress, ct).ConfigureAwait(false);
        if (!tarOk)
        {
            progress.Report(new ModelDownloadStatus(
                "Segmentation download failed", $"URL: {SegmentationTarUrl}",
                PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }

        try
        {
            progress.Report(new ModelDownloadStatus(
                "Extracting segmentation model.onnx", tarTmp,
                PercentComplete: null, IsTerminal: false, IsSuccess: false));
            ExtractModelOnnx(tarTmp, segDst);
            TryDelete(tarTmp);
        }
        catch (Exception ex)
        {
            progress.Report(new ModelDownloadStatus(
                "Segmentation extract failed", ex.Message,
                PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }

        progress.Report(new ModelDownloadStatus(
            "✓ Diarization models ready",
            $"segmentation: {new FileInfo(segDst).Length / 1024.0 / 1024.0:F1} MB · embedding: {new FileInfo(embDst).Length / 1024.0 / 1024.0:F1} MB · cache: {dir}",
            PercentComplete: 100, IsTerminal: true, IsSuccess: true));
        return true;
    }

    /// <summary>Wipe the default diarization model directory. Wired to ModelDownloadDialog's "Start fresh".</summary>
    public static void ClearCacheDirectory()
    {
        var dir = DiarizationSettingsStore.DefaultModelDirectory;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Extract the <c>model.onnx</c> entry from a .tar.bz2 segmentation
    /// bundle. The Sherpa release layout puts model.onnx + LICENSE +
    /// README inside a single top-level folder; we only need model.onnx
    /// at the convention path. Uses pure-.NET extraction (no bunzip2
    /// dependency) since the file is tiny.
    /// </summary>
    private static void ExtractModelOnnx(string tarBz2Path, string destOnnxPath)
    {
        // .NET 7+ ships System.Formats.Tar; combined with BZip2 from
        // SharpZipLib OR a built-in BZip2Stream wrapper. For simplicity
        // here we read the .tar.bz2 through a process pipeline that .NET
        // supports natively: try System.IO.Compression's BZip2 via the
        // BrotliStream isn't applicable. Fall back to invoking `tar`
        // which Windows 11 ships natively (bsdtar).
        var argv = $"-xjf \"{tarBz2Path}\" -C \"{Path.GetDirectoryName(tarBz2Path)}\"";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar",
            Arguments = argv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch tar — Windows 11 should ship bsdtar by default.");
        p.WaitForExit(TimeSpan.FromMinutes(2));
        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"tar exited {p.ExitCode}: {err}");
        }

        // Find model.onnx anywhere under the extraction directory and
        // move it to the convention path.
        var dir = Path.GetDirectoryName(tarBz2Path)!;
        var found = Directory.GetFiles(dir, "model.onnx", SearchOption.AllDirectories);
        if (found.Length == 0)
            throw new FileNotFoundException("model.onnx not found in extracted segmentation archive.");

        if (File.Exists(destOnnxPath)) File.Delete(destOnnxPath);
        File.Move(found[0], destOnnxPath);
    }

    private static async Task<bool> StreamToFileAsync(
        HttpClient http, string url, string destination, string caption,
        IProgress<ModelDownloadStatus> progress, CancellationToken ct)
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
                var buffer = new byte[1 << 16];
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
                    int? pct = total is long t && t > 0 ? (int)(copied * 100 / t) : null;
                    var detail = total is long tt
                        ? $"{copied / 1024.0 / 1024.0:F1} / {tt / 1024.0 / 1024.0:F1} MB · {mbps:F1} MB/s"
                        : $"{copied / 1024.0 / 1024.0:F1} MB · {mbps:F1} MB/s";
                    progress.Report(new ModelDownloadStatus(
                        caption, detail, pct, IsTerminal: false, IsSuccess: false));
                }
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tmp, destination);
            return true;
        }
        catch (OperationCanceledException) { TryDelete(tmp); throw; }
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
