using System.Security.Cryptography;

namespace Agent.Common.Voice.KittenTts2;

/// <summary>
/// Installs a KittenTTS 2 bundle into <see cref="KittenTts2ModelStore.DefaultDirectory"/> from a folder that holds
/// one (a copy someone handed over, a synced cloud folder). Every file the source's manifest names is copied to a
/// <c>.part</c> file while its SHA-256 is computed, and only a matching hash is renamed into place, so a truncated
/// or altered file is never mistaken for an installed model. The manifest goes in last.
/// </summary>
public static class KittenTts2ModelInstaller
{
    public static async Task<bool> InstallFromFolderAsync(string source, IProgress<ModelDownloadStatus> progress,
        CancellationToken ct)
    {
        var manifest = KittenTts2ModelStore.ReadManifest(source);
        if (manifest is null)
            return Fail(progress, $"no {KittenTts2ModelStore.ManifestFile} in {source}");
        var target = KittenTts2ModelStore.DefaultDirectory;
        Directory.CreateDirectory(target);
        var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        long done = 0;
        foreach (var file in manifest.Files)
        {
            var src = Path.Combine(source, file.Path);
            var dst = Path.GetFullPath(Path.Combine(target, file.Path));
            if (!dst.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                return Fail(progress, $"manifest path leaves the model folder: {file.Path}");
            if (!File.Exists(src)) return Fail(progress, $"missing in the source: {file.Path}");

            // Already installed with the same size: trust it only if the hash agrees too.
            if (File.Exists(dst) && new FileInfo(dst).Length == file.Size && await HashAsync(dst, ct) == file.Sha256)
            {
                done += file.Size;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var part = dst + ".part";
            try
            {
                var hash = await CopyHashingAsync(src, part, file, manifest.TotalBytes, done, progress, ct);
                if (hash != file.Sha256)
                {
                    File.Delete(part);
                    return Fail(progress, $"{file.Path}: SHA-256 {hash} does not match the manifest");
                }
                File.Move(part, dst, overwrite: true);
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }
            done += file.Size;
        }
        File.Copy(Path.Combine(source, KittenTts2ModelStore.ManifestFile),
            Path.Combine(target, KittenTts2ModelStore.ManifestFile), overwrite: true);
        progress.Report(new ModelDownloadStatus("✓ KittenTTS 2 installed",
            $"{manifest.Files.Count} files, {manifest.TotalBytes / 1e9:0.00} GB at {target}", 100, true, true));
        return true;
    }

    private static async Task<string> CopyHashingAsync(string src, string dst, KittenTts2File file, long total, long before,
        IProgress<ModelDownloadStatus> progress, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long copied = 0;
        var last = DateTime.UtcNow;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, n, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            copied += n;
            if ((DateTime.UtcNow - last).TotalMilliseconds < 250) continue;
            last = DateTime.UtcNow;
            progress.Report(new ModelDownloadStatus($"Installing {file.Path}",
                $"{(before + copied) / 1e6:0} / {total / 1e6:0} MB", (int)((before + copied) * 100 / Math.Max(1, total)), false, false));
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    private static bool Fail(IProgress<ModelDownloadStatus> progress, string detail)
    {
        progress.Report(new ModelDownloadStatus("KittenTTS 2 install failed", detail, null, true, false));
        return false;
    }
}
