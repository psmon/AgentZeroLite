using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Agent.Common.Os;

/// <summary>
/// <see cref="IOsControl"/> on Windows: user32 for windows, input and DPI, gdi32 for the
/// capture, ShellExecute for starting programs. The mechanics are the WPF host's
/// <c>AgentZeroWpf.OsControl</c> (M0014) — same enumeration rules, same activate dance,
/// same key map — rewritten without WPF: classic <c>DllImport</c> because ZeroCommon allows
/// no unsafe code, and the PNG made by <see cref="PngEncoder"/> instead of WPF imaging.
///
/// <para>The process is made per-monitor DPI aware on first use. Without it, on a scaled
/// display every rectangle and click coordinate is virtualised and a click lands somewhere
/// other than where the screenshot showed the button.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsControl : IOsControl
{
    private static int _dpiSet;

    public WindowsOsControl()
    {
        if (Interlocked.Exchange(ref _dpiSet, 1) == 0)
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* older Windows: stay as we are */ }
        }
    }

    public bool Supported => true;
    public string Platform => "windows";

    // ------------------------------------------------------------------ windows

    public IReadOnlyList<OsWindow> ListWindows(string? titleFilter = null)
    {
        var list = new List<OsWindow>();
        var names = new Dictionary<int, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return true;
            var len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var title = new StringBuilder(len + 1);
            GetWindowText(hwnd, title, title.Capacity);
            var t = title.ToString();
            if (string.IsNullOrWhiteSpace(t)) return true;
            var window = Describe(hwnd, t, names);
            if (!string.IsNullOrEmpty(titleFilter)
                && t.IndexOf(titleFilter, StringComparison.OrdinalIgnoreCase) < 0
                && window.Process.IndexOf(titleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                return true;
            list.Add(window);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Visible to user32 but not drawn: UWP frames parked off-screen and shell overlays
    /// (measured: two "ClickToDo" windows covering both monitors). Listing them gives the
    /// model windows it can neither see in a screenshot nor click.
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static OsWindow Describe(IntPtr hwnd, string title, Dictionary<int, string>? names = null)
    {
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        GetWindowRect(hwnd, out var r);
        GetWindowThreadProcessId(hwnd, out var pid);
        string? proc = null;
        if (names is null || !names.TryGetValue((int)pid, out proc))
        {
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; }
            catch { proc = "?"; }
            if (names is not null) names[(int)pid] = proc;
        }
        return new OsWindow(hwnd.ToInt64(), title, cls.ToString(), (int)pid, proc!,
            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IsIconic(hwnd));
    }

    public bool Activate(long hwnd)
    {
        var h = new IntPtr(hwnd);
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        // A tapped ALT counts as user input, which is what unlocks SetForegroundWindow for a
        // process nobody is using — the documented foreground-lock workaround.
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        SetForegroundWindow(h);
        BringWindowToTop(h);
        if (attached) AttachThreadInput(me, fgThread, false);
        for (var i = 0; i < 10 && GetAncestor(GetForegroundWindow(), GA_ROOT) != h; i++) Thread.Sleep(30);
        return GetAncestor(GetForegroundWindow(), GA_ROOT) == h;
    }

    public OsWindow? Foreground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        var root = GetAncestor(h, GA_ROOT);
        if (root != IntPtr.Zero) h = root;
        var len = GetWindowTextLength(h);
        var title = new StringBuilder(len + 1);
        GetWindowText(h, title, title.Capacity);
        return Describe(h, title.ToString());
    }

    public bool Close(long hwnd, TimeSpan wait)
    {
        var h = new IntPtr(hwnd);
        if (h == IntPtr.Zero || !IsWindow(h)) return true;
        PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsWindow(h) || !IsWindowVisible(h)) return true;
            Thread.Sleep(50);
        }
        return !IsWindow(h) || !IsWindowVisible(h);
    }

    // ------------------------------------------------------------------ capture

    public OsCapture? Capture(long hwnd, bool grayscale, int maxWidth = 1920, int maxHeight = 1080)
    {
        int x, y, w, h;
        if (hwnd == 0)
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }
        else
        {
            var handle = new IntPtr(hwnd);
            if (!IsWindow(handle) || IsIconic(handle) || !GetWindowRect(handle, out var r)) return null;
            (x, y, w, h) = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        if (w <= 0 || h <= 0) return null;

        var bgra = Grab(x, y, w, h);
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / w, (double)maxHeight / h));
        var ow = Math.Max(1, (int)(w * scale));
        var oh = Math.Max(1, (int)(h * scale));
        var channels = grayscale ? 1 : 3;
        var outPixels = new byte[ow * oh * channels];
        for (var oy = 0; oy < oh; oy++)
        {
            var sy = Math.Min(h - 1, (int)(oy / scale));
            for (var ox = 0; ox < ow; ox++)
            {
                var sx = Math.Min(w - 1, (int)(ox / scale));
                var i = (sy * w + sx) * 4;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                var o = (oy * ow + ox) * channels;
                if (grayscale) outPixels[o] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                else { outPixels[o] = r; outPixels[o + 1] = g; outPixels[o + 2] = b; }
            }
        }
        return new OsCapture(PngEncoder.Encode(outPixels, ow, oh, grayscale), ow, oh, w, h);
    }

    /// <summary>BitBlt the screen rectangle and read it back as top-down 32-bit BGRA.</summary>
    private static byte[] Grab(int x, int y, int w, int h)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var info = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,   // negative: top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            var pixels = new byte[w * h * 4];
            GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0);
            return pixels;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ------------------------------------------------------------------ input

    public void Click(int x, int y, bool right = false, bool doubleClick = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(20);
        var down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        mouse_event(down, 0, 0, 0, IntPtr.Zero);
        mouse_event(up, 0, 0, 0, IntPtr.Zero);
        if (!doubleClick) return;
        Thread.Sleep(50);
        mouse_event(down, 0, 0, 0, IntPtr.Zero);
        mouse_event(up, 0, 0, 0, IntPtr.Zero);
    }

    public bool KeyPress(string spec)
    {
        if (!KeySpec.TryParse(spec, out var modifiers, out var key)) return false;
        foreach (var m in modifiers) keybd_event(m, 0, 0, IntPtr.Zero);
        keybd_event(key, 0, 0, IntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        for (var i = modifiers.Count - 1; i >= 0; i--) keybd_event(modifiers[i], 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        return true;
    }

    /// <summary>KEYEVENTF_UNICODE per UTF-16 unit, so Hangul and symbols type as themselves whatever the layout.</summary>
    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i] == '\n' ? '\r' : text[i];
            inputs[i * 2] = INPUT.Unicode(c, up: false);
            inputs[i * 2 + 1] = INPUT.Unicode(c, up: true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ------------------------------------------------------------------ launch

    public int Launch(string program, string? filePath = null)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(filePath)) psi.ArgumentList.Add(filePath);
        using var p = System.Diagnostics.Process.Start(psi);
        return p?.Id ?? 0;
    }

    // ------------------------------------------------------------------ Win32

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // The union's size is MOUSEINPUT's (the largest); padding keeps INPUT the size SendInput expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] private long _pad0;
        [FieldOffset(16)] private long _pad1;
        [FieldOffset(24)] private long _pad2;
        [FieldOffset(32)] private long _pad3;   // INPUT is 40 bytes on x64 (MOUSEINPUT is 32)

        public static INPUT Unicode(char c, bool up) => new()
        {
            type = 1,   // INPUT_KEYBOARD
            ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
        };
    }

    private const int SW_RESTORE = 9;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04, MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x02, KEYEVENTF_UNICODE = 0x04;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    private const int DWMWA_CLOAKED = 14;
    private const uint WM_CLOSE = 0x0010;
    private const uint GA_ROOT = 2;
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

