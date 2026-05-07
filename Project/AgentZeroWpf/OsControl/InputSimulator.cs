using System.Collections.Generic;
using System.Threading;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// Mouse and keyboard input simulation. Every method here is gated by
/// <see cref="OsApprovalGate"/> at the caller site — this class itself does
/// not check the gate so unit tests can exercise the mechanics. CLI/LLM
/// entry points perform the check before reaching here.
/// </summary>
internal static class InputSimulator
{
    public static void MouseMove(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);
    }

    public static void MouseClick(int x, int y, bool right = false, bool dbl = false)
    {
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(20);

        uint down = right ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_LEFTDOWN;
        uint up = right ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_LEFTUP;

        NativeMethods.mouse_event(down, 0, 0, 0, IntPtr.Zero);
        NativeMethods.mouse_event(up, 0, 0, 0, IntPtr.Zero);

        if (dbl)
        {
            Thread.Sleep(50);
            NativeMethods.mouse_event(down, 0, 0, 0, IntPtr.Zero);
            NativeMethods.mouse_event(up, 0, 0, 0, IntPtr.Zero);
        }
    }

    public static void MouseWheel(int x, int y, int delta)
    {
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(20);
        NativeMethods.mouse_event(
            NativeMethods.MOUSEEVENTF_WHEEL,
            0,
            0,
            delta,
            IntPtr.Zero);
    }

    /// <summary>
    /// Send a keystroke described as a "+"-joined modifier+key spec. Examples:
    /// <c>"a"</c>, <c>"ctrl+c"</c>, <c>"ctrl+shift+t"</c>, <c>"alt+f4"</c>,
    /// <c>"f5"</c>, <c>"escape"</c>, <c>"return"</c>, <c>"down"</c>.
    /// Returns false if the spec couldn't be parsed.
    /// </summary>
    public static bool KeyPress(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = new List<byte>();
        byte? mainKey = null;

        foreach (var raw in parts)
        {
            var p = raw.ToLowerInvariant();
            switch (p)
            {
                case "ctrl":
                case "control":
                    modifiers.Add(NativeMethods.VK_CONTROL); break;
                case "alt":
                case "menu":
                    modifiers.Add(NativeMethods.VK_MENU); break;
                case "shift":
                    modifiers.Add(NativeMethods.VK_SHIFT); break;
                case "win":
                case "lwin":
                    modifiers.Add(NativeMethods.VK_LWIN); break;
                default:
                    if (mainKey is not null) return false;   // two non-modifier keys = malformed
                    mainKey = TryMapKey(p);
                    if (mainKey is null) return false;
                    break;
            }
        }

        if (mainKey is null) return false;

        foreach (var m in modifiers)
            NativeMethods.keybd_event(m, 0, 0, IntPtr.Zero);

        NativeMethods.keybd_event(mainKey.Value, 0, 0, IntPtr.Zero);
        NativeMethods.keybd_event(mainKey.Value, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);

        for (int i = modifiers.Count - 1; i >= 0; i--)
            NativeMethods.keybd_event(modifiers[i], 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);

        return true;
    }

    private static byte? TryMapKey(string p)
    {
        if (p.Length == 1)
        {
            char c = p[0];
            if (c is >= 'a' and <= 'z') return (byte)('A' + (c - 'a'));
            if (c is >= '0' and <= '9') return (byte)c;
        }

        return p switch
        {
            "return" or "enter" => NativeMethods.VK_RETURN,
            "tab"               => NativeMethods.VK_TAB,
            "escape" or "esc"   => NativeMethods.VK_ESCAPE,
            "space"             => NativeMethods.VK_SPACE,
            "back" or "backspace" => NativeMethods.VK_BACK,
            "delete" or "del"   => NativeMethods.VK_DELETE,
            "insert" or "ins"   => NativeMethods.VK_INSERT,
            "home"              => NativeMethods.VK_HOME,
            "end"               => NativeMethods.VK_END,
            "pageup" or "pgup"  => NativeMethods.VK_PRIOR,
            "pagedown" or "pgdn" => NativeMethods.VK_NEXT,
            "left"              => NativeMethods.VK_LEFT,
            "right"             => NativeMethods.VK_RIGHT,
            "up"                => NativeMethods.VK_UP,
            "down"              => NativeMethods.VK_DOWN,
            "f1"  => NativeMethods.VK_F1,
            "f2"  => NativeMethods.VK_F2,
            "f3"  => NativeMethods.VK_F3,
            "f4"  => NativeMethods.VK_F4,
            "f5"  => NativeMethods.VK_F5,
            "f6"  => NativeMethods.VK_F6,
            "f7"  => NativeMethods.VK_F7,
            "f8"  => NativeMethods.VK_F8,
            "f9"  => NativeMethods.VK_F9,
            "f10" => NativeMethods.VK_F10,
            "f11" => NativeMethods.VK_F11,
            "f12" => NativeMethods.VK_F12,
            _ => null,
        };
    }
}
