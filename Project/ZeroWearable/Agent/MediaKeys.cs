using System.Runtime.InteropServices;

namespace ZeroWearable.Agent;

/// <summary>
/// The system-wide media keys, for <c>stop_media</c> when no player process can be found
/// (M0032 follow-up #1). A keyboard's ⏹ button is exactly this; every player that honours
/// it honours us. user32 is host-side on purpose — ZeroCommon stays Win32-free.
/// </summary>
internal static class MediaKeys
{
    private const byte VK_MEDIA_STOP = 0xB2;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    public static bool SendStop()
    {
        try
        {
            keybd_event(VK_MEDIA_STOP, 0, 0, 0);
            keybd_event(VK_MEDIA_STOP, 0, KEYEVENTF_KEYUP, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
