namespace Agent.Common.Voice;

/// <summary>
/// One-shot downloader for the native SuperTonic-3 ONNX bundle. Pulls the 4
/// ONNX graphs + 2 data files + 10 voice-style embeddings straight from the
/// public HuggingFace repo <c>Supertone/supertonic-3</c> into
/// <see cref="SuperTonicModelStore.DefaultModelDirectory"/> — the exact same
/// <c>resolve/main/</c> pattern <c>AstModelDownloader</c> uses for the AST model.
///
/// This replaces the old pip path (<c>TTS(auto_download=True)</c> pulling into
/// <c>~/.cache/supertonic3</c>): no Python interpreter, no <c>pip install</c>,
/// no subprocess. Reuses the <c>ModelDownloadDialog</c> contract:
///   - periodic <see cref="ModelDownloadStatus"/> with %-complete + ETA,
///   - streams to a <c>.part</c> file then atomic-renames so a cancelled
///     download never leaves a half-written model behind,
///   - "Start fresh" wipes the whole dir via <see cref="ClearCacheDirectory"/>.
/// </summary>
public static class SuperTonicModelDownloader
{
    private const string RepoBase =
        "https://huggingface.co/Supertone/supertonic-3/resolve/main";

    /// <summary>
    /// Fetch every file the native provider needs into the default model
    /// directory. Returns true on full success; partial downloads are cleaned
    /// up (the <c>.part</c> temp is deleted on any failure/cancel).
    /// </summary>
    public static async Task<bool> DownloadAsync(
        IProgress<ModelDownloadStatus> progress,
        CancellationToken ct)
    {
        var dir = SuperTonicModelStore.DefaultModelDirectory;
        Directory.CreateDirectory(dir);
        var voiceDir = Path.Combine(dir, SuperTonicModelStore.VoiceStylesSubdir);
        Directory.CreateDirectory(voiceDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // A UA header keeps HF from serving the anonymous-rate-limited path as
        // aggressively — the pip route hit HTTP 429 without one (M0020 #? note).
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-Voice/1.0");

        // Build the full work-list: onnx graphs + data files (flat in dir) and
        // the 10 voice styles (under voice_styles/). vector_estimator.onnx is
        // 257 MB — the eager-skip in StreamToFileAsync means a retry after a
        // later-file failure doesn't re-pull it.
        var jobs = new List<(string url, string dst, string caption, long skipBytes)>();
        foreach (var f in SuperTonicModelStore.OnnxFiles)
            jobs.Add(($"{RepoBase}/onnx/{f}", Path.Combine(dir, f), $"Downloading {f}", MinOnnxBytes(f)));
        foreach (var f in SuperTonicModelStore.DataFiles)
            jobs.Add(($"{RepoBase}/onnx/{f}", Path.Combine(dir, f), $"Downloading {f}", 0));
        foreach (var voice in SuperTonicModelStore.BuiltinVoices)
            jobs.Add(($"{RepoBase}/voice_styles/{voice}.json",
                      Path.Combine(voiceDir, $"{voice}.json"),
                      $"Downloading {voice}.json", 0));

        int done = 0;
        foreach (var (url, dst, caption, skipBytes) in jobs)
        {
            // Eager-skip a file already sitting on disk above its expected floor
            // so retries are cheap; "Start fresh" is the escape hatch to force
            // a re-pull of everything.
            if (skipBytes > 0 && File.Exists(dst) && new FileInfo(dst).Length >= skipBytes)
            {
                done++;
                continue;
            }

            var ok = await StreamToFileAsync(http, url, dst, caption, progress, ct).ConfigureAwait(false);
            if (!ok)
            {
                progress.Report(new ModelDownloadStatus(
                    Caption: "Download failed",
                    Detail: $"See app log; partial files cleaned. URL: {url}",
                    PercentComplete: null, IsTerminal: true, IsSuccess: false));
                return false;
            }
            done++;
        }

        progress.Report(new ModelDownloadStatus(
            Caption: "✓ All files downloaded",
            Detail: $"{done} files cached to {dir}",
            PercentComplete: 100, IsTerminal: true, IsSuccess: true));
        return true;
    }

    /// <summary>Expected minimum byte size for the big ONNX graphs (eager-skip floor).</summary>
    private static long MinOnnxBytes(string file) => file switch
    {
        "vector_estimator.onnx" => 200_000_000, // ~257 MB
        "vocoder.onnx" => 80_000_000,           // ~101 MB
        "text_encoder.onnx" => 20_000_000,      // ~36 MB
        _ => 0,                                  // duration_predictor is small — always re-check
    };

    /// <summary>
    /// Wipe the whole default model directory. Wired to the dialog's "Start
    /// fresh" checkbox so a corrupted partial can be forced out without hunting
    /// through %LOCALAPPDATA% in Explorer.
    /// </summary>
    public static void ClearCacheDirectory()
    {
        var dir = SuperTonicModelStore.DefaultModelDirectory;
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
