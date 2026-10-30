using System.Diagnostics;
using Agent.Common.Voice.Diarization;

namespace Agent.Common.Voice.Streaming;

/// <summary>
/// Installs a <see cref="StreamingSttModel"/>: fetch its <c>.tar.bz2</c> from k2-fsa's <c>asr-models</c>
/// release and unpack it into <see cref="StreamingSttModelCatalog.ModelDirectory"/>. Same
/// <see cref="ModelDownloadStatus"/> contract as the diarization downloader, so both hosts' download dialogs
/// drive it unchanged.
///
/// <para>The archive is unpacked into a staging folder and moved into place only when the four files are
/// there, so an interrupted extract never leaves a folder <see cref="StreamingSttModelCatalog.FindFiles(StreamingSttModel)"/>
/// would take for a model.</para>
/// </summary>
public static class StreamingSttModelDownloader
{
    public static async Task<bool> DownloadAsync(
        StreamingSttModel model, IProgress<ModelDownloadStatus> progress, CancellationToken ct)
    {
        if (StreamingSttModelCatalog.IsInstalled(model))
        {
            progress.Report(new ModelDownloadStatus(
                $"{model.Label} already installed", StreamingSttModelCatalog.ModelDirectory(model),
                PercentComplete: 100, IsTerminal: true, IsSuccess: true));
            return true;
        }

        var root = StreamingSttModelCatalog.RootDirectory;
        Directory.CreateDirectory(root);
        var tar = Path.Combine(root, model.Archive + ".tar.bz2");
        var staging = Path.Combine(root, ".extract-" + model.Archive);
        var url = StreamingSttModelCatalog.DownloadUrl(model);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-StreamingStt/1.0");

        if (!await SherpaDiarizationModelDownloader.StreamToFileAsync(http, url, tar,
                $"Downloading {model.Label} (~{model.ApproxMegabytes} MB)", progress, ct).ConfigureAwait(false))
        {
            progress.Report(new ModelDownloadStatus(
                "Download failed", $"URL: {url}", PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }

        try
        {
            progress.Report(new ModelDownloadStatus(
                "Extracting", tar, PercentComplete: null, IsTerminal: false, IsSuccess: false));
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            await ExtractAsync(tar, staging, ct).ConfigureAwait(false);

            // The archive holds one top-level folder named like the asset; accept a flat archive too.
            var unpacked = Path.Combine(staging, model.Archive);
            if (!Directory.Exists(unpacked)) unpacked = staging;
            if (StreamingSttModelCatalog.FindFiles(unpacked) is null)
                throw new InvalidOperationException("The archive did not contain encoder / decoder / joiner / tokens.txt.");

            var dest = StreamingSttModelCatalog.ModelDirectory(model);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            await MoveWhenReleasedAsync(unpacked, dest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress.Report(new ModelDownloadStatus(
                "Extract failed", ex.Message, PercentComplete: null, IsTerminal: true, IsSuccess: false));
            return false;
        }
        finally
        {
            TryDelete(tar);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }

        progress.Report(new ModelDownloadStatus(
            $"✓ {model.Label} ready", StreamingSttModelCatalog.ModelDirectory(model),
            PercentComplete: 100, IsTerminal: true, IsSuccess: true));
        return true;
    }

    /// <summary>Remove one installed model (ModelDownloadDialog's "Start fresh").</summary>
    public static void Remove(StreamingSttModel model)
    {
        var dir = StreamingSttModelCatalog.ModelDirectory(model);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// bsdtar ships with Windows 10 1803+ and macOS, GNU tar with bzip2 on Linux; .NET has tar but no bzip2.
    /// stderr is drained while tar runs — reading it only after exit can deadlock a chatty tar.
    /// </summary>
    private static async Task ExtractAsync(string tarBz2, string intoDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-xjf");
        psi.ArgumentList.Add(tarBz2);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(intoDir);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start tar.");
        var err = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw; }
        if (p.ExitCode != 0) throw new InvalidOperationException($"tar exited {p.ExitCode}: {await err.ConfigureAwait(false)}");
    }

    /// <summary>
    /// Measured: moving the freshly unpacked Nemotron folder (a 627 MB encoder) failed at once with
    /// "Access to the path … is denied" while the 121 MB Zipformer moved fine — something (Defender scanning
    /// the new file, by the look of it) still held it. Retry for a while, then move file by file, which only
    /// needs each file released in turn.
    /// </summary>
    private static async Task MoveWhenReleasedAsync(string from, string to, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
