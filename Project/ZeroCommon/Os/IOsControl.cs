namespace Agent.Common.Os;

/// <summary>One top-level window as the OS reports it. Coordinates are virtual-screen pixels.</summary>
public sealed record OsWindow(long Hwnd, string Title, string ClassName, int Pid, string Process,
    int X, int Y, int Width, int Height, bool Minimized);

/// <summary>A captured image: PNG bytes plus the size the PNG ended up (after any downscale).</summary>
public sealed record OsCapture(byte[] Png, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>
/// The desktop as an agent sees it — windows, a picture of them, the mouse, the keyboard
/// and starting a program — behind one seam, so the tool layer never names a platform.
/// <see cref="OsControl.Create"/> hands out the implementation for this OS:
/// <see cref="WindowsOsControl"/> on Windows, <see cref="UnsupportedOsControl"/> elsewhere
/// until a macOS one (Accessibility + CGEvent + screencapture) is written against the
/// same members.
///
/// <para>Everything here acts; nothing here decides. Whether an agent may click, which
/// program it may start and which file it may hand that program are the caller's rules
/// (the wearable's <c>OsToolActor</c> applies them), so a second caller with other rules
/// does not have to fork the platform code.</para>
/// </summary>
public interface IOsControl
{
    /// <summary>False when this OS has no implementation yet; every other member then fails.</summary>
    bool Supported { get; }

    /// <summary>Short name of the platform behind this instance ("windows", "unsupported").</summary>
    string Platform { get; }

    /// <summary>
    /// Visible, titled top-level windows, front to back. The filter matches the title OR the
    /// process name: a tabbed editor shows only its active tab in the title (measured:
    /// Notepad titled "테스트 메모.txt" while A.txt sat in another tab), so a title search
    /// alone reports an open window as gone.
    /// </summary>
    IReadOnlyList<OsWindow> ListWindows(string? titleFilter = null);

    /// <summary>
    /// Restore and bring a window to the foreground, and report whether it really is in front
    /// afterwards. Windows refuses foreground changes to a process the user is not using —
    /// a background host is exactly that — so "asked for it" is not "got it".
    /// </summary>
    bool Activate(long hwnd);

    /// <summary>The window that has the keyboard now — where a key press or typed text lands. Null when none.</summary>
    OsWindow? Foreground();

    /// <summary>
    /// Ask a window to close (WM_CLOSE, the same as its X button) and report whether it is
    /// gone within <paramref name="wait"/>. Needs no focus and sends no keys, so nothing lands
    /// in another window. False usually means the program is asking to save.
    /// </summary>
    bool Close(long hwnd, TimeSpan wait);

    /// <summary>
    /// A PNG of one window (hwnd &gt; 0) or of the whole virtual desktop (hwnd 0), shrunk to
    /// fit <paramref name="maxWidth"/>×<paramref name="maxHeight"/>. Null when the window is
    /// minimized, gone or zero-sized.
    /// </summary>
    OsCapture? Capture(long hwnd, bool grayscale, int maxWidth = 1920, int maxHeight = 1080);

    /// <summary>Move the pointer to a virtual-screen point and click.</summary>
    void Click(int x, int y, bool right = false, bool doubleClick = false);

    /// <summary>A key with modifiers ("ctrl+s", "alt+f4", "enter", "a"). False when the spec is not understood.</summary>
    bool KeyPress(string spec);

    /// <summary>Type literal text into whatever has the keyboard focus.</summary>
    void TypeText(string text);

    /// <summary>
    /// Start a program by name ("notepad", "calc") with an optional file to open. Returns the
    /// process id, or 0 when the shell handed the work to an already running instance.
    /// </summary>
    int Launch(string program, string? filePath = null);
}

/// <summary>The stand-in for an OS without an implementation: says so instead of pretending.</summary>
public sealed class UnsupportedOsControl : IOsControl
{
    public bool Supported => false;
    public string Platform => "unsupported";
    private static NotSupportedException No() =>
        new($"OS control is not implemented for {System.Runtime.InteropServices.RuntimeInformation.OSDescription} yet");
    public IReadOnlyList<OsWindow> ListWindows(string? titleFilter = null) => throw No();
    public bool Activate(long hwnd) => throw No();
    public OsWindow? Foreground() => throw No();
    public bool Close(long hwnd, TimeSpan wait) => throw No();
    public OsCapture? Capture(long hwnd, bool grayscale, int maxWidth = 1920, int maxHeight = 1080) => throw No();
    public void Click(int x, int y, bool right = false, bool doubleClick = false) => throw No();
    public bool KeyPress(string spec) => throw No();
    public void TypeText(string text) => throw No();
    public int Launch(string program, string? filePath = null) => throw No();
}

public static class OsControl
{
    /// <summary>The implementation for the OS this process runs on.</summary>
    public static IOsControl Create() =>
        OperatingSystem.IsWindows() ? new WindowsOsControl() : new UnsupportedOsControl();
}
