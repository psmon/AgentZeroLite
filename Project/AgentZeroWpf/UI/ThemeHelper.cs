using System;
using System.Windows;
using System.Windows.Interop;

namespace AgentZeroWpf.UI;

/// <summary>
/// DWM 다크 타이틀바를 모든 윈도우에 적용하는 헬퍼.
/// </summary>
internal static class ThemeHelper
{
    /// <summary>
    /// DWM immersive dark mode + caption/border color를 #1E1E1E로 설정.
    /// WindowStyle="None"인 경우에도 프레임 잔상 제거 효과가 있다.
    /// </summary>
    public static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref darkMode, sizeof(int));

        int captionColor = 0x001E1E1E; // COLORREF: 0x00BBGGRR
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_CAPTION_COLOR,
            ref captionColor, sizeof(int));

        int borderColor = 0x001E1E1E;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR,
            ref borderColor, sizeof(int));
    }

    /// <summary>
    /// Window의 Loaded 이벤트에서 호출. HWND를 자동으로 가져와 다크 타이틀바 적용.
    /// </summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            ApplyDarkTitleBar(hwnd);
    }
}
