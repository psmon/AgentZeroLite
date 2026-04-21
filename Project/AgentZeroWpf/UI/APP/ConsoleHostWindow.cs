using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AgentZeroWpf.UI.APP;

/// <summary>
/// 콘솔 프로세스를 임베딩하는 전용 윈도우.
/// AllowsTransparency가 없으므로 WindowsFormsHost가 정상 동작한다.
/// </summary>
internal sealed class ConsoleHostWindow : Window
{
    private readonly List<ConsoleTab> _tabs = [];
    private int _activeTab = -1;
    private readonly StackPanel _tabBar;
    private readonly Grid _hostGrid;

    private sealed record ConsoleTab(
        string Title,
        Process Process,
        IntPtr ConsoleHwnd,
        System.Windows.Forms.Integration.WindowsFormsHost Host,
        System.Windows.Forms.Panel Panel,
        Border TabButton);

    public ConsoleHostWindow(Window owner)
    {
        Owner = owner;
        Title = "AgentZero Console";
        Width = 900;
        Height = 600;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A0A12")!);

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // tab bar
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // console

        // Title bar
        var titleBar = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D0D1A")!),
            Height = 28,
        };
        titleBar.MouseLeftButtonDown += (_, _) => DragMove();
        var titleDock = new DockPanel();

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 36, Height = 28,
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#556677")!),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        closeBtn.Click += (_, _) => Hide();
        DockPanel.SetDock(closeBtn, Dock.Right);
        titleDock.Children.Add(closeBtn);

