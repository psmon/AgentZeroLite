using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AgentZeroWpf;

/// <summary>대상 컨트롤 영역을 빨간 테두리로 표시하는 투명 오버레이 윈도우.
/// Topmost로 항상 최상위에 표시한다.</summary>
internal sealed class TargetHighlightOverlay : Window
{
    public TargetHighlightOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW);
    }

    public void ShowAt(NativeMethods.RECT rect, double dpiScaleX, double dpiScaleY)
    {
        Left = rect.Left / dpiScaleX - 2;
        Top = rect.Top / dpiScaleY - 2;
        Width = rect.Width / dpiScaleX + 4;
        Height = rect.Height / dpiScaleY + 4;

        AppLogger.Log($"[Overlay] ShowAt | rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}), dpi=({dpiScaleX:F2},{dpiScaleY:F2}), wpfPos=({Left:F0},{Top:F0},{Width:F0}x{Height:F0})");
        Show();
    }

    public void HideOverlay()
    {
        AppLogger.Log("[Overlay] HideOverlay 호출");
        if (IsVisible) Hide();
    }
}
