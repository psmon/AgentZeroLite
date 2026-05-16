using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Scrap (window spy) full-overlay page. M0019 — port from AgentZero Origin
/// (D:\Code\AI\AgentWin\Project\AgentZeroWpf\UI\APP\MainWindow.xaml.cs scrap
/// handlers, lines ~340-1480).
///
/// Self-contained: owns its own <see cref="WpfWindowPicker"/>,
/// <see cref="TextCaptureService"/>, optional <see cref="TargetHighlightOverlay"/>,
/// and the in-flight <see cref="CancellationTokenSource"/>. The host MainWindow
/// only toggles Visibility — no callbacks needed.
///
/// Hosted in MainWindow same way as <see cref="WebDevPagePanel"/> — full
/// overlay (Grid.Column 1, ColumnSpan 3, RowSpan 3) so only the ActivityBar
/// stays visible while Scrap is open. ConPTY airspace handled by
/// MainWindow.EnterOverlayMode / ExitOverlayMode.
/// </summary>
public partial class ScrapPagePanel : UserControl
{
    private WpfWindowPicker? _picker;
    private TargetHighlightOverlay? _targetOverlay;
    private readonly TextCaptureService _captureService = new();
    private CancellationTokenSource? _captureCts;

    private IntPtr _selectedHwnd;
    private IntPtr _lastChildHwnd;
    private NativeMethods.POINT? _lastPickPoint;

    private ScrapWriter? _currentScrap;
    private string? _fullTreeText;

    // Output panel cap (matches Origin's MaxDisplayChars).
    private const int MaxDisplayChars = 256 * 1024;

