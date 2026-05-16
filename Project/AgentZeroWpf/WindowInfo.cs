using System.Diagnostics;

namespace AgentZeroWpf;

internal sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public NativeMethods.RECT Rect { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int Style { get; init; }
    public int ExStyle { get; init; }
    public bool IsVisible { get; init; }
    public bool IsEnabled { get; init; }

    public static WindowInfo Capture(IntPtr hwnd)
    {
        var classChars = new char[256];
        int classLen = NativeMethods.GetClassName(hwnd, classChars, classChars.Length);
        string className = new string(classChars, 0, classLen);

        int titleLen = NativeMethods.GetWindowTextLength(hwnd);
        string title = string.Empty;
        if (titleLen > 0)
        {
            var titleChars = new char[titleLen + 1];
            NativeMethods.GetWindowText(hwnd, titleChars, titleChars.Length);
            title = new string(titleChars, 0, titleLen);
        }

        NativeMethods.GetWindowRect(hwnd, out var rect);

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string processName = string.Empty;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch { }

        return new WindowInfo
        {
            Handle = hwnd,
            ClassName = className,
            Title = title,
            Rect = rect,
            ProcessId = pid,
            ProcessName = processName,
            Style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE),
            ExStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE),
            IsVisible = NativeMethods.IsWindowVisible(hwnd),
            IsEnabled = NativeMethods.IsWindowEnabled(hwnd),
        };
    }

    public override string ToString()
    {
        return $"""
            Handle:   0x{Handle:X8}
            Class:    {ClassName}
            Title:    {Title}
            Rect:     ({Rect.Left},{Rect.Top})-({Rect.Right},{Rect.Bottom}) [{Rect.Width}x{Rect.Height}]
            Process:  {ProcessName} (PID: {ProcessId})
            Style:    0x{Style:X8}
            ExStyle:  0x{ExStyle:X8}
            Visible:  {IsVisible}
            Enabled:  {IsEnabled}
            """;
    }
}
