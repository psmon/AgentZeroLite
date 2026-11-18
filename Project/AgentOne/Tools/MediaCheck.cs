using System.Diagnostics;
using System.Globalization;
using AgentOne.Tools.Cli;

namespace AgentOne.Tools;

/// <summary>
/// What a command's new picture or video actually shows — measured the moment it
/// is made, because a length and a resolution say nothing about the picture.
///
/// Measured (2026-11-16, music-harness): a compositor rendered a 223-second
/// 1920×1080 music video with its audio, and the turn measured exactly that —
/// duration and size — and called the test passed. Every frame was one flat
/// colour: no artwork, no lyric. The colour values had never been scaled, so the
/// file was a 1.2 GB recording of nothing. A person saw it in one glance; nothing
/// in the loop had looked.
///
/// So after a command, media files it wrote in the folders it named are probed
/// with the ffmpeg already on the machine (found, never installed): a few frames
/// are shrunk to a thumbnail and their spread of brightness is read. Frames that
/// are all nearly uniform are reported with the numbers — a fact for the model to
/// act on, not a verdict on taste. No ffmpeg, no check.
/// </summary>
public static class MediaCheck
{
    private static readonly string[] Video = [".mp4", ".mov", ".webm", ".mkv"];
    private static readonly string[] Image = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>A frame whose brightness varies less than this (0–255, standard deviation) shows no picture.</summary>
    internal const double FlatSpread = 6.0;

    /// <summary>
    /// A frame whose neighbouring thumbnail pixels differ by less than this on average has no edges:
    /// a smooth gradient or a vignette over one colour. Measured on the run above: the blank frames
    /// 0.4 and 0.9 (the second a grey vignette whose spread, 11, passed the first rule), the three
    /// generated pictures 8.5, 9.0 and 22.
    /// </summary>
    internal const double FlatDetail = 2.0;

    private const int ThumbW = 64, ThumbH = 36, MaxFiles = 4;

    /// <summary>Replaceable by a test: (executable name) → path or null.</summary>
    internal static Func<string, string?> Locate { get; set; } = ExecutableLocator.Find;

