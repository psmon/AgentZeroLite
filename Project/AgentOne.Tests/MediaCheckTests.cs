using System.Diagnostics;
using AgentOne.Tools;
using AgentOne.Tools.Cli;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-16, music-harness): a 223-second 1920×1080 music video was rendered
/// with its audio, measured for length and size, and called passed. Every frame was one
/// flat colour. These tests make the videos with the machine's own ffmpeg and are skipped
/// where there is none — the check itself is skipped there too.
/// </summary>
public class MediaCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-one-media-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly string? Ffmpeg = ExecutableLocator.Find("ffmpeg");

    public MediaCheckTests() => Directory.CreateDirectory(Path.Combine(_root, "out"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Renders two seconds of a lavfi source into out/&lt;name&gt;.</summary>
    private bool Make(string name, string source)
    {
        var start = new ProcessStartInfo(Ffmpeg!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", source, "-t", "2", "-pix_fmt", "yuv420p", Path.Combine(_root, "out", name) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    [Fact]
    public void A_video_of_one_flat_colour_is_reported_with_its_numbers()
    {
        if (Ffmpeg is null || !Make("mv-test.mp4", "color=c=0x101418:s=320x180:r=10")) return;

        var note = MediaCheck.Note(_root, "python mv/mograph.py test --out out/", DateTime.UtcNow.AddMinutes(-1));

        Assert.NotNull(note);
        Assert.Contains("out/mv-test.mp4", note);
        Assert.Contains("nearly uniform colour", note);
        Assert.Contains("Do not report this file as done", note);
    }

    [Fact]
    public void A_smooth_gradient_is_no_picture_either()
    {
        // Measured: a grey vignette frame had a brightness spread of 11 and passed the first rule.
        if (Ffmpeg is null || !Make("mv-test.mp4", "gradients=s=320x180:c0=0x202020:c1=0xa0a0a0:x0=0:y0=0:x1=320:y1=180:speed=0:r=10")) return;

        var note = MediaCheck.Note(_root, "python mv/mograph.py test --out out/", DateTime.UtcNow.AddMinutes(-1));

        Assert.NotNull(note);
        Assert.Contains("smooth gradient", note);
    }

    [Fact]
    public void A_video_with_a_picture_in_it_passes()
    {
        if (Ffmpeg is null || !Make("mv-test.mp4", "testsrc=s=320x180:r=10")) return;

        Assert.Null(MediaCheck.Note(_root, "python mv/mograph.py test --out out/", DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void Only_what_the_command_just_wrote_in_the_folders_it_names_is_looked_at()
    {
        if (Ffmpeg is null || !Make("old.mp4", "color=c=black:s=320x180:r=10")) return;

        // Written before the command started: not this command's.
        Assert.Null(MediaCheck.Note(_root, "python render.py --out out/", DateTime.UtcNow.AddMinutes(5)));
        // A command that names no folder holding it.
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        Assert.Null(MediaCheck.Note(_root, "python render.py --out elsewhere/", DateTime.UtcNow.AddMinutes(-1)));
        // A script in <work>/mv is taken to write into <work>/out as well.
        Directory.CreateDirectory(Path.Combine(_root, "mv"));
        File.WriteAllText(Path.Combine(_root, "mv", "mograph.py"), "");
        Assert.NotNull(MediaCheck.Note(_root, "python mv/mograph.py full", DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void No_ffmpeg_means_no_check()
    {
        var before = MediaCheck.Locate;
        try
        {
            MediaCheck.Locate = _ => null;
            File.WriteAllBytes(Path.Combine(_root, "out", "x.mp4"), [1, 2, 3]);
            Assert.Null(MediaCheck.Note(_root, "render --out out/", DateTime.UtcNow.AddMinutes(-1)));
        }
        finally { MediaCheck.Locate = before; }
    }
}
