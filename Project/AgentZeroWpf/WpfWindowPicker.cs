using System.Windows;
using System.Windows.Input;

namespace AgentZeroWpf;

internal sealed class WpfWindowPicker : IDisposable
{
    private readonly FrameworkElement _element;
    private readonly IntPtr _ownerHandle;
    private bool _isDragging;
    private IntPtr _hoveredHwnd;

    /// <summary>마지막으로 윈도우를 선택한 화면 좌표.</summary>
    public NativeMethods.POINT? LastPickPoint { get; private set; }

    /// <summary>픽포인트 위치의 가장 깊은 자식 윈도우 핸들.</summary>
    public IntPtr LastChildHwnd { get; private set; }

    public event Action<IntPtr>? WindowHovered;
    public event Action<IntPtr>? WindowSelected;

    public WpfWindowPicker(FrameworkElement element, IntPtr ownerHandle)
    {
        _element = element;
        _ownerHandle = ownerHandle;

        _element.Cursor = Cursors.Cross;
        _element.MouseLeftButtonDown += OnMouseDown;
        _element.MouseMove += OnMouseMove;
        _element.MouseLeftButtonUp += OnMouseUp;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _element.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        AppLogger.Log("[Picker] 드래그 시작");
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        NativeMethods.GetCursorPos(out var pt);
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);

        // Walk up to root window
        if (hwnd != IntPtr.Zero)
        {
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            if (root != IntPtr.Zero)
                hwnd = root;
        }

        // Skip our own window
        if (hwnd == _ownerHandle)
            return;

        if (hwnd != _hoveredHwnd)
        {
            _hoveredHwnd = hwnd;
            WindowHovered?.Invoke(hwnd);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        Mouse.OverrideCursor = null;

        // 마우스 캡처 해제 전에 픽포인트와 자식 HWND를 캡처
        IntPtr selectedHwnd = _hoveredHwnd;
        NativeMethods.POINT pickPt = default;
        IntPtr childHwnd = IntPtr.Zero;

        if (selectedHwnd != IntPtr.Zero)
        {
            NativeMethods.GetCursorPos(out pickPt);

            // 픽포인트 위치의 가장 깊은 자식 윈도우
            childHwnd = NativeMethods.WindowFromPoint(pickPt);
            IntPtr childRoot = NativeMethods.GetAncestor(childHwnd, NativeMethods.GA_ROOT);
            if (childRoot == _ownerHandle || childHwnd == _ownerHandle)
                childHwnd = selectedHwnd;
        }

        _element.ReleaseMouseCapture();
        if (selectedHwnd != IntPtr.Zero)
        {
            LastPickPoint = pickPt;
            LastChildHwnd = childHwnd;
            AppLogger.Log($"[Picker] 윈도우 선택 완료 | root=0x{selectedHwnd:X8}, child=0x{childHwnd:X8}, pickPt=({pickPt.X},{pickPt.Y})");
            WindowSelected?.Invoke(selectedHwnd);
        }
        else
        {
            AppLogger.Log("[Picker] 드래그 종료 — 선택된 윈도우 없음");
        }

        _hoveredHwnd = IntPtr.Zero;
        e.Handled = true;
    }

    public void Dispose()
    {
        _element.MouseLeftButtonDown -= OnMouseDown;
        _element.MouseMove -= OnMouseMove;
        _element.MouseLeftButtonUp -= OnMouseUp;
    }
}
