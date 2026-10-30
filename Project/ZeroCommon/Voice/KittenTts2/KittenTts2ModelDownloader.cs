using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agent.Common.Voice.KittenTts2;

/// <summary>
/// Downloads the KittenTTS 2 bundle from AgentZero's own GitHub Release (<see cref="ReleaseTag"/>) — the same direct-URL
/// pattern as the other models (Hugging Face for Supertonic / Whisper / KittenTTS, sherpa-onnx's GitHub Releases for
/// diarization), but hosted by AgentZero, because this converted bundle exists nowhere upstream. A release has no
/// folders, so each asset is the bundle path with '/' replaced by '.' (<see cref="AssetName"/>); a large file may be
/// split into numbered pieces (<see cref="Segments"/>), joined here.
///
/// <para><c>manifest.json</c> comes first and decides everything: each file it lists is skipped when already installed
/// with the right size and SHA-256, resumed with a Range request when a <c>.part</c> is left from an interrupted run,
/// and moved into place only when its hash matches. The manifest is written last, so a folder without it — or with a
/// file whose size disagrees — never counts as installed.</para>
/// </summary>
public static class KittenTts2ModelDownloader
{
    public const string ReleaseTag = "model-kittentts2-v1";
    public const string BaseUrl = "https://github.com/psmon/AgentZeroLite/releases/download/" + ReleaseTag + "/";

    public static string AssetName(string path) => path.Replace('/', '.');

    public static Task<bool> DownloadAsync(IProgress<ModelDownloadStatus> progress, CancellationToken ct,
        string baseUrl = BaseUrl) => DownloadAsync(progress, ct, baseUrl, null);

    /// <summary>Tests hand in the transport.</summary>
    internal static async Task<bool> DownloadAsync(IProgress<ModelDownloadStatus> progress, CancellationToken ct,
        string baseUrl, HttpMessageHandler? handler)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-Voice/1.0");

        progress.Report(new ModelDownloadStatus("Downloading KittenTTS 2", "manifest.json", null, false, false));
        string manifestJson;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            try { manifestJson = await http.GetStringAsync(baseUrl + KittenTts2ModelStore.ManifestFile, cts.Token); }
            catch (HttpRequestException ex) { return Fail(progress, $"manifest.json: {ex.Message}"); }
        }
        var manifest = JsonSerializer.Deserialize<KittenTts2Manifest>(manifestJson);
        if (manifest is null || manifest.Files.Count == 0) return Fail(progress, "manifest.json is empty");

        var target = KittenTts2ModelStore.DefaultDirectory;
        Directory.CreateDirectory(target);
        var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        long done = 0;
        foreach (var file in manifest.Files)
        {
            var dst = Path.GetFullPath(Path.Combine(target, file.Path));
            if (!dst.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                return Fail(progress, $"manifest path leaves the model folder: {file.Path}");
            if (File.Exists(dst) && new FileInfo(dst).Length == file.Size &&
                await KittenTts2ModelInstaller.HashAsync(dst, ct) == file.Sha256)
            {
                done += file.Size;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var ok = await FetchAsync(http, baseUrl, dst, file, manifest.TotalBytes, done, progress, ct);
            if (!ok) return false;
            done += file.Size;
        }
        await File.WriteAllTextAsync(Path.Combine(target, KittenTts2ModelStore.ManifestFile), manifestJson, ct);
        RemoveUnlisted(target, manifest);
        progress.Report(new ModelDownloadStatus("✓ KittenTTS 2 downloaded",
            $"{manifest.Files.Count} files, {manifest.TotalBytes / 1e9:0.00} GB at {target}", 100, true, true));
        return true;
    }

    /// <summary>
    /// The release assets a file is published as, in order: the file itself, or its <c>.001</c>, <c>.002</c>, …
    /// pieces when the manifest says it was split.
    /// </summary>
    public static IReadOnlyList<(string Asset, long Length)> Segments(KittenTts2File file)
    {
        var name = AssetName(file.Path);
        if (file.Parts <= 1 || file.PartSize <= 0) return [(name, file.Size)];
        return Enumerable.Range(0, file.Parts)
            .Select(i => ($"{name}.{i + 1:000}", Math.Min(file.PartSize, file.Size - i * file.PartSize)))
            .ToList();
    }

    private static async Task<bool> FetchAsync(HttpClient http, string baseUrl, string dst, KittenTts2File file, long total,
        long before, IProgress<ModelDownloadStatus> progress, CancellationToken ct)
    {
        var part = dst + ".part";
        using var sha = SHA256.Create();
        long have = 0;
        if (File.Exists(part))
        {
            have = new FileInfo(part).Length;
            if (have > file.Size) { File.Delete(part); have = 0; }
            else await HashInto(sha, part, ct);   // resume: the hash must cover the bytes already on disk
        }

        var started = DateTime.UtcNow;
        var last = DateTime.MinValue;
        long fetched = 0;
        long segmentStart = 0;
        foreach (var (asset, length) in Segments(file))
        {
            var segmentEnd = segmentStart + length;
            if (have >= segmentEnd) { segmentStart = segmentEnd; continue; }   // already on disk
            var offset = have - segmentStart;                                  // resume inside this segment
            var url = baseUrl + Uri.EscapeDataString(asset);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return Fail(progress, $"{file.Path}: HTTP {(int)response.StatusCode} {response.ReasonPhrase} ({url})");
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // The server ignored the Range: drop this segment's partial bytes and take it whole.
                await using (var trim = new FileStream(part, FileMode.Open, FileAccess.Write)) trim.SetLength(segmentStart);
                sha.Initialize();
                await HashInto(sha, part, ct);
                have = segmentStart;
            }
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    have += n;
                    fetched += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds < 250) continue;
                    last = DateTime.UtcNow;
                    var mbps = fetched / 1e6 / Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                    var now = before + have;
                    progress.Report(new ModelDownloadStatus($"Downloading {file.Path}",
                        $"{now / 1e6:0} / {total / 1e6:0} MB · {mbps:0.0} MB/s", (int)(now * 100 / Math.Max(1, total)), false, false));
                }
            }
            if (have != segmentEnd)
                return Fail(progress, $"{file.Path}: {asset} ended at {have - segmentStart} of {length} bytes — run Download again to resume");
            segmentStart = segmentEnd;
        }
        sha.TransformFinalBlock([], 0, 0);
        var hash = Convert.ToHexStringLower(sha.Hash!);
        if (hash != file.Sha256)
        {
            File.Delete(part);
            return Fail(progress, $"{file.Path}: SHA-256 {hash} does not match the manifest — removed, run Download again");
        }
        File.Move(part, dst, overwrite: true);
        return true;
    }

    /// <summary>
    /// Files a newer manifest no longer lists (the fp16 decoder that fp32 replaced) — removed so an update does not
    /// leave hundreds of MB behind. Only inside the bundle folder; partial downloads are left for a resume.
    /// </summary>
    internal static void RemoveUnlisted(string target, KittenTts2Manifest manifest)
    {
        var keep = new HashSet<string>(manifest.Files.Select(f => Path.GetFullPath(Path.Combine(target, f.Path))),
            StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(Path.Combine(target, KittenTts2ModelStore.ManifestFile)) };
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            if (keep.Contains(Path.GetFullPath(file)) || file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task HashInto(SHA256 sha, string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0) sha.TransformBlock(buffer, 0, n, null, 0);
    }

    private static bool Fail(IProgress<ModelDownloadStatus> progress, string detail)
    {
        progress.Report(new ModelDownloadStatus("KittenTTS 2 download failed", detail, null, true, false));
        return false;
    }
}
