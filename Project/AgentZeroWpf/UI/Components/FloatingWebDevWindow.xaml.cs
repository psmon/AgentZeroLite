using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using Agent.Common;
using AgentZeroWpf.Services.Browser;
using Microsoft.Web.WebView2.Wpf;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Floating window that hosts a single WebDev sample's WebView2 after the
/// user clicks Detach in the main WebDev page. The WebView2 instance is
/// **reparented**, never recreated — JS state, audio playback, scroll
/// position all survive the trip. Closing the window dock-backs to main.
///
/// Settings (pin / opacity) and bounds (x,y,w,h) are persisted per sample
/// id via <see cref="WebDevFloatingPrefs"/> and restored on the next
/// Detach for the same sample.
///
/// **Compact chrome, always visible.** A 24-px titlebar + 20-px footer
/// stay rendered at all times — no toggle. Earlier "title bar OFF" mode
/// proved brittle (HwndHost airspace hid recovery affordances; Alt-based
/// alternates caused event-bubble side effects), so the chrome is now
/// non-removable. WindowChrome handles drag/resize natively.
///
/// **Pin = independent lifetime.** When the user pins the window, we
/// detach <see cref="Window.Owner"/> so main-window minimize / restore /
/// activation no longer drag the floating window with it. Unpinning
/// re-attaches the owner so the windows tile together again.
/// </summary>
public partial class FloatingWebDevWindow : Window
{
    private readonly string _sampleId;
    private readonly string _sampleDisplayName;
    private readonly WebView2 _view;
    private readonly Action<string> _onDockBack;
    private readonly Window? _initialOwner;
    private WebDevFloatingPref _pref;
    private bool _suppressPersist;
    private bool _docked;          // true once WebView2 has been handed back

    public string SampleId => _sampleId;