    /// <summary>
    /// A note about media the command just wrote, or null: files in the folders the command names
    /// (and the folders of the files it names), changed since <paramref name="since"/>.
    /// </summary>
    public static string? Note(string root, string command, DateTime since)
    {
        try
        {
            var ffmpeg = Locate("ffmpeg");
            if (ffmpeg is null) return null;

            var notes = new List<string>();
            foreach (var file in Written(root, command, since).Take(MaxFiles))
            {
                var isVideo = Video.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
                var spreads = isVideo
                    ? VideoTimes(ffmpeg, file).Select(t => (At: t, Spread: Spread(ffmpeg, file, t))).ToList()
                    : [(At: 0.0, Spread: Spread(ffmpeg, file, null))];
                spreads = spreads.Where(s => s.Spread is not null).ToList();
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                // A video ffmpeg cannot take a single frame from has no picture at all. Measured (2026-11-18):
                // a compositor's test clip came out as 5.7 s of audio with no video stream, the command
                // succeeded and this check, finding no frame to measure, said nothing.
                if (spreads.Count == 0)
                {
                    if (isVideo) notes.Add($"{relative}: no frame could be read from it — it has no video stream (audio only?) " +
                                           "or the stream is broken");
                    continue;
                }
                if (spreads.Any(s => s.Spread!.Value.Deviation >= FlatSpread && s.Spread.Value.Detail >= FlatDetail)) continue;

                var seen = string.Join(", ", spreads.Select(s =>
                    (isVideo ? $"at {s.At:0.#}s " : "") +
                    $"brightness {s.Spread!.Value.Mean:0}/255 ± {s.Spread.Value.Deviation:0.0}, edge detail {s.Spread.Value.Detail:0.0}"));
                notes.Add($"{relative}: every sampled frame is one nearly uniform colour or a smooth gradient ({seen}; a picture has an " +
                          $"edge detail of {FlatDetail:0} or more) — no picture and no text is visible in it");
            }
            if (notes.Count == 0) return null;

            return "[check] The command finished, but what it made shows nothing:\n- " + string.Join("\n- ", notes) + "\n" +
                   "A right length and resolution do not make it a result. Find why the frames are blank — images not " +
                   "loaded or not drawn, colour values on the wrong scale (0–1 written where 0–255 is expected), text " +
                   "drawn off-screen or fully transparent — fix it, render a SHORT test again and look at this check's " +
                   "answer before any full render. Do not report this file as done.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Media files changed since the command started, in the folders its words point at. Newest first.</summary>
    internal static List<string> Written(string root, string command, DateTime since)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in command.Split([' ', '\t', '\n', '"', '\'', ';', '=', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('&', '|', ',');
            if (token.Length < 2 || token.StartsWith('-') || token.Contains("://") || token.IndexOfAny(['*', '?', '<', '>', '$']) >= 0) continue;
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(token) ? token : Path.Combine(root, token)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (Directory.Exists(full)) folders.Add(full);
            else if (File.Exists(full) && Path.GetDirectoryName(full) is { } parent)
            {
                folders.Add(parent);
                // A script in <work>/mv usually writes beside itself or into <work>/out.
                var sibling = Path.Combine(Path.GetDirectoryName(parent) ?? parent, "out");
                if (Directory.Exists(sibling)) folders.Add(sibling);
            }
        }

        var cutoff = since.ToUniversalTime().AddSeconds(-1);
        return folders
            .Where(f => PathGrants.IsUnder(f, Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .SelectMany(f => { try { return Directory.EnumerateFiles(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; } })
            .Where(f => Video.Concat(Image).Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTimeUtc >= cutoff && f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .ToList();
    }

    /// <summary>Three moments spread over the video: a quarter, a half and three quarters in.</summary>
    private static List<double> VideoTimes(string ffmpeg, string file)
    {
        var duration = 0.0;
        var ffprobe = Locate("ffprobe");
        if (ffprobe is not null &&
            Run(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], out var text, out _))
            double.TryParse(System.Text.Encoding.ASCII.GetString(text).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        return duration > 1 ? [duration * 0.25, duration * 0.5, duration * 0.75] : [0.0];
    }

    /// <summary>
    /// One frame's brightness from a grey thumbnail — its mean, its standard deviation and its edge detail
    /// (the average difference between horizontal neighbours); null when the frame could not be read.
    /// </summary>
    internal static (double Mean, double Deviation, double Detail)? Spread(string ffmpeg, string file, double? at)
    {
        var args = new List<string> { "-v", "error" };
        if (at is { } seconds) { args.Add("-ss"); args.Add(seconds.ToString("0.###", CultureInfo.InvariantCulture)); }
        args.AddRange(["-i", file, "-frames:v", "1", "-vf", $"scale={ThumbW}:{ThumbH},format=gray", "-f", "rawvideo", "-"]);

        if (!Run(ffmpeg, args, out var pixels, out _) || pixels.Length < ThumbW * ThumbH) return null;

        double sum = 0, squares = 0;
        var n = ThumbW * ThumbH;
        for (var i = 0; i < n; i++) { sum += pixels[i]; squares += pixels[i] * (double)pixels[i]; }
        var mean = sum / n;

        double edges = 0;
        var pairs = 0;
        for (var i = 0; i + 1 < n; i++)
        {
            if ((i + 1) % ThumbW == 0) continue; // the last pixel of a row has no right neighbour
            edges += Math.Abs(pixels[i] - pixels[i + 1]);
            pairs++;
        }
        return (mean, Math.Sqrt(Math.Max(0, squares / n - mean * mean)), pairs == 0 ? 0 : edges / pairs);
    }

    private static bool Run(string exe, IEnumerable<string> args, out byte[] stdout, out string stderr)
    {
        stdout = []; stderr = "";
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) return false;
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync();
            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            copy.Wait(TimeSpan.FromSeconds(5));
            stdout = buffer.ToArray();
            stderr = errors.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
            return false;
        }
    }
}