    public ScrapPagePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_picker is not null) return;
        // The crosshair target is the click surface; the owning HWND is the
        // top-level window so the picker can skip-self filter.
        var win = Window.GetWindow(this);
        IntPtr ownerHwnd = win is null
            ? IntPtr.Zero
            : new System.Windows.Interop.WindowInteropHelper(win).Handle;
        _picker = new WpfWindowPicker(CrosshairTarget, ownerHwnd);
        _picker.WindowHovered += OnWindowHovered;
        _picker.WindowSelected += OnWindowSelected;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _captureCts?.Cancel();
        _picker?.Dispose();
        _picker = null;
        _targetOverlay?.Close();
        _targetOverlay = null;
    }

    // =========================================================================
    //  Window selection — drag picker
    // =========================================================================

    private void OnWindowHovered(IntPtr hwnd)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnWindowHovered(hwnd));
            return;
        }
        lblCaptureStatus.Text = $"호버: 0x{hwnd:X8}";
    }

    private void OnWindowSelected(IntPtr hwnd)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnWindowSelected(hwnd));
            return;
        }
        _lastPickPoint = _picker?.LastPickPoint;
        _lastChildHwnd = _picker?.LastChildHwnd ?? IntPtr.Zero;
        AppLogger.Log($"[Scrap] OnWindowSelected | root=0x{hwnd:X8}, child=0x{_lastChildHwnd:X8}");
        SelectWindow(hwnd);
    }

    // =========================================================================
    //  HWND input — direct entry
    // =========================================================================

    private void OnSelectByHwnd(object sender, RoutedEventArgs e) => ParseAndSelectHwnd();

    private void OnHwndKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ParseAndSelectHwnd();
            e.Handled = true;
        }
    }

    private void ParseAndSelectHwnd()
    {
        string input = txtHwnd.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        IntPtr hwnd;
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(input.AsSpan(2), NumberStyles.HexNumber, null, out long val))
                return;
            hwnd = (IntPtr)val;
        }
        else
        {
            if (!long.TryParse(input, out long val)) return;
            hwnd = (IntPtr)val;
        }

        AppLogger.Log($"[Scrap] ParseAndSelectHwnd | input=\"{input}\", hwnd=0x{hwnd:X8}");
        _lastPickPoint = null;
        _lastChildHwnd = IntPtr.Zero;
        _targetOverlay?.HideOverlay();
        SelectWindow(hwnd);
    }

    private void SelectWindow(IntPtr hwnd)
    {
        _selectedHwnd = hwnd;
        txtHwnd.Text = $"0x{hwnd:X8}";

        var info = WindowInfo.Capture(hwnd);
        txtWindowInfo.Text = info.ToString();
        lblCaptureStatus.Text = $"선택됨: {info.ClassName} - {info.Title}";
        AppLogger.Log($"[Scrap] SelectWindow | hwnd=0x{hwnd:X8}, class={info.ClassName}");

        var framework = DetectFramework(info);
        string tag = framework switch
        {
            AppFramework.Flutter => "◆ Flutter",
            AppFramework.Electron => "◆ Electron/Chromium",
            _ => "◇ Native",
        };
        txtWindowInfo.Text = info.ToString() + $"\n    Framework: {tag}";

        if (framework != AppFramework.Unknown)
        {
            pnlElementTree.Visibility = Visibility.Visible;
            ElementTreeColumn.Width = new GridLength(1, GridUnitType.Star);
            lblElementTreeTitle.Text = $"ELEMENT_TREE // {tag}";
            _ = ScanElementTreeAsync(hwnd);
        }
        else
        {
            pnlElementTree.Visibility = Visibility.Collapsed;
            ElementTreeColumn.Width = new GridLength(0);
            txtElementTree.Clear();
        }
    }

    // =========================================================================
    //  Text capture — main entry
    // =========================================================================

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_selectedHwnd == IntPtr.Zero)
        {
            MessageBox.Show(Window.GetWindow(this) ?? (Window)null!,
                "먼저 대상 창을 선택하세요.",
                "AgentZero — Scrap",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Re-click while running → cancel.
        if (_captureCts != null)
        {
            _captureCts.Cancel();
            _captureCts = null;
            btnCapture.Content = "▶  CAPTURE";
            lblCaptureStatus.Text = "취소됨";
            return;
        }

        _captureCts = new CancellationTokenSource();
        _targetOverlay?.HideOverlay();
        btnCapture.Content = "■  STOP";

        var progress = new Progress<string>(msg => lblCaptureStatus.Text = msg);

        ScrapWriter? scrap = null;
        try
        {
            for (int i = 3; i > 0; i--)
            {
                _captureCts.Token.ThrowIfCancellationRequested();
                lblCaptureStatus.Text = $"{i}초 후 캡처 시작...";
                await Task.Delay(1000, _captureCts.Token);
            }

            NativeMethods.SetForegroundWindow(_selectedHwnd);
            await Task.Delay(500, _captureCts.Token);

            txtCapturedText.Clear();
            scrap = new ScrapWriter(AppContext.BaseDirectory);
            _currentScrap = scrap;
            AppLogger.Log($"[Scrap.UI] ScrapWriter 생성 + ChunkWritten 구독 | path={scrap.FilePath}");
            scrap.ChunkWritten += chunk => Dispatcher.BeginInvoke(() =>
            {
                AppLogger.Log($"[Scrap.UI] ChunkWritten → AppendWithCap | +{chunk.Length}자 (txt before={txtCapturedText.Text.Length}자)");
                AppendWithCap(chunk);
            });

            var scrollOpts = new ScrollOptions(
                DelayMs: int.TryParse(txtScrollDelay.Text, out int delay) ? Math.Clamp(delay, 50, 2000) : 200,
                MaxAttempts: int.TryParse(txtMaxAttempts.Text, out int max) ? Math.Clamp(max, 10, 9999) : 500,
                DeltaMultiplier: int.TryParse(txtScrollDelta.Text, out int delta) ? Math.Clamp(delta, 1, 20) : 3,
                FilterStartDate: dpFilterStart.SelectedDate,
                FilterEndDate: dpFilterEnd.SelectedDate);

            lblCaptureStatus.Text = "캡처 중...";
            string result = await _captureService.CaptureAsync(
                _selectedHwnd, _captureCts.Token, progress, scrap, null,
                scrollOpts, _lastPickPoint, _lastChildHwnd);

            string scrapPath = scrap?.FilePath ?? "";
            bool hasFilter = scrollOpts.FilterStartDate.HasValue || scrollOpts.FilterEndDate.HasValue;
            if (hasFilter && scrap != null && !string.IsNullOrEmpty(result))
            {
                txtCapturedText.Text = result;
                scrap.Dispose();
                scrap = null;
                File.WriteAllText(scrapPath, result, new UTF8Encoding(false));
            }

            lblCaptureStatus.Text = string.IsNullOrEmpty(result)
                ? "텍스트 없음"
                : $"완료 ({result.Length} 글자) → {scrapPath}";
        }
        catch (OperationCanceledException)
        {
            lblCaptureStatus.Text = "취소됨";
        }
        catch (Exception ex)
        {
            lblCaptureStatus.Text = "오류";
            MessageBox.Show(Window.GetWindow(this) ?? (Window)null!,
                ex.Message, "캡처 오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            scrap?.Dispose();
            _currentScrap = null;
            _captureCts?.Dispose();
            _captureCts = null;
            btnCapture.Content = "▶  CAPTURE";
        }
    }

    private void AppendWithCap(string chunk)
    {
        txtCapturedText.AppendText(chunk);
        if (txtCapturedText.Text.Length > MaxDisplayChars)
        {
            int removeCount = txtCapturedText.Text.Length - MaxDisplayChars + MaxDisplayChars / 4;
            string remaining = txtCapturedText.Text.Substring(removeCount);
            txtCapturedText.Text = "[...이전 내용은 파일 참조...]\r\n" + remaining;
        }
        txtCapturedText.ScrollToEnd();
    }

    // =========================================================================
    //  Toolbar buttons
    // =========================================================================

    private void OnClearClick(object sender, RoutedEventArgs e) => txtCapturedText.Clear();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(txtCapturedText.Text))
        {
            Clipboard.SetText(txtCapturedText.Text);
            lblCaptureStatus.Text = "클립보드에 복사됨";
        }
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        var scrapDir = Path.Combine(AppContext.BaseDirectory, "logs", "scrap");
        Directory.CreateDirectory(scrapDir);
        Process.Start("explorer.exe", scrapDir);
    }

    private void OnConsoleClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
        });
    }

    private void OnNumericInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    // =========================================================================
    //  Element Tree — Flutter / Accessibility
    // =========================================================================

    private static readonly System.Collections.Generic.HashSet<string> FlutterClassNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FLUTTER_RUNNER_WIN32_WINDOW",
            "FLUTTERWINDOW",
        };

    private static readonly System.Collections.Generic.HashSet<string> ElectronClassNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Chrome_WidgetWin_1",
            "Chrome_WidgetWin_0",
            "TeamsWebView",
            "CefBrowserWindow",
            "WebView2",
            "Chrome_RenderWidgetHostHWND",
        };

    private static readonly string[] ElectronProcessHints = new[]
    {
        "electron", "teams", "ms-teams", "slack", "discord", "code",
        "spotify", "notion", "obsidian", "figma", "postman",
    };

    private enum AppFramework { Unknown, Flutter, Electron }

    private static AppFramework DetectFramework(WindowInfo info)
    {
        if (FlutterClassNames.Contains(info.ClassName))
            return AppFramework.Flutter;
        if (info.ProcessName.Contains("flutter", StringComparison.OrdinalIgnoreCase))
            return AppFramework.Flutter;

        if (ElectronClassNames.Contains(info.ClassName))
            return AppFramework.Electron;
        foreach (var hint in ElectronProcessHints)
        {
            if (info.ProcessName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return AppFramework.Electron;
        }
        return AppFramework.Unknown;
    }

    private void OnRescanTreeClick(object sender, RoutedEventArgs e)
    {
        if (_selectedHwnd != IntPtr.Zero)
            _ = ScanElementTreeAsync(_selectedHwnd);
    }

    private void OnTreeSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrEmpty(_fullTreeText)) return;
        e.Handled = true;

        string keyword = txtTreeSearch.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            txtElementTree.Text = _fullTreeText;
            lblTreeSearchResult.Text = "";
            return;
        }

        var lines = _fullTreeText.Split('\n');
        var matched = lines.Where(l => l.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
        txtElementTree.Text = string.Join("\n", matched);
        lblTreeSearchResult.Text = $"{matched.Length}건";
        AppLogger.Log($"[Scrap] 트리 검색: \"{keyword}\" → {matched.Length}건");
    }

    private async Task ScanElementTreeAsync(IntPtr hwnd)
    {
        txtElementTree.Clear();
        lblElementTreeCount.Text = "(스캔 중...)";

        var result = await Task.Run(() => ElementTreeScanner.Scan(hwnd));

        if (result != null)
        {
            lblElementTreeCount.Text = $"({result.NodeCount} nodes)";
            AppLogger.Log($"[Scrap] Element tree 스캔 완료 | {result.NodeCount} nodes");
            _fullTreeText = result.TreeText;
            txtElementTree.Text = _fullTreeText;
        }
        else
        {
            lblElementTreeCount.Text = "(요소 없음)";
            txtElementTree.Clear();
        }
    }
}
