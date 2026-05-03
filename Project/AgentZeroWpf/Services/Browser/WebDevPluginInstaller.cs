using System.IO;
using System.IO.Compression;
using Agent.Common;

namespace AgentZeroWpf.Services.Browser;

public sealed record InstallResult(bool Ok, string? PluginId, string? Name, string? Error);

/// <summary>
/// Installs a WebDev plugin from a .zip archive into
/// <c>%LOCALAPPDATA%/AgentZeroLite/Wasm/plugins/{id}/</c>.
///
/// The archive must contain a top-level <c>manifest.json</c> (either at the
/// root of the zip or inside a single top-level folder — both shapes are
/// common when zipping). Validation is strict on the manifest contract; a
/// failed install never partially writes, the staging directory is removed.
/// </summary>
public static class WebDevPluginInstaller
{
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
}
