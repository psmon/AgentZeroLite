namespace AgentZeroWpf.OsControl;

/// <summary>
/// DPI introspection. Returns per-monitor and system DPI plus a derived
/// scale factor (1.0 = 96 DPI). Callers in CLI/LLM use this to convert
/// virtual-screen coordinates to physical pixels when needed.
/// </summary>
internal static class DpiHelper
{
    public sealed record DpiInfo(uint SystemDpi, uint MonitorDpiX, uint MonitorDpiY, double ScaleFactor);

    public static DpiInfo Query()
    {
        uint systemDpi = 96;
        uint monX = 96, monY = 96;
        try
        {
            systemDpi = NativeMethods.GetDpiForSystem();
        }
        catch
        {
            // Pre-1607 Windows fallback — leave at 96 (System.Drawing
            // GetDeviceCaps requires a screen DC; over-engineering).
        }

        try
        {
            NativeMethods.GetCursorPos(out var pt);
            IntPtr mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            NativeMethods.GetDpiForMonitor(mon, NativeMethods.MDT_EFFECTIVE_DPI, out monX, out monY);
        }
        catch
        {
            monX = monY = systemDpi;
        }

        double scale = systemDpi / 96.0;
        return new DpiInfo(systemDpi, monX, monY, scale);
    }
}
