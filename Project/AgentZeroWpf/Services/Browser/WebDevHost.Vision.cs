using System.IO;
using System.Text.RegularExpressions;
using Agent.Common;
using Agent.Common.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// WinForms is enabled project-wide, so bare `Image` / `Rectangle` collide with
// System.Drawing. Alias the ImageSharp types we use.
using ISImage = SixLabors.ImageSharp.Image;
using ISRect = SixLabors.ImageSharp.Rectangle;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// On-device vision surface for the WebDev plugin sandbox (M0028 — Agent Band
/// girl-group mode). Runs Florence-2 object detection on a cropped region of the
/// plugin's captured frame (the YouTube MV area) so the plugin can:
///   • count <c>person</c> boxes → girl-group member count,
///   • read instruments visible in the frame → vision-first instrument summon,
///   • get a cheap frame-diff motion energy → dance idle↔action sync.
///
/// The frame is captured host-side in <see cref="WebDevBridge"/> (the plugin's
/// cross-origin YouTube iframe can't be read from JS); this class only decodes,
/// crops, and runs the model. Mirrors <see cref="WebDevHost"/>'s music surface.
/// </summary>
public sealed partial class WebDevHost
{
    private Florence2VisionInterpreter? _visionInterpreter;
    private int _visionInFlight;               // 0/1 single-flight for the slow analyze
    private byte[]? _visionLastMotionGray;      // previous motion crop (downscaled gray)
    private const int MotionGrid = 32;          // NxN downscale for frame-diff

    private static readonly Regex PersonRe = new(
        @"\b(persons?|people|humans?|man|men|woman|women|girls?|boys?|lady|ladies)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>{ present, modelDir } — whether the Florence-2 model is downloaded.</summary>
    public object GetVisionStatus()
    {
        var s = VisionSettingsStore.Load();
        var dir = VisionSettingsStore.ResolveModelDir(s);
        return new { present = VisionSettingsStore.IsModelPresent(dir), modelDir = dir };
    }

    /// <summary>
    /// Object-detect a crop of <paramref name="fullPng"/> (device px rect). Returns
    /// { ok, personCount, detections:[{label,xmin,ymin,xmax,ymax}], inferMs }, or
    /// { ok:false, error } when the model is missing or a prior analyze is still
    /// running (single-flight — Florence-2 OD is ~1.4s on CPU).
    /// </summary>
    public async Task<object> VisionAnalyzeAsync(byte[] fullPng, int cx, int cy, int cw, int ch)
    {
        var s = VisionSettingsStore.Load();
        var dir = VisionSettingsStore.ResolveModelDir(s);
        if (!VisionSettingsStore.IsModelPresent(dir))
            return new { ok = false, error = "model-missing", modelDir = dir };

        if (Interlocked.CompareExchange(ref _visionInFlight, 1, 0) != 0)
            return new { ok = false, error = "busy" };

        try
        {
            _visionInterpreter ??= new Florence2VisionInterpreter(s);
            if (!await _visionInterpreter.EnsureReadyAsync(null))
                return new { ok = false, error = "model-missing", modelDir = dir };

            byte[] cropPng = CropToPng(fullPng, cx, cy, cw, ch);
            var r = await _visionInterpreter.InterpretAsync(cropPng);

            int persons = 0;
            var dets = new List<object>(r.Detections.Count);
            foreach (var d in r.Detections)
            {
                if (PersonRe.IsMatch(d.Label)) persons++;
                dets.Add(new { label = d.Label, xmin = d.XMin, ymin = d.YMin, xmax = d.XMax, ymax = d.YMax });
            }
            return new { ok = true, personCount = persons, detections = dets, inferMs = (int)r.InferenceTime.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Vision] analyze failed: {ex.GetType().Name}: {ex.Message}");
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            Interlocked.Exchange(ref _visionInFlight, 0);
        }
    }

    /// <summary>
    /// Cheap motion energy for the crop region — mean absolute pixel difference
    /// (0..1) versus the previous motion crop, on a downscaled grayscale grid.
    /// Cadence is JS-driven (~300ms); Florence-2 is not involved.
    /// </summary>
    public object VisionMotion(byte[] fullPng, int cx, int cy, int cw, int ch)
    {
        try
        {
            var gray = CropToGrayGrid(fullPng, cx, cy, cw, ch, MotionGrid);
            double motion = 0;
            var last = _visionLastMotionGray;
            if (last is not null && last.Length == gray.Length)
            {
                long sum = 0;
                for (int i = 0; i < gray.Length; i++) sum += Math.Abs(gray[i] - last[i]);
                motion = sum / (double)gray.Length / 255.0;
            }
            _visionLastMotionGray = gray;
            return new { ok = true, motion };
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Vision] motion failed: {ex.Message}");
            return new { ok = false, error = ex.Message, motion = 0.0 };
        }
    }

    /// <summary>Drop the motion baseline (on video change / test stop).</summary>
    public void ResetVision() => _visionLastMotionGray = null;

    internal void DisposeVision()
    {
        try { _visionInterpreter?.DisposeAsync().AsTask().Wait(1000); } catch { }
        _visionInterpreter = null;
        _visionLastMotionGray = null;
    }

    private static (int x, int y, int w, int h) ClampRect(int cx, int cy, int cw, int ch, int W, int H)
    {
        int x = Math.Clamp(cx, 0, Math.Max(0, W - 1));
        int y = Math.Clamp(cy, 0, Math.Max(0, H - 1));
        int w = Math.Clamp(cw, 1, W - x);
        int h = Math.Clamp(ch, 1, H - y);
        return (x, y, w, h);
    }

    private static byte[] CropToPng(byte[] png, int cx, int cy, int cw, int ch)
    {
        using var img = ISImage.Load<Rgba32>(png);
        var (x, y, w, h) = ClampRect(cx, cy, cw, ch, img.Width, img.Height);
        img.Mutate(ctx => ctx.Crop(new ISRect(x, y, w, h)));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] CropToGrayGrid(byte[] png, int cx, int cy, int cw, int ch, int grid)
    {
        using var img = ISImage.Load<Rgba32>(png);
        var (x, y, w, h) = ClampRect(cx, cy, cw, ch, img.Width, img.Height);
        img.Mutate(ctx => ctx.Crop(new ISRect(x, y, w, h)).Resize(grid, grid));

        var gray = new byte[grid * grid];
        img.ProcessPixelRows(accessor =>
        {
            int i = 0;
            for (int row = 0; row < accessor.Height; row++)
            {
                var span = accessor.GetRowSpan(row);
                for (int col = 0; col < span.Length; col++)
                {
                    var p = span[col];
                    gray[i++] = (byte)((p.R * 30 + p.G * 59 + p.B * 11) / 100);
                }
            }
        });
        return gray;
    }
}
