using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Agent.Common;
using AgentZeroWpf.Services.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Top-level WebDev page hosted in MainWindow. Three-column layout:
/// 240px sample list (built-ins + plugins) on the left, full-width
/// WebView2 canvas on the right.
///
/// **Per-sample WebView2 cache**: each sample owns its own WebView2 the
/// first time it's selected; subsequent selections are a Visibility
/// toggle, so switching is instant and JS state is preserved. Reload
/// button is the explicit refresh channel for settings changes.
///
/// All views share one WebDevHost (single LLM session, single voice
/// pipeline) and one CoreWebView2Environment (single user-data folder),
/// so plugins observe consistent state across hops.
///
/// Disposal is bound to the host window's Closed event, NOT
/// UserControl.Unloaded — the WebDev menu can be toggled in/out of
/// existence many times per session and tearing the views down on every
/// hide would defeat the entire point of the cache.
/// </summary>
public partial class WebDevPagePanel : UserControl
{
    private const string DefaultEntryFolder = "voice-test";

    private CoreWebView2Environment? _env;
    private WebDevHost? _host;
    private bool _initStarted;
    private bool _initCompleted;
    private bool _windowClosedHooked;
    private string? _pendingSelectId;

    private readonly Dictionary<string, ViewSlot> _viewsBySampleId = new();
    private readonly Dictionary<string, FloatingWebDevWindow> _floatingBySampleId = new();
    private WebView2? _activeView;

    // WebView2 default background is white — paints a frame of white before
    // the first navigation completes. Setting DefaultBackgroundColor to the
    // dark canvas color hides that flash so the loading overlay can take
    // over without a strobe.
    private static readonly System.Drawing.Color CanvasBg
        = System.Drawing.Color.FromArgb(0x0A, 0x0A, 0x14);

    private Storyboard? _spinSb;
    private Storyboard? _dotsSb;
    private bool _loadingShown;

    private sealed class ViewSlot
    {
        public WebView2 View { get; }
        public WebDevBridge Bridge { get; }
        public bool FirstLoadCompleted { get; set; }
        public ViewSlot(WebView2 view, WebDevBridge bridge)
        {
            View = view;
            Bridge = bridge;
        }
    }