        var titleText = new TextBlock
        {
            Text = " CONSOLE",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFF0")!),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        titleDock.Children.Add(titleText);
        titleBar.Child = titleDock;
        Grid.SetRow(titleBar, 0);
        mainGrid.Children.Add(titleBar);

        // Tab bar
        var tabBarBorder = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D0D1A")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A3E")!),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 4, 6, 4),
        };
        var tabDock = new DockPanel();

        var addBtn = new Button
        {
            Content = "+",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFF0")!),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14143A")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A5E")!),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = Cursors.Hand,
        };
        var ctx = new ContextMenu();
        ctx.Items.Add(CreateMenuItem("기본콘솔 (cmd)", "cmd.exe"));
        ctx.Items.Add(CreateMenuItem("PowerShell 5", "powershell.exe"));
        ctx.Items.Add(CreateMenuItem("PowerShell 7 (pwsh)", "pwsh.exe"));
        addBtn.ContextMenu = ctx;
        addBtn.Click += (_, _) => ctx.IsOpen = true;
        DockPanel.SetDock(addBtn, Dock.Right);
        tabDock.Children.Add(addBtn);

        _tabBar = new StackPanel { Orientation = Orientation.Horizontal };
        tabDock.Children.Add(_tabBar);
        tabBarBorder.Child = tabDock;
        Grid.SetRow(tabBarBorder, 1);
        mainGrid.Children.Add(tabBarBorder);

        // Console host area
        _hostGrid = new Grid { Background = Brushes.Black };
        Grid.SetRow(_hostGrid, 2);
        mainGrid.Children.Add(_hostGrid);

        Content = mainGrid;
        Closing += (_, e) =>
        {
            if (Owner?.IsLoaded == true)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                KillAll();
            }
        };
    }

    private MenuItem CreateMenuItem(string header, string exe)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => _ = AddTab(header.Split(' ')[0], exe);
        return mi;
    }

    /// <summary>PID로 윈도우 핸들을 찾는다 (conhost 포함)</summary>
    private static IntPtr FindWindowByPid(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint wPid);
            if (wPid == pid && NativeMethods.IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false; // stop
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public async Task AddTab(string title, string exe)
    {
        // Create WinForms host FIRST so Handle is valid before SetParent
        var panel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
        var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = panel };
        _hostGrid.Children.Add(host);
        host.UpdateLayout();
        _ = panel.Handle;

        // Windows 11: 기본 터미널이 Windows Terminal이므로 conhost.exe로 강제
        string conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        AppLogger.Log($"[CLI] 콘솔 시작: conhost.exe {exe}, panel=0x{panel.Handle:X8}");

        var psi = new ProcessStartInfo
        {
            FileName = conhost,
            Arguments = $"-- \"{exe}\"",
            UseShellExecute = false,
        };

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            AppLogger.Log($"[CLI] 콘솔 시작 실패: {exe} | {ex.Message}");
            _hostGrid.Children.Remove(host);
            return;
        }

        // Find conhost window by PID
        IntPtr hwnd = IntPtr.Zero;
        uint pid = (uint)proc.Id;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            proc.Refresh();

            // conhost: MainWindowHandle 먼저
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                hwnd = proc.MainWindowHandle;
                AppLogger.Log($"[CLI] MainWindowHandle: 0x{hwnd:X8} (PID={pid})");
                break;
            }

            // EnumWindows로 PID 기반 탐색
            hwnd = FindWindowByPid(pid);
            if (hwnd != IntPtr.Zero)
            {
                AppLogger.Log($"[CLI] EnumWindows: 0x{hwnd:X8} (PID={pid})");
                break;
            }

            // conhost의 자식 프로세스(실제 셸)의 윈도우도 탐색
            try
            {
                foreach (var child in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                {
                    if (child.MainWindowHandle != IntPtr.Zero)
                    {
                        hwnd = child.MainWindowHandle;
                        AppLogger.Log($"[CLI] 자식 프로세스: 0x{hwnd:X8} (child PID={child.Id})");
                        break;
                    }
                }
                if (hwnd != IntPtr.Zero) break;
            }
            catch { }
        }

        if (hwnd == IntPtr.Zero)
        {
            AppLogger.Log($"[CLI] 콘솔 핸들 취득 실패: {exe} (PID={pid})");
            try { proc.Kill(); } catch { }
            _hostGrid.Children.Remove(host);
            return;
        }

        // Hide → restyle → reparent → show
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);

        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        AppLogger.Log($"[CLI] 원본 스타일: 0x{style:X8}");
        style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);

        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, 0);

        var result = NativeMethods.SetParent(hwnd, panel.Handle);
        AppLogger.Log($"[CLI] SetParent(0x{hwnd:X8}, 0x{panel.Handle:X8}) → 0x{result:X8}");

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        int w = Math.Max(panel.ClientSize.Width, 200);
        int h = Math.Max(panel.ClientSize.Height, 200);
        NativeMethods.MoveWindow(hwnd, 0, 0, w, h, true);

        panel.SizeChanged += (_, _) =>
            NativeMethods.MoveWindow(hwnd, 0, 0, panel.ClientSize.Width, panel.ClientSize.Height, true);

        int tabIndex = _tabs.Count;
        var tabBtn = MakeTabButton($"{title} {tabIndex + 1}", tabIndex);
        _tabBar.Children.Add(tabBtn);

        _tabs.Add(new ConsoleTab(title, proc, hwnd, host, panel, tabBtn));
        AppLogger.Log($"[CLI] 콘솔 추가: {title} (PID={proc.Id})");
        ActivateTab(tabIndex);
    }

    private Border MakeTabButton(string title, int index)
    {
        var closeBtn = new Button
        {
            Content = "×", FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#556677")!),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        int ci = index;
        closeBtn.Click += (_, _) => CloseTab(ci);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C8D6E5")!),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(closeBtn);

        var border = new Border
        {
            Child = sp,
            Padding = new Thickness(8, 4, 4, 4),
            Margin = new Thickness(0, 0, 2, 0),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14143A")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A5E")!),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Cursor = Cursors.Hand,
        };
        border.MouseLeftButtonDown += (_, _) => ActivateTab(ci);
        return border;
    }

    private void ActivateTab(int index)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            bool active = i == index;
            // Use ZIndex instead of Visibility to keep Handle valid
            System.Windows.Controls.Panel.SetZIndex(_tabs[i].Host, active ? 1 : 0);
            _tabs[i].Host.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            _tabs[i].TabButton.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(active ? "#00FFF0" : "#2A2A5E")!);
        }
        _activeTab = index;

        if (index >= 0 && index < _tabs.Count)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var t = _tabs[index];
                NativeMethods.MoveWindow(t.ConsoleHwnd, 0, 0,
                    t.Panel.ClientSize.Width, t.Panel.ClientSize.Height, true);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var tab = _tabs[index];
        try { tab.Process.Kill(entireProcessTree: true); } catch { }
        _hostGrid.Children.Remove(tab.Host);
        _tabBar.Children.Remove(tab.TabButton);
        _tabs.RemoveAt(index);

        // Rebuild indices
        for (int i = 0; i < _tabs.Count; i++)
        {
            int ci = i;
            _tabs[i] = _tabs[i] with { TabButton = MakeTabButton($"{_tabs[i].Title} {i + 1}", ci) };
        }
        _tabBar.Children.Clear();
        foreach (var t in _tabs) _tabBar.Children.Add(t.TabButton);

        if (_tabs.Count > 0)
            ActivateTab(Math.Min(index, _tabs.Count - 1));
        else
            _activeTab = -1;
    }

    public void KillAll()
    {
        foreach (var t in _tabs)
        {
            try { t.Process.Kill(entireProcessTree: true); } catch { }
        }
        _tabs.Clear();
    }
}
