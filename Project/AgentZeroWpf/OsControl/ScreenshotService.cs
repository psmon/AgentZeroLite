using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// PNG-only screen capture using GDI BitBlt + WPF imaging (already implicit
/// via UseWPF=true; no extra NuGet). Two scopes:
///   • Full virtual-desktop capture (spans all monitors, supports negative
///     origins for left-of-primary monitors).
///   • Per-window capture (clipped to the window's bounding rect).
///
/// Outputs land under <c>tmp/os-cli/screenshots/{yyyy-MM-dd}/{HH-mm-ss-fff}-{label}.png</c>
/// so a busy session doesn't collide on filenames and operators can grep by
/// day.
///
/// Mission M0014 explicit choice: PNG path is returned to callers, not the
/// raw bytes, so the LLM never receives image data on the context window —
/// it gets a path it can hand to the user.
/// </summary>
internal static class ScreenshotService
{
    public sealed record CaptureResult(string Path, int Width, int Height, bool Grayscale);

    /// <summary>
    /// Capture the entire virtual desktop. <paramref name="grayscale"/>
    /// defaults to true (matches AgentWin default — smaller files, fine
    /// for diagnostic captures and LLM consumption).
    /// </summary>
    public static CaptureResult CaptureDesktop(string label = "desktop", bool grayscale = true, bool downscale1080 = true)
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        return CaptureRegion(x, y, w, h, label, grayscale, downscale1080);
    }

    /// <summary>
    /// Capture the area covered by one window's bounding rect. If the window
    /// is minimized, returns null — there's nothing on screen to capture.
    /// </summary>
    public static CaptureResult? CaptureWindow(IntPtr hwnd, string label = "window", bool grayscale = true, bool downscale1080 = false)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (NativeMethods.IsIconic(hwnd)) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        return CaptureRegion(rect.Left, rect.Top, w, h, label, grayscale, downscale1080);
    }

    public static CaptureResult CaptureRegion(int x, int y, int w, int h, string label, bool grayscale, bool downscale1080)
    {
        if (w <= 0 || h <= 0)
            throw new ArgumentException($"invalid region {w}x{h}");

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr bmp = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
        IntPtr oldBmp = NativeMethods.SelectObject(memDc, bmp);

        try
        {
            NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, NativeMethods.SRCCOPY);

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bmp,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            BitmapSource encoded = source;

            if (downscale1080 && (encoded.PixelWidth > 1920 || encoded.PixelHeight > 1080))
            {
                double scale = Math.Min(
                    1920.0 / encoded.PixelWidth,
                    1080.0 / encoded.PixelHeight);
                encoded = new TransformedBitmap(encoded, new ScaleTransform(scale, scale));
            }

            if (grayscale)
            {
                encoded = new FormatConvertedBitmap(encoded, PixelFormats.Gray8, null, 0);
            }

            string outPath = AllocateOutputPath(label);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(encoded));
            using (var fs = File.Create(outPath))
                encoder.Save(fs);

            return new CaptureResult(outPath, encoded.PixelWidth, encoded.PixelHeight, grayscale);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBmp);
            NativeMethods.DeleteObject(bmp);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static string AllocateOutputPath(string label)
    {
        var ts = DateTimeOffset.Now;
        var dir = OsControlPaths.ResolveAndEnsureDirectory("screenshots", ts.ToString("yyyy-MM-dd"));
        var safe = SanitizeLabel(label);
        var name = $"{ts:HH-mm-ss-fff}-{safe}.png";
        return Path.Combine(dir, name);
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return "shot";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(label.Length);
        foreach (var c in label)
            sb.Append(invalid.Contains(c) || c == ' ' ? '-' : c);
        return sb.ToString();
    }
}