    public WebDevPagePanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
        Loaded += OnLoaded;
        _spinSb = (Storyboard)Resources["LoadingSpinStoryboard"];
        _dotsSb = (Storyboard)Resources["LoadingDotsStoryboard"];
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowClosedHooked) return;
        var w = Window.GetWindow(this);
        if (w is null) return;
        _windowClosedHooked = true;
        w.Closed += OnHostWindowClosed;
    }

    private void OnHostWindowClosed(object? sender, EventArgs e)
    {
        // Close every floating window FIRST so each one re-parents its
        // WebView2 back into viewHost via the dock-back callback. Snapshot
        // the values: dictionary mutates inside DockBack().
        foreach (var w in _floatingBySampleId.Values.ToArray())
        {
            try { w.Close(); } catch { }
        }
        _floatingBySampleId.Clear();

        foreach (var slot in _viewsBySampleId.Values)
        {
            try { slot.Bridge.Detach(); } catch { }
            try { slot.View.Dispose(); } catch { }
        }
        _viewsBySampleId.Clear();
        _activeView = null;
        try { _host?.Dispose(); } catch { }
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _initStarted) return;
        _initStarted = true;
        try
        {
            await InitAsync();
        }
        catch (Exception ex)
        {
            lblStatus.Text = "WebView2 init failed: " + ex.Message;
            AppLogger.Log($"[WebDev:Page] init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task InitAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "AgentZeroLite_WebDev");
        _env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        _host = new WebDevHost();

        Directory.CreateDirectory(WebDevSampleCatalog.PluginsRoot);

        _initCompleted = true;
        lblStatus.Visibility = Visibility.Collapsed;

        RefreshSampleList(preserveSelection: false);
    }

    private void RefreshSampleList(bool preserveSelection)
    {
        var prevId = preserveSelection
            ? (lstSamples.SelectedItem as WebDevSample)?.Id ?? _pendingSelectId
            : _pendingSelectId;

        var samples = WebDevSampleCatalog.Discover();
        lstSamples.ItemsSource = samples;

        var builtIns = 0;
        var plugins = 0;
        foreach (var s in samples) { if (s.IsBuiltIn) builtIns++; else plugins++; }
        lblCounts.Text = $"built-in: {builtIns}  ·  plugins: {plugins}";

        WebDevSample? toSelect = null;
        if (!string.IsNullOrEmpty(prevId))
            toSelect = samples.FirstOrDefault(s => s.Id == prevId);
        toSelect ??= samples.FirstOrDefault(s => s.Id == DefaultEntryFolder)
                    ?? samples.FirstOrDefault();

        _pendingSelectId = null;
        if (toSelect is not null)
            lstSamples.SelectedItem = toSelect;
    }

    private async void OnSampleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (lstSamples.SelectedItem is not WebDevSample s) return;
        lblBreadcrumbSample.Text = s.DisplayName;

        if (!_initCompleted)
        {
            _pendingSelectId = s.Id;
            return;
        }

        // Sample is detached → show placeholder instead of (now empty) viewHost
        if (_floatingBySampleId.ContainsKey(s.Id))
        {
            ShowFloatingPlaceholder(s);
            return;
        }

        // Cache hit — just flip visibility, JS keeps running.
        if (_viewsBySampleId.TryGetValue(s.Id, out var cached))
        {
            ShowOnly(cached);
            return;
        }

        // Cache miss — create a dedicated WebView2 for this sample.
        ShowLoading(s.Url);
        try
        {
            var view = new WebView2 { DefaultBackgroundColor = CanvasBg };
            viewHost.Children.Add(view);
            await view.EnsureCoreWebView2Async(_env);

            // Suppress WebView2's built-in right-click menu — when this
            // sample is later detached into a chrome-less floating window,
            // the only menu the user should see on right-click is OURS
            // (Pin / Opacity / Title bar / Reload / DevTools / Dock back).
            // Otherwise the WebView2 menu pops over our menu and the user
            // can't reach the chrome-restore action.
            try { view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; }
            catch { /* setting may not exist on older WebView2 — ignore */ }

            view.CoreWebView2.SetVirtualHostNameToFolderMapping(
                WebDevSampleCatalog.BuiltInVirtualHost,
                WebDevSampleCatalog.BuiltInRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            view.CoreWebView2.SetVirtualHostNameToFolderMapping(
                WebDevSampleCatalog.PluginVirtualHost,
                WebDevSampleCatalog.PluginsRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            var bridge = new WebDevBridge(view.CoreWebView2, _host!);
            var slot = new ViewSlot(view, bridge);
            _viewsBySampleId[s.Id] = slot;

            // Loading overlay reacts to nav events for the active view only.
            view.CoreWebView2.NavigationStarting += (_, _) =>
            {
                if (ReferenceEquals(view, _activeView)) ShowLoading(s.Url);
            };
            view.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                slot.FirstLoadCompleted = true;
                if (ReferenceEquals(view, _activeView)) HideLoading();
            };

            view.CoreWebView2.Navigate(s.Url);
            ShowOnly(slot);
            AppLogger.Log($"[WebDev:Page] mount + navigate → {s.Url}");
        }
        catch (Exception ex)
        {
            HideLoading();
            AppLogger.Log($"[WebDev:Page] mount failed for '{s.Id}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ShowOnly(ViewSlot slot)
    {
        floatingPlaceholder.Visibility = Visibility.Collapsed;
        lblBreadcrumbFloating.Visibility = Visibility.Collapsed;
        viewHost.Visibility = Visibility.Visible;
        foreach (var s in _viewsBySampleId.Values)
            s.View.Visibility = Visibility.Collapsed;
        slot.View.Visibility = Visibility.Visible;
        _activeView = slot.View;
        if (slot.FirstLoadCompleted) HideLoading();
        else ShowLoading(slot.View.Source?.ToString() ?? "");
        UpdateDetachButton();
    }

    private void ShowFloatingPlaceholder(WebDevSample sample)
    {
        HideLoading();
        viewHost.Visibility = Visibility.Collapsed;
        floatingPlaceholder.Visibility = Visibility.Visible;
        lblBreadcrumbFloating.Visibility = Visibility.Visible;
        lblFloatingPlaceholderTitle.Text =
            $"{sample.DisplayName} is running in a floating window";
        _activeView = null;
        UpdateDetachButton();
    }

    /// <summary>
    /// Toolbar's Detach button is a dual-purpose toggle: when the active
    /// sample is currently floating, it reads "복귀 / Dock back" and
    /// closes the floating window on click. Otherwise it reads "Detach"
    /// and spawns a floating window. Without this flip the user has no
    /// toolbar-level affordance to bring a sample back home.
    /// </summary>
    private void UpdateDetachButton()
    {
        if (lstSamples.SelectedItem is not WebDevSample s)
        {
            btnDetach.Visibility = Visibility.Collapsed;
            return;
        }
        btnDetach.Visibility = Visibility.Visible;
        bool isFloating = _floatingBySampleId.ContainsKey(s.Id);
        if (isFloating)
        {
            btnDetach.Content = "⤡  복귀 (Dock back)";
            btnDetach.Background = (System.Windows.Media.SolidColorBrush)
                new System.Windows.Media.BrushConverter().ConvertFromString("#FFA94D")!;
            btnDetach.ToolTip = "Bring this sample's WebView2 back into the main window (no reload).";
        }
        else
        {
            btnDetach.Content = "⤢  Detach";
            btnDetach.Background = (System.Windows.Media.SolidColorBrush)
                new System.Windows.Media.BrushConverter().ConvertFromString("#0A84FF")!;
            btnDetach.ToolTip = "Detach this sample to a floating window (right-click in the new window for pin / opacity / titlebar settings).";
        }
    }

    private void ShowLoading(string detail)
    {
        lblLoadingDetail.Text = detail;
        if (_loadingShown) return;
        _loadingShown = true;
        loadingOverlay.Visibility = Visibility.Visible;
        _spinSb?.Begin(this, isControllable: true);
        _dotsSb?.Begin(this, isControllable: true);
    }

    private void HideLoading()
    {
        if (!_loadingShown) return;
        _loadingShown = false;
        loadingOverlay.Visibility = Visibility.Collapsed;
        try { _spinSb?.Stop(this); } catch { }
        try { _dotsSb?.Stop(this); } catch { }
    }

    private void OnReloadListClick(object sender, RoutedEventArgs e)
        => RefreshSampleList(preserveSelection: true);

    /// <summary>
    /// Uninstall handler wired from the per-row × button. Confirms with
    /// the user, deletes the mounted folder, evicts the cached WebView2
    /// for that sample (so the next visit reads fresh state), then
    /// refreshes the list. Built-in samples are filtered out at the
    /// XAML level so they never reach this handler.
    /// </summary>
    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not WebDevSample s) return;
        if (s.IsBuiltIn) return; // belt + braces — XAML hides the button too

        var owner = Window.GetWindow(this);
        var confirm = MessageBox.Show(owner,
            $"Uninstall plugin '{s.DisplayName}'?\n\nThis deletes the folder under %LOCALAPPDATA%\\AgentZeroLite\\Wasm\\plugins\\{s.Id}\\.",
            "Uninstall Plugin",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        // If the plugin is currently floating, close the floating window
        // first so its WebView2 returns to viewHost (where Dispose paths
        // below can find and tear it down).
        if (_floatingBySampleId.TryGetValue(s.Id, out var floating))
        {
            try { floating.Close(); } catch { }
        }

        // Tear down the cached WebView2 first so the file handles release
        // before Directory.Delete tries to remove the plugin folder.
        if (_viewsBySampleId.TryGetValue(s.Id, out var slot))
        {
            try { slot.Bridge.Detach(); } catch { }
            try { slot.View.Dispose(); } catch { }
            try { viewHost.Children.Remove(slot.View); } catch { }
            _viewsBySampleId.Remove(s.Id);
            if (ReferenceEquals(_activeView, slot.View)) _activeView = null;
        }

        var result = WebDevPluginInstaller.Uninstall(s.Id);
        if (!result.Ok)
        {
            MessageBox.Show(owner,
                "Uninstall failed:\n\n" + result.Error,
                "Uninstall Plugin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshSampleList(preserveSelection: false);

        // Stop the click from bubbling — without this the parent ListBoxItem
        // would also fire SelectionChanged for the row we just removed.
        e.Handled = true;
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        // Reload only the active sample. Cached siblings stay as-is.
        // Floating samples reload via their own right-click menu.
        _activeView?.CoreWebView2?.Reload();
    }

    private void OnDevToolsClick(object sender, RoutedEventArgs e)
    {
        _activeView?.CoreWebView2?.OpenDevToolsWindow();
    }

    /// <summary>
    /// Detach the active sample's WebView2 into a <see cref="FloatingWebDevWindow"/>.
    /// The view is *reparented*, not recreated — JS state, audio playback, and
    /// scroll position survive. Re-clicking Detach for an already-floating
    /// sample focuses the existing window (no second window per sample).
    /// </summary>
    private void OnDetachClick(object sender, RoutedEventArgs e)
    {
        if (lstSamples.SelectedItem is not WebDevSample s) return;

        // Toolbar button is dual-purpose — if this sample is currently
        // floating, the click means "dock back".
        if (_floatingBySampleId.TryGetValue(s.Id, out var existing))
        {
            try { existing.Close(); }   // OnClosed → DockBackFromFloating
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Page] dock-back via toolbar failed for '{s.Id}': " +
                              $"{ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        if (!_viewsBySampleId.TryGetValue(s.Id, out var slot))
        {
            AppLogger.Log($"[WebDev:Page] detach skipped — no cached view for '{s.Id}' yet");
            return;
        }

        WebView2? viewBeingMoved = slot.View;
        FloatingWebDevWindow? floating = null;

        try
        {
            // Remove the WebView2 from viewHost — it now belongs to the
            // floating window, but the same instance survives.
            viewHost.Children.Remove(viewBeingMoved);
            if (ReferenceEquals(_activeView, viewBeingMoved)) _activeView = null;

            var owner = Window.GetWindow(this);
            floating = new FloatingWebDevWindow(
                sampleId: s.Id,
                sampleDisplayName: s.DisplayName,
                view: viewBeingMoved,
                owner: owner,
                onDockBack: DockBackFromFloating);

            _floatingBySampleId[s.Id] = floating;
            floating.Show();
            ShowFloatingPlaceholder(s);
            AppLogger.Log($"[WebDev:Page] detach → '{s.Id}' floating at " +
                          $"{floating.Left:F0},{floating.Top:F0} " +
                          $"{floating.Width:F0}x{floating.Height:F0}");
        }
        catch (Exception ex)
        {
            var info = $"{ex.GetType().FullName}: {ex.Message}\n\n" +
                       $"Stack:\n{ex.StackTrace}";
            AppLogger.Log($"[WebDev:Page] detach failed for '{s.Id}': {info}");

            // Recover: re-adopt the view back into viewHost so it isn't
            // orphaned when the floating window failed to materialize.
            _floatingBySampleId.Remove(s.Id);
            if (viewBeingMoved is not null && viewBeingMoved.Parent is null)
            {
                try { viewHost.Children.Add(viewBeingMoved); } catch { }
            }
            try { floating?.Close(); } catch { }
            ShowOnly(slot);

            // Make the diagnostic shareable — paste-back is the fastest
            // path to root-cause when the operator hits this in the wild.
            try { Clipboard.SetText(info); } catch { }
            MessageBox.Show(Window.GetWindow(this),
                $"Detach failed.\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                "(Full error + stack trace copied to clipboard — paste it " +
                "back so the harness can analyze the root cause.)",
                "WebDev — Detach failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDockBackClick(object sender, RoutedEventArgs e)
    {
        if (lstSamples.SelectedItem is not WebDevSample s) return;
        if (_floatingBySampleId.TryGetValue(s.Id, out var w))
        {
            try { w.Close(); } catch { }   // OnClosed → DockBackFromFloating
        }
    }

    private void OnFocusFloatingClick(object sender, RoutedEventArgs e)
    {
        if (lstSamples.SelectedItem is not WebDevSample s) return;
        if (_floatingBySampleId.TryGetValue(s.Id, out var w))
        {
            try { w.Activate(); } catch { }
        }
    }

    /// <summary>
    /// Callback from <see cref="FloatingWebDevWindow"/> after its WebView2
    /// has been removed from the floating window's body. Re-adopts the
    /// view into viewHost.Children and refreshes the active selection.
    /// </summary>
    private void DockBackFromFloating(string sampleId)
    {
        _floatingBySampleId.Remove(sampleId);

        if (!_viewsBySampleId.TryGetValue(sampleId, out var slot)) return;

        try
        {
            // Defensive: if some earlier path already added the view back,
            // skip the re-add (WPF would throw otherwise).
            if (slot.View.Parent is null)
                viewHost.Children.Add(slot.View);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Page] dock-back add failed for '{sampleId}': " +
                          $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Restore visibility only if the user is currently looking at this
        // sample. Otherwise the cached slot just goes back to "hidden in
        // viewHost" and the next selection will surface it.
        if (lstSamples.SelectedItem is WebDevSample s && s.Id == sampleId)
        {
            ShowOnly(slot);
        }
        AppLogger.Log($"[WebDev:Page] dock-back ← '{sampleId}'");
    }

    private async void OnInstallPluginClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var picker = new InstallPluginPickerDialog { Owner = owner };
        if (picker.ShowDialog() != true) return;

        InstallResult? result = null;
        if (picker.Mode == InstallPluginPickerDialog.InstallMode.Zip)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Install WebDev Plugin — pick a .zip",
                Filter = "Plugin package (*.zip)|*.zip|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(owner) != true) return;
            result = WebDevPluginInstaller.InstallFromZip(dlg.FileName);
        }
        else if (picker.Mode == InstallPluginPickerDialog.InstallMode.Git)
        {
            var url = picker.GitUrl?.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;
            ShowLoading("Installing from " + url);
            try
            {
                result = await WebDevPluginInstaller.InstallFromGitUrlAsync(url);
            }
            finally
            {
                if (_activeView is null || _viewsBySampleId.Values.All(s => !s.FirstLoadCompleted))
                    HideLoading();
                else if (_activeView is { } av && _viewsBySampleId.Values.FirstOrDefault(s => ReferenceEquals(s.View, av)) is { FirstLoadCompleted: true })
                    HideLoading();
            }
        }
        if (result is null) return;

        if (result.Ok)
        {
            MessageBox.Show(owner,
                $"Installed: {result.Name} ({result.PluginId})",
                "WebDev plugin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _pendingSelectId = result.PluginId;
            RefreshSampleList(preserveSelection: true);
        }
        else
        {
            MessageBox.Show(owner,
                "Install failed:\n\n" + result.Error,
                "WebDev plugin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
