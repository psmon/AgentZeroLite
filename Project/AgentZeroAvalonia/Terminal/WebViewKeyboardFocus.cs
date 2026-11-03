using System.Runtime.InteropServices;
using Agent.Common;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AgentZeroAvalonia.Terminal;

/// <summary>
/// Gives the page inside a <see cref="NativeWebView"/> the keyboard. On Windows
/// <c>Control.Focus()</c> focuses the Avalonia control but not WebView2's own window, so a
/// page element focused from script (the find bar's input) received nothing: measured, the
/// find chord matched in the main window, the bar opened, and the typed query went nowhere.
/// <c>ICoreWebView2Controller::MoveFocus(PROGRAMMATIC)</c> is what moves it.
///
/// This host does not reference the WebView2 SDK, so the call goes through the vtable, as
/// <c>WebViewFrameCapture</c> does for CapturePreview. MoveFocus is slot 12: three IUnknown
/// slots, then get/put_IsVisible, get/put_Bounds, get/put_ZoomFactor, add/remove_ZoomFactorChanged,
/// SetBoundsAndZoomFactor, MoveFocus — header order in WebView2.h.
/// </summary>
internal static class WebViewKeyboardFocus
{
    private const int MoveFocusSlot = 12;
    private const int ReasonProgrammatic = 0;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MoveFocusFn(IntPtr controller, int reason);

    private static bool _logged;

    /// <summary>Focus the control and, on Windows, move WebView2's keyboard focus into the page.</summary>
    public static void Focus(NativeWebView view)
    {
        try { view.Focus(); } catch { }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle || handle.CoreWebView2Controller == IntPtr.Zero)
                return;
            var vtable = Marshal.ReadIntPtr(handle.CoreWebView2Controller);
            var fn = Marshal.GetDelegateForFunctionPointer<MoveFocusFn>(Marshal.ReadIntPtr(vtable, MoveFocusSlot * IntPtr.Size));
            var hr = fn(handle.CoreWebView2Controller, ReasonProgrammatic);
            if (hr != 0 && !_logged)
            {
                _logged = true;
                AppLogger.Log($"[Xterm] WebView2 MoveFocus hr=0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            if (_logged) return;
            _logged = true;
            AppLogger.Log($"[Xterm] WebView2 MoveFocus failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
