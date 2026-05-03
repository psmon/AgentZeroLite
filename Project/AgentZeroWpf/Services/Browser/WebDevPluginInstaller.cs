using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Common;

namespace AgentZeroWpf.Services.Browser;

public sealed record InstallResult(bool Ok, string? PluginId, string? Name, string? Error);

/// <summary>
/// Installs a WebDev plugin from a .zip archive or a public Git URL into
/// <c>%LOCALAPPDATA%/AgentZeroLite/Wasm/plugins/{id}/</c>.
///
/// **ZIP** — archive must contain a top-level <c>manifest.json</c>
/// (either at the zip root or inside a single top-level folder — both
/// shapes auto-unwrap). Strict <see cref="PluginManifest"/> validation
/// before files are moved into place.
///
/// **Git URL** — accepts a GitHub tree URL pointing at a plugin folder
/// (e.g. <c>https://github.com/owner/repo/tree/branch/path/to/plugin</c>).
/// The installer fetches <c>manifest.json</c> via the raw content URL,
/// validates it, then walks the GitHub Trees API to download every file
/// in the folder. No local <c>git</c> CLI dependency.
///
/// Both paths share staging-dir isolation: a failed install never
/// partial-writes, the staging directory is removed in <c>finally</c>.
/// </summary>
public static class WebDevPluginInstaller
{
    private static readonly HttpClient Http = CreateHttp();
    private const long MaxFileBytes = 25 * 1024 * 1024; // 25 MB per file
    private const int  MaxFiles     = 200;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-PluginInstaller/1.0");
        return c;
    }

    /// <summary>
    /// Remove an installed plugin's mounted folder. Built-in samples
    /// (under <c>{exeDir}/Wasm/</c>) are out of scope here — they're
    /// part of the shipped exe and not deletable from the UI.
    /// </summary>
    public static InstallResult Uninstall(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return new InstallResult(false, null, null, "missing plugin id");

        var pluginsRoot = WebDevSampleCatalog.PluginsRoot;
        var target = Path.GetFullPath(Path.Combine(pluginsRoot, pluginId));
        var rootFull = Path.GetFullPath(pluginsRoot + Path.DirectorySeparatorChar);

        // Hard guard against ../ escapes — the id should only ever be a single
        // path segment from the catalog, but assert the resolved path stays
        // under the plugins root before any filesystem write.
        if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return new InstallResult(false, pluginId, null, "unsafe plugin id");

        if (!Directory.Exists(target))
            return new InstallResult(false, pluginId, null, "plugin not installed");

        try
        {
            Directory.Delete(target, recursive: true);
            AppLogger.Log($"[WebDev] plugin uninstalled | id={pluginId}");
            return new InstallResult(true, pluginId, null, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] plugin uninstall failed: {ex.GetType().Name}: {ex.Message}");
            return new InstallResult(false, pluginId, null, ex.Message);
        }
    }

    public static InstallResult InstallFromZip(string zipPath, bool allowOverwrite = true)
    {
        if (!File.Exists(zipPath))
            return new InstallResult(false, null, null, $"file not found: {zipPath}");

        var pluginsRoot = WebDevSampleCatalog.PluginsRoot;
        Directory.CreateDirectory(pluginsRoot);

        var stagingDir = Path.Combine(pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDir);
            ExtractSafely(zipPath, stagingDir);

            var (manifestPath, contentRoot) = LocateManifest(stagingDir)
                ?? throw new InvalidDataException(
                    "manifest.json not found at the root of the zip (or in a single top-level folder)");

            var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
            var entryFile = Path.Combine(contentRoot, manifest.Entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryFile))
                throw new InvalidDataException($"entry file '{manifest.Entry}' not found in the zip");

            var targetDir = Path.Combine(pluginsRoot, manifest.Id);
            if (Directory.Exists(targetDir))
            {
                if (!allowOverwrite)
                    throw new InvalidOperationException($"plugin '{manifest.Id}' is already installed");
                Directory.Delete(targetDir, recursive: true);
            }

            // contentRoot may be the staging dir itself or one level deeper.
            // Move the *content* — not the staging dir — into the final location.
            Directory.Move(contentRoot, targetDir);

            AppLogger.Log($"[WebDev] plugin installed | id={manifest.Id} name={manifest.Name} target={targetDir}");
            return new InstallResult(true, manifest.Id, manifest.Name, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] plugin install failed: {ex.GetType().Name}: {ex.Message}");
            return new InstallResult(false, null, null, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch { }
        }
    }

    private static void ExtractSafely(string zipPath, string destinationDir)
    {
        var fullDest = Path.GetFullPath(destinationDir + Path.DirectorySeparatorChar);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
            {
                Directory.CreateDirectory(Path.Combine(destinationDir, entry.FullName));
                continue;
            }
            var targetPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!targetPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"zip slip detected: '{entry.FullName}' resolves outside the staging dir");
            var targetDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(targetDir);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static (string ManifestPath, string ContentRoot)? LocateManifest(string stagingDir)
    {
        var rootManifest = Path.Combine(stagingDir, "manifest.json");
        if (File.Exists(rootManifest))
            return (rootManifest, stagingDir);

        var topDirs = Directory.GetDirectories(stagingDir);
        var topFiles = Directory.GetFiles(stagingDir);
        if (topDirs.Length == 1 && topFiles.Length == 0)
        {
            var nestedManifest = Path.Combine(topDirs[0], "manifest.json");
            if (File.Exists(nestedManifest))
                return (nestedManifest, topDirs[0]);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Git URL installer
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Install a plugin from a public GitHub folder URL. The URL must
    /// be either:
    ///   <list type="bullet">
    ///   <item>browse form: <c>https://github.com/{o}/{r}/tree/{b}/{path}</c></item>
    ///   <item>raw form:   <c>https://raw.githubusercontent.com/{o}/{r}/{b}/{path}/manifest.json</c></item>
    ///   </list>
    /// Validates the manifest (same contract as ZIP), enumerates the
    /// folder via the Trees API, downloads every file, then moves the
    /// staged content into the final mount.
    /// </summary>
    public static async Task<InstallResult> InstallFromGitUrlAsync(
        string url,
        bool allowOverwrite = true,
        CancellationToken ct = default)
    {
        var pluginsRoot = WebDevSampleCatalog.PluginsRoot;
        Directory.CreateDirectory(pluginsRoot);

        var stagingDir = Path.Combine(pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var coords = ParseGitHubFolder(url)
                ?? throw new InvalidOperationException(
                    "Only public GitHub folder URLs are supported: " +
                    "https://github.com/{owner}/{repo}/tree/{branch}/{path-to-plugin-folder}");

            Directory.CreateDirectory(stagingDir);

            // Step 1 — fetch manifest.json first; reject early if missing/invalid.
            var manifestRawUrl = RawUrl(coords, "manifest.json");
            var manifestText = await FetchTextAsync(manifestRawUrl, ct);
            if (manifestText is null)
                throw new InvalidDataException(
                    $"manifest.json not found at {manifestRawUrl} — verify the URL points at a plugin folder, not the repo root");
            var manifest = PluginManifest.Parse(manifestText);

            // Step 2 — enumerate every file in the folder via the Trees API.
            var files = await ListGitHubFolderAsync(coords, ct);
            if (files.Count > MaxFiles)
                throw new InvalidDataException($"plugin folder has {files.Count} files; limit is {MaxFiles}");
            if (!files.Any(f => string.Equals(f.Path, "manifest.json", StringComparison.Ordinal)))
                throw new InvalidDataException("manifest.json not present in the listed folder");

            // Step 3 — download every file into the staging dir, preserving relative layout.
            foreach (var file in files)
            {
                if (file.Size > MaxFileBytes)
                    throw new InvalidDataException($"file '{file.Path}' is {file.Size} bytes; limit is {MaxFileBytes}");
                var rawUrl = RawUrl(coords, file.Path);
                var bytes  = await FetchBytesAsync(rawUrl, ct)
                    ?? throw new InvalidDataException($"failed to download {rawUrl}");
                var dst = Path.GetFullPath(Path.Combine(stagingDir, file.Path));
                if (!dst.StartsWith(Path.GetFullPath(stagingDir + Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"unsafe path '{file.Path}' resolves outside staging dir");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                await File.WriteAllBytesAsync(dst, bytes, ct);
            }

            // Step 4 — sanity check entry file actually landed.
            var entryFile = Path.Combine(stagingDir, manifest.Entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryFile))
                throw new InvalidDataException($"entry file '{manifest.Entry}' not present after download");

            // Step 5 — move into the final mount. Same overwrite policy as the ZIP path.
            var targetDir = Path.Combine(pluginsRoot, manifest.Id);
            if (Directory.Exists(targetDir))
            {
                if (!allowOverwrite)
                    throw new InvalidOperationException($"plugin '{manifest.Id}' is already installed");
                Directory.Delete(targetDir, recursive: true);
            }
            Directory.Move(stagingDir, targetDir);
            stagingDir = targetDir; // prevent finally from deleting the live install

            AppLogger.Log($"[WebDev] git plugin installed | id={manifest.Id} files={files.Count} from={url}");
            return new InstallResult(true, manifest.Id, manifest.Name, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] git plugin install failed: {ex.GetType().Name}: {ex.Message}");
            return new InstallResult(false, null, null, ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir) && Path.GetFileName(stagingDir).StartsWith(".staging-"))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch { }
        }
    }

    private sealed record GitHubCoords(string Owner, string Repo, string Branch, string Path);
    private sealed record GitHubFile(string Path, long Size);

    // browse: https://github.com/{o}/{r}/tree/{b}/{path}
    // raw:    https://raw.githubusercontent.com/{o}/{r}/{b}/{path}/manifest.json
    private static readonly Regex BrowseRx = new(
        @"^https?://github\.com/(?<o>[^/]+)/(?<r>[^/]+)/tree/(?<b>[^/]+)/(?<p>.+?)/?$",
        RegexOptions.Compiled);
    private static readonly Regex RawRx = new(
        @"^https?://raw\.githubusercontent\.com/(?<o>[^/]+)/(?<r>[^/]+)/(?<b>[^/]+)/(?<p>.+?)/(?:manifest\.json)?$",
        RegexOptions.Compiled);

    private static GitHubCoords? ParseGitHubFolder(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        var m = BrowseRx.Match(trimmed);
        if (!m.Success) m = RawRx.Match(trimmed);
        if (!m.Success) return null;
        return new GitHubCoords(
            m.Groups["o"].Value,
            m.Groups["r"].Value,
            m.Groups["b"].Value,
            m.Groups["p"].Value.TrimEnd('/'));
    }

    private static string RawUrl(GitHubCoords c, string relativePath)
        => $"https://raw.githubusercontent.com/{c.Owner}/{c.Repo}/{c.Branch}/{c.Path}/{relativePath.Replace('\\', '/').TrimStart('/')}";

    private static async Task<List<GitHubFile>> ListGitHubFolderAsync(GitHubCoords c, CancellationToken ct)
    {
        // Resolve the branch SHA so the Trees API call is reproducible.
        var refUrl = $"https://api.github.com/repos/{c.Owner}/{c.Repo}/git/refs/heads/{c.Branch}";
        var refJson = await FetchTextAsync(refUrl, ct)
            ?? throw new InvalidDataException($"could not resolve branch '{c.Branch}' on {c.Owner}/{c.Repo}");
        using var refDoc = JsonDocument.Parse(refJson);
        var sha = refDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidDataException("branch SHA missing in refs response");

        // Recursive tree of the whole repo at that SHA.
        var treeUrl = $"https://api.github.com/repos/{c.Owner}/{c.Repo}/git/trees/{sha}?recursive=1";
        var treeJson = await FetchTextAsync(treeUrl, ct)
            ?? throw new InvalidDataException("git/trees call failed");
        using var treeDoc = JsonDocument.Parse(treeJson);
        if (treeDoc.RootElement.TryGetProperty("truncated", out var trunc) && trunc.GetBoolean())
            AppLogger.Log("[WebDev] WARN: git tree was truncated by GitHub — large repo");

        var prefix = c.Path + "/";
        var list = new List<GitHubFile>();
        foreach (var entry in treeDoc.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "blob") continue;
            var path = entry.GetProperty("path").GetString();
            if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rel = path.Substring(prefix.Length);
            var size = entry.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0L;
            list.Add(new GitHubFile(rel, size));
        }
        return list;
    }

    private static async Task<string?> FetchTextAsync(string url, CancellationToken ct)
    {
        var res = await Http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static async Task<byte[]?> FetchBytesAsync(string url, CancellationToken ct)
    {
        var res = await Http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadAsByteArrayAsync(ct);
    }
}
