namespace AgentZeroWpf;

internal sealed class WindowHighlighter
{
    private const int BorderWidth = 3;
    private IntPtr _lastHighlighted;

    public void Highlight(IntPtr hwnd)
    {
        if (hwnd == _lastHighlighted)
            return;

        RemoveHighlight();

        if (hwnd == IntPtr.Zero)
            return;

        _lastHighlighted = hwnd;
        DrawXorBorder(hwnd);
    }

    public void RemoveHighlight()
    {
        if (_lastHighlighted != IntPtr.Zero)
        {
            DrawXorBorder(_lastHighlighted);
            NativeMethods.InvalidateRect(_lastHighlighted, IntPtr.Zero, true);
            _lastHighlighted = IntPtr.Zero;
        }
    }

    private static void DrawXorBorder(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return;

        int width = rect.Width;
        int height = rect.Height;

        IntPtr hdc = NativeMethods.GetWindowDC(hwnd);
        if (hdc == IntPtr.Zero)
            return;

        try
        {
            IntPtr pen = NativeMethods.CreatePen(NativeMethods.PS_INSIDEFRAME, BorderWidth, 0x0000FF);
            IntPtr oldPen = NativeMethods.SelectObject(hdc, pen);
            IntPtr oldBrush = NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(NativeMethods.NULL_BRUSH));

            int oldRop = NativeMethods.SetROP2(hdc, NativeMethods.R2_NOT);

            NativeMethods.Rectangle(hdc, 0, 0, width, height);

            NativeMethods.SetROP2(hdc, oldRop);
            NativeMethods.SelectObject(hdc, oldBrush);
            NativeMethods.SelectObject(hdc, oldPen);
            NativeMethods.DeleteObject(pen);
        }
        finally
        {
            NativeMethods.ReleaseDC(hwnd, hdc);
        }
    }
}
