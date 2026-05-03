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
        foreach (var s in _viewsBySampleId.Values)
            s.View.Visibility = Visibility.Collapsed;
        slot.View.Visibility = Visibility.Visible;
        _activeView = slot.View;
        if (slot.FirstLoadCompleted) HideLoading();
        else ShowLoading(slot.View.Source?.ToString() ?? "");
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

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        // Reload only the active sample. Cached siblings stay as-is.
        _activeView?.CoreWebView2?.Reload();
    }

    private void OnDevToolsClick(object sender, RoutedEventArgs e)
    {
        _activeView?.CoreWebView2?.OpenDevToolsWindow();
    }

    private void OnInstallPluginClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Install WebDev Plugin",
            Filter = "Plugin package (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var result = WebDevPluginInstaller.InstallFromZip(dlg.FileName);
        if (result.Ok)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Installed: {result.Name} ({result.PluginId})",
                "WebDev plugin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _pendingSelectId = result.PluginId;
            RefreshSampleList(preserveSelection: true);
        }
        else
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Install failed:\n\n" + result.Error,
                "WebDev plugin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
