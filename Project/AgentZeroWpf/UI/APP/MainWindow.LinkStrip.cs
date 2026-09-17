using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Services;
using AgentZeroWpf.Module;
using AgentZeroWpf.Services;

namespace AgentZeroWpf.UI.APP;

/// <summary>
/// Terminal hyperlink detection → "open in browser" strip.
///
/// Why a strip as well as clickable text: the terminal renders OSC 8 and bare
/// URLs as Ctrl+click links through xterm.js's web-links addon, but OAuth /
/// device-login flows (claude, gh, az, gcloud) print a URL and then wait, and the
/// link is easy to miss in a busy scrollback. So every tab's session output is also
/// watched host-side by <see cref="TerminalLinkDetector"/> (which joins
/// soft-wrapped URLs) and the latest link is surfaced in a strip. Both paths end in
/// <see cref="ExternalLinkOpener"/>.
///
/// <para>The strip used to sit <i>above</i> the terminal rather than over it,
/// because the HwndHost-based control it replaced would punch through any overlay.
/// That constraint is gone.</para>
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan LinkStripAutoHide = TimeSpan.FromMinutes(3);

    private static readonly System.Windows.Media.SolidColorBrush LinkStripBg =
        new(System.Windows.Media.Color.FromRgb(0x0F, 0x1F, 0x33));
    private static readonly System.Windows.Media.SolidColorBrush LinkStripBorder =
        new(System.Windows.Media.Color.FromRgb(0x3B, 0x8E, 0xEA));
    private static readonly System.Windows.Media.SolidColorBrush LinkStripFg =
        new(System.Windows.Media.Color.FromRgb(0x9C, 0xDC, 0xFE));
    private static readonly System.Windows.Media.SolidColorBrush LinkStripLabelFg =
        new(System.Windows.Media.Color.FromRgb(0xC8, 0xD8, 0xEA));

    /// <summary>
    /// Attach a link detector to the tab's current session. Called from
    /// <see cref="BindSessionToActors"/> exactly once per new session (same
    /// dedup as the health alert); a restart replaces the session, so the old
    /// detector is disposed first.
    /// </summary>
    private void WireLinkDetector(ConsoleTabInfo tab)
    {
        if (tab.Session is null) return;
        DisposeLinkDetector(tab);

        var session = tab.Session;
        var detector = new TerminalLinkDetector(session, ResolveTerminalColumns(tab));
        detector.LinkDetected += url =>
        {
            // Fires on the session's output thread — marshal to UI. Drop the
            // event if the tab has since moved on to another session.
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(tab.Session, session)) return;
                    ShowLinkStrip(tab, url);
                }));
            }
            catch { /* dispatcher shutting down */ }
        };
        tab.LinkDetector = detector;
        AppLogger.Log($"[Link] detector wired | label={tab.Title} session={session.SessionId} cols={detector.Columns?.ToString() ?? "?"}");
    }

    private static void DisposeLinkDetector(ConsoleTabInfo tab)
    {
        if (tab.LinkDetector is null) return;
        try { tab.LinkDetector.Dispose(); } catch { }
        tab.LinkDetector = null;
    }

    // The xterm renderer reports its fitted width; the EasyConPty control
    // doesn't expose one, so that backend falls back to the scanner's default
    // soft-wrap width.
    private static int? ResolveTerminalColumns(ConsoleTabInfo tab)
    {
        try
        {
            if (tab.XtermTerminal is { } x && x.Columns > 0) return x.Columns;
        }
        catch { }
        return null;
    }

    private void ShowLinkStrip(ConsoleTabInfo tab, string url)
    {
        if (tab.StripHost is null) return;
        if (tab.LinkStrip is null) BuildLinkStrip(tab);

        // Keep the detector's wrap heuristic current — xterm may have been
        // resized since the session was wired.
        if (tab.LinkDetector is { } det && ResolveTerminalColumns(tab) is { } cols)
            det.Columns = cols;

        tab.LinkStripUrl = url;
        tab.LinkStripText!.Text = url;
        tab.LinkStripText.ToolTip = url;
        tab.LinkStrip!.Visibility = Visibility.Visible;

        tab.LinkStripHideTimer?.Stop();
        tab.LinkStripHideTimer?.Start();
        AppLogger.Log($"[Link] strip shown | label={tab.Title} url=[{url}]");
    }

    private static void HideLinkStrip(ConsoleTabInfo tab)
    {
        tab.LinkStripHideTimer?.Stop();
        if (tab.LinkStrip is null) return;
        tab.LinkStrip.Visibility = Visibility.Collapsed;
        tab.LinkStripUrl = null;
    }

    private void BuildLinkStrip(ConsoleTabInfo tab)
    {
        var label = new TextBlock
        {
            Text = "🔗 Link detected",
            Foreground = LinkStripLabelFg,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        DockPanel.SetDock(label, Dock.Left);

        var closeBtn = MakeStripButton("✕", "링크 표시 닫기");
        closeBtn.Click += (_, _) => HideLinkStrip(tab);
        DockPanel.SetDock(closeBtn, Dock.Right);

        var copyBtn = MakeStripButton("Copy", "URL을 클립보드에 복사");
        copyBtn.Click += (_, _) =>
        {
            if (tab.LinkStripUrl is not { } u) return;
            try { Clipboard.SetText(u); } catch (Exception ex) { AppLogger.Log($"[Link] clipboard failed: {ex.Message}"); }
        };
        DockPanel.SetDock(copyBtn, Dock.Right);

        var openBtn = MakeStripButton("Open in Browser", "기본 브라우저로 이 링크 열기 (OAuth 로그인 등)");
        openBtn.FontWeight = FontWeights.Bold;
        openBtn.Click += (_, _) => OpenLinkFromStrip(tab);
        DockPanel.SetDock(openBtn, Dock.Right);

        var urlText = new TextBlock
        {
            Foreground = LinkStripFg,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
            Margin = new Thickness(0, 0, 8, 0),
        };
        urlText.MouseLeftButtonUp += (_, e) => { OpenLinkFromStrip(tab); e.Handled = true; };

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(label);
        dock.Children.Add(closeBtn);
        dock.Children.Add(copyBtn);
        dock.Children.Add(openBtn);
        dock.Children.Add(urlText);

        var strip = new Border
        {
            Background = LinkStripBg,
            BorderBrush = LinkStripBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 4, 4, 4),
            Visibility = Visibility.Collapsed,
            Child = dock,
        };

        var hide = new DispatcherTimer { Interval = LinkStripAutoHide };
        hide.Tick += (_, _) => HideLinkStrip(tab);

        tab.LinkStrip = strip;
        tab.LinkStripText = urlText;
        tab.LinkStripHideTimer = hide;
        tab.StripHost!.Children.Add(strip);
    }

    private void OpenLinkFromStrip(ConsoleTabInfo tab)
    {
        if (tab.LinkStripUrl is not { } url) return;
        ExternalLinkOpener.TryOpen(url, $"strip:{tab.Title}");
        // Keep the strip up (the user may need it again) but restart the
        // auto-hide clock.
        tab.LinkStripHideTimer?.Stop();
        tab.LinkStripHideTimer?.Start();
    }

    // Focusable=false: clicking a strip button must not steal keyboard focus
    // from the terminal (the HwndHost/WebView2 would need a re-focus dance).
    private static Button MakeStripButton(string content, string tooltip) => new()
    {
        Content = content,
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(0, 0, 6, 0),
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        FontSize = 10,
        Foreground = LinkStripFg,
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x2A, 0x44)),
        BorderBrush = LinkStripBorder,
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Focusable = false,
        ToolTip = tooltip,
    };
}