    /// <param name="onDockBack">
    /// Called after the WebView2 has been removed from this window's body.
    /// The host (WebDevPagePanel) is expected to re-add the WebView2 to
    /// its own viewHost.Children and refresh selection state.
    /// </param>
    public FloatingWebDevWindow(
        string sampleId,
        string sampleDisplayName,
        WebView2 view,
        Window? owner,
        Action<string> onDockBack)
    {
        InitializeComponent();
        _sampleId = sampleId;
        _sampleDisplayName = sampleDisplayName;
        _view = view;
        _onDockBack = onDockBack;
        _initialOwner = owner;
        _pref = WebDevFloatingPrefs.Get(sampleId);

        // Owner is set conditionally — if pin is restored from prefs, we
        // start owner-less so a minimized main window doesn't drag us
        // down on first appearance.
        Owner = _pref.Topmost ? null : owner;

        Title = $"WebDev — {sampleDisplayName}";
        lblTitle.Text = $"WebDev — {sampleDisplayName}";

        ApplyPrefBeforeShow();

        // Wire the slider AFTER pref load so the synthetic ValueChanged
        // fired during XAML parse (Slider coerces default 0.0 up to its
        // Minimum=0.5) doesn't NRE on the still-null _pref.
        sldOpacity.ValueChanged += OnOpacityChanged;

        // Defer reparenting until AFTER our Window has its HwndSource —
        // adding a HwndHost (WebView2) to a tree whose root Window hasn't
        // been initialized yet causes BuildWindowCore to be called with no
        // valid parent HWND and the WebView2 fails to attach. Loaded is
        // raised after the visual tree is connected to a live HwndSource.
        Loaded += OnFirstLoaded;
        Closed += OnClosed;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        try
        {
            // Same WebView2 instance — just a new visual parent. WebView2's
            // HwndHost handles the parent-HWND swap as long as the source
            // tree is unparented (we do that in OnDetachClick) before we
            // add it here.
            webHost.Children.Add(_view);
            AppLogger.Log($"[WebDev:Floating] adopted view for '{_sampleId}'");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Floating] adopt failed for '{_sampleId}': " +
                          $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");

            // Hand the view back to the host immediately so the user isn't
            // stranded with an empty floating window AND no main view.
            DetachViewForDockBack();
            try { Close(); } catch { }
            // Don't rethrow — we're inside a Loaded event handler. Throwing
            // here would propagate to the dispatcher's unhandled-exception
            // path and could tear the process down.
        }
    }

    private void ApplyPrefBeforeShow()
    {
        _suppressPersist = true;
        try
        {
            // Bounds — only restore if all four were saved AND fall on a
            // currently visible work area. Otherwise let WPF position the
            // window at default and we'll persist the new spot on close.
            if (_pref.X is double px && _pref.Y is double py
                && _pref.W is double pw && _pref.H is double ph
                && pw >= MinWidth && ph >= MinHeight
                && IsRectOnAnyScreen(px, py, pw, ph))
            {
                Left = px;
                Top = py;
                Width = pw;
                Height = ph;
            }
            else if (_initialOwner is { } o)
            {
                Left = o.Left + 80;
                Top = o.Top + 80;
            }

            Topmost = _pref.Topmost;
            Opacity = Math.Clamp(_pref.Opacity, 0.5, 1.0);
            sldOpacity.Value = Opacity;
            miPin.IsChecked = _pref.Topmost;
            miChrome.IsChecked = _pref.Chrome;
            ApplyChrome(_pref.Chrome);
            UpdatePinButtonStyle();
            UpdateStatusLine();
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    private static bool IsRectOnAnyScreen(double x, double y, double w, double h)
    {
        // 64-px header sliver must intersect at least one work area so the
        // window can be moved by the user — otherwise treat the saved rect
        // as stale (e.g. monitor unplugged) and fall back to default.
        var headerRect = new System.Drawing.Rectangle(
            (int)x, (int)y, (int)w, 64);
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            if (s.WorkingArea.IntersectsWith(headerRect)) return true;
        }
        return false;
    }

    // ----- Settings handlers -----

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        bool? checkedState = (sender as MenuItem)?.IsChecked;
        bool newValue = checkedState ?? !Topmost;
        Topmost = newValue;
        _pref.Topmost = newValue;
        miPin.IsChecked = newValue;
        UpdatePinButtonStyle();
        UpdateStatusLine();
        ApplyOwnerForPinState();
        Persist();
    }

    /// <summary>
    /// When pinned, detach <see cref="Window.Owner"/> so main-window state
    /// changes (minimize / activate) don't propagate. When unpinned,
    /// re-attach so the windows tile together. Owner reassignment after
    /// Show throws on some WPF versions — wrap in try/catch.
    /// </summary>
    private void ApplyOwnerForPinState()
    {
        try
        {
            Owner = _pref.Topmost ? null : _initialOwner;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Floating] owner toggle failed: " +
                          $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdatePinButtonStyle()
    {
        btnPinChrome.Background = Topmost
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Media.SolidColorBrush)
                new System.Windows.Media.BrushConverter().ConvertFromString("#0A84FF")!;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;
        if (lblOpacityPct is not null)
            lblOpacityPct.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        if (_pref is null) return;
        _pref.Opacity = e.NewValue;
        UpdateStatusLine();
        Persist();
    }

    private void UpdateStatusLine()
    {
        if (lblFooterStatus is null) return;
        var pct = (int)Math.Round(Opacity * 100);
        var pin = Topmost ? "on" : "off";
        var chrome = _pref.Chrome ? "on" : "off";
        lblFooterStatus.Text = $"opacity {pct}% · pin {pin} · titlebar {chrome}";
    }

    // ----- Chrome (titlebar/footer) toggle -----

    private void OnChromeToggle(object sender, RoutedEventArgs e)
    {
        bool? checkedState = (sender as MenuItem)?.IsChecked;
        bool newValue = checkedState ?? !_pref.Chrome;
        _pref.Chrome = newValue;
        ApplyChrome(newValue);
        UpdateStatusLine();
        Persist();
    }

    private void ApplyChrome(bool show)
    {
        rowHandle.Height = show ? new GridLength(0) : new GridLength(8);
        rowTitle.Height  = show ? new GridLength(24) : new GridLength(0);
        rowFooter.Height = show ? new GridLength(20) : new GridLength(0);
        titleBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        footerBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        chromeHandle.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

        // Match WindowChrome caption to the visible titlebar so the user
        // can't drag from an empty top strip when chrome is off — the
        // chromeHandle handles drag in that mode. WindowChrome from XAML
        // can come back frozen; clone-and-replace if so.
        try
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome is null) return;
            if (chrome.IsFrozen)
            {
                chrome = (WindowChrome)chrome.Clone();
                WindowChrome.SetWindowChrome(this, chrome);
            }
            chrome.CaptionHeight = show ? 24 : 0;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Floating] ApplyChrome WindowChrome update failed: " +
                          $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drag to move; single click (no drag) restores the titlebar. We
    /// detect "no movement" by comparing the window's Left/Top before
    /// and after DragMove — DragMove is blocking so this works without
    /// a separate gesture state machine.
    /// </summary>
    private void OnChromeHandleLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.ButtonState != MouseButtonState.Pressed) return;

        var beforeLeft = Left;
        var beforeTop = Top;
        try { DragMove(); } catch { /* may throw if not pressed */ }

        bool moved = Math.Abs(Left - beforeLeft) > 1
                     || Math.Abs(Top - beforeTop) > 1;
        if (!moved)
        {
            _pref.Chrome = true;
            miChrome.IsChecked = true;
            ApplyChrome(true);
            UpdateStatusLine();
            Persist();
        }
        e.Handled = true;
    }

    /// <summary>
    /// Right-click on the chrome-handle strip opens the same context menu
    /// as the (hidden) titlebar — the only menu access while chrome is
    /// off, since we removed the Alt-overlay path entirely.
    /// </summary>
    private void OnChromeHandleRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ContextMenu is { } cm)
        {
            cm.PlacementTarget = (UIElement)sender;
            cm.IsOpen = true;
        }
        e.Handled = true;
    }

    private void OnReload(object sender, RoutedEventArgs e)
        => _view.CoreWebView2?.Reload();

    private void OnDevTools(object sender, RoutedEventArgs e)
        => _view.CoreWebView2?.OpenDevToolsWindow();

    private void OnDockBack(object sender, RoutedEventArgs e)
        => Close();   // Closed → DetachAndDockBack → host re-adopts the view

    // ----- Close / dock-back lifecycle -----

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_suppressPersist) return;
        _pref.X = Left; _pref.Y = Top;
        Persist();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_suppressPersist) return;
        _pref.W = Width; _pref.H = Height;
        Persist();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Final persist BEFORE handing the view back — bounds at close
        // should be the next Detach's restore point.
        _pref.X = Left; _pref.Y = Top;
        _pref.W = Width; _pref.H = Height;
        Persist();

        DetachViewForDockBack();
    }

    private void DetachViewForDockBack()
    {
        if (_docked) return;
        _docked = true;

        try
        {
            webHost.Children.Remove(_view);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Floating] detach failed for '{_sampleId}': " +
                          $"{ex.GetType().Name}: {ex.Message}");
        }

        try { _onDockBack(_sampleId); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Floating] dock-back callback threw for '{_sampleId}': " +
                          $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Persist()
    {
        if (_suppressPersist) return;
        WebDevFloatingPrefs.Save(_sampleId, _pref);
    }
}