/// <summary>
/// "ctrl+shift+t" → modifiers + one virtual key. The same vocabulary as the WPF host's
/// <c>InputSimulator</c>; pure, so tests pin it without pressing anything.
/// </summary>
public static class KeySpec
{
    public static bool TryParse(string? spec, out List<byte> modifiers, out byte key)
    {
        modifiers = new List<byte>();
        key = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        byte? main = null;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers.Add(0x11); break;
                case "alt" or "menu": modifiers.Add(0x12); break;
                case "shift": modifiers.Add(0x10); break;
                case "win" or "lwin": modifiers.Add(0x5B); break;
                default:
                    if (main is not null) return false;   // two non-modifier keys
                    main = Map(raw.ToLowerInvariant());
                    if (main is null) return false;
                    break;
            }
        }
        if (main is null) return false;
        key = main.Value;
        return true;
    }

    private static byte? Map(string p)
    {
        if (p.Length == 1)
        {
            var c = p[0];
            if (c is >= 'a' and <= 'z') return (byte)('A' + (c - 'a'));
            if (c is >= '0' and <= '9') return (byte)c;
        }
        if (p.Length is 2 or 3 && p[0] == 'f' && int.TryParse(p[1..], out var f) && f is >= 1 and <= 12)
            return (byte)(0x70 + f - 1);
        return p switch
        {
            "return" or "enter" => 0x0D,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "space" => 0x20,
            "back" or "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => null,
        };
    }
}
