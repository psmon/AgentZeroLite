using System;
using System.Runtime.InteropServices;

namespace AgentZeroWpf.Services;

/// <summary>
/// Flashes the taskbar button to draw attention when an agent needs the user
/// (herdr-adoption H2 — "never hunt for the stuck one"). No-op if the window is
/// already in the foreground.
/// </summary>
public static class TaskbarFlasher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x00000002;   // flash taskbar button
    private const uint FLASHW_TIMERNOFG = 0x0000000C; // flash until foreground

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Flashes the taskbar for <paramref name="hwnd"/> unless it is already foreground.</summary>
    public static void Flash(IntPtr hwnd, uint count = 3)
    {
        try
        {
            if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = count,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fi);
        }
        catch { /* attention flash is best-effort */ }
    }
}
