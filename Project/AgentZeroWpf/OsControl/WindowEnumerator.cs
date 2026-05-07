using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// Snapshot of one top-level window. Compact JSON-friendly shape — both CLI
/// stdout and LLM tool results consume this directly.
/// </summary>
internal sealed record WindowInfo(
    [property: JsonPropertyName("hwnd")] long Handle,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("class")] string ClassName,
    [property: JsonPropertyName("pid")] int ProcessId,
    [property: JsonPropertyName("process")] string ProcessName,
    [property: JsonPropertyName("rect")] WindowRect Rect,
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("minimized")] bool Minimized);

internal sealed record WindowRect(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("w")] int Width,
    [property: JsonPropertyName("h")] int Height);

/// <summary>
/// Enumerate visible top-level windows on the current desktop. Skips
/// invisible / zero-size / message-only windows by default — only what a
/// human user could actually point at. Origin survey shows AgentWin
/// returned the same filter; matching its behaviour keeps the LLM-callable
/// catalog comparable across both projects.
/// </summary>
internal static class WindowEnumerator
{
    public static IReadOnlyList<WindowInfo> EnumerateTopLevel(string? titleFilter = null, bool includeHidden = false)
    {
        var results = new List<WindowInfo>();

        bool Callback(IntPtr hwnd, IntPtr _)
        {
            try
            {
                bool visible = NativeMethods.IsWindowVisible(hwnd);
                if (!visible && !includeHidden) return true;

                int titleLen = NativeMethods.GetWindowTextLength(hwnd);
                string title = "";
                if (titleLen > 0)
                {
                    var buf = new char[titleLen + 1];
                    int n = NativeMethods.GetWindowText(hwnd, buf, buf.Length);
                    title = new string(buf, 0, n);
                }

                var classBuf = new char[256];
                int classLen = NativeMethods.GetClassName(hwnd, classBuf, classBuf.Length);
                var className = new string(classBuf, 0, classLen);

                NativeMethods.GetWindowRect(hwnd, out var rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                // Skip zero-size windows unless caller explicitly wants
                // hidden — they're never user-visible and pollute the list.
                if (!includeHidden && (width <= 0 || height <= 0)) return true;
                if (!includeHidden && string.IsNullOrEmpty(title)) return true;

                if (!string.IsNullOrEmpty(titleFilter)
                    && title.IndexOf(titleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                string processName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch
                {
                    // Process exited between enum and probe — leave name blank.
                }

                bool minimized = NativeMethods.IsIconic(hwnd);

                results.Add(new WindowInfo(
                    hwnd.ToInt64(),
                    title,
                    className,
                    (int)pid,
                    processName,
                    new WindowRect(rect.Left, rect.Top, width, height),
                    visible,
                    minimized));
            }
            catch
            {
                // Single-window failure shouldn't stop the enumeration.
            }
            return true;
        }

        NativeMethods.EnumWindows(Callback, IntPtr.Zero);
        return results;
    }

    /// <summary>
    /// Bring a window to the foreground. Uses the AttachThreadInput dance
    /// that AgentWin verified — SetForegroundWindow alone fails when the
    /// caller doesn't own the foreground (Windows 10+ rate-limits raw calls).
    /// Returns true on best-effort success.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        IntPtr fg = NativeMethods.GetForegroundWindow();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint myThread = NativeMethods.GetCurrentThreadId();

        bool attached = false;
        if (fgThread != 0 && fgThread != myThread)
        {
            attached = NativeMethods.AttachThreadInput(myThread, fgThread, true);
        }

        bool ok = NativeMethods.SetForegroundWindow(hwnd);

        if (attached)
            NativeMethods.AttachThreadInput(myThread, fgThread, false);

        return ok;
    }
}
