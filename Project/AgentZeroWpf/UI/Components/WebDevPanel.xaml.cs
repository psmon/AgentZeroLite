using System.IO;
using System.Windows;
using System.Windows.Controls;
using Agent.Common;
using AgentZeroWpf.Services.Browser;
using Microsoft.Web.WebView2.Core;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// In-app browser sandbox (WebView2) that loads a local web app from
/// <c>{exeDir}/Wasm/</c> and bridges JS calls to <see cref="WebDevHost"/>
/// via <see cref="WebDevBridge"/>. Lazily initializes on first show so
/// users who never open the WebDev tab don't pay the WebView2 cost.
/// </summary>
public partial class WebDevPanel : UserControl
{
    private const string VirtualHost = "zero.local";
    private const string DefaultEntry = "voice-test/index.html";

    private WebDevHost? _host;
    private WebDevBridge? _bridge;
    private bool _initStarted;
    private bool _windowClosedHooked;
    private string _entryPath = DefaultEntry;

    public WebDevPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
        Loaded += OnLoaded;
    }

    // Disposal is bound to the host window's Closed event, NOT to UserControl.Unloaded.
    // The Settings TabControl raises Unloaded every time the user switches away from
    // the WebDev tab; disposing webDevView there killed the WebView2 and made the next
    // Reload/DevTools click throw ObjectDisposedException — the typical trigger was
    // editing the Voice/TTS provider on a sibling tab and returning here to reload.
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
        try { _bridge?.Detach(); } catch { }
        try { _host?.Dispose(); } catch { }
        try { webDevView.Dispose(); } catch { }
    }

    /// <summary>Optional — change the loaded sub-app path (e.g. "voice-test/index.html").</summary>
    public string EntryPath
    {
        get => _entryPath;
        set => _ = SwitchEntryAsync(value);
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _initStarted) return;
        _initStarted = true;
        try
        {
            await InitWebViewAsync();
        }
        catch (Exception ex)
        {
            lblWebDevStatus.Text = "WebView2 init failed: " + ex.Message;
            AppLogger.Log($"[WebDev] init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task InitWebViewAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "AgentZeroLite_WebDev");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await webDevView.EnsureCoreWebView2Async(env);

        _host = new WebDevHost();
        _bridge = new WebDevBridge(webDevView.CoreWebView2, _host);

        var wasmRoot = Path.Combine(AppContext.BaseDirectory, "Wasm");
        if (!Directory.Exists(wasmRoot))
        {
            lblWebDevStatus.Text = "Wasm folder not found at " + wasmRoot;
            AppLogger.Log($"[WebDev] missing Wasm dir: {wasmRoot}");
            return;
        }

        webDevView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, wasmRoot, CoreWebView2HostResourceAccessKind.Allow);

        Navigate();
        lblWebDevStatus.Visibility = Visibility.Collapsed;
    }

    private void Navigate()
    {
        var url = $"https://{VirtualHost}/{_entryPath}";
        webDevView.CoreWebView2.Navigate(url);
        lblWebDevFooter.Text = $"loaded: {url}  ·  bridge: window.zero";
        AppLogger.Log($"[WebDev] Navigate → {url}");
    }

    private void OnWebDevReload(object sender, RoutedEventArgs e)
    {
        if (webDevView.CoreWebView2 is null) return;
        webDevView.CoreWebView2.Reload();
    }

    private void OnWebDevDevTools(object sender, RoutedEventArgs e)
    {
        if (webDevView.CoreWebView2 is null) return;
        webDevView.CoreWebView2.OpenDevToolsWindow();
    }

    private void OnWebDevAppChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cbWebDevApp.SelectedItem is not ComboBoxItem ci) return;
        var path = ci.Tag as string;
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = SwitchEntryAsync(path);
    }

    /// <summary>
    /// Stage <paramref name="path"/> as the active sub-app and navigate when ready.
    /// Handles three timing cases:
    ///   1. SelectionChanged fires during InitializeComponent (first IsSelected=True)
    ///      — `_initStarted` is false, init hasn't kicked off, so the new path just
    ///      becomes the path the upcoming Init will navigate to.
    ///   2. User picks combo while init is in flight — `_initStarted` true but
    ///      CoreWebView2 not ready; awaiting EnsureCoreWebView2Async lets us join
    ///      the in-flight init, then Navigate uses the new path.
    ///   3. User picks combo after init — straight Navigate, bypassing the previous
    ///      EntryPath setter's null-guard (which silently no-op'd if the getter
    ///      raced to null between checks on rapid clicks).
    /// </summary>
    private async Task SwitchEntryAsync(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? DefaultEntry : path;
        if (normalized == _entryPath && _initStarted) return;

        _entryPath = normalized;
        if (lblWebDevApp is not null)
            lblWebDevApp.Text = "  ·  " + _entryPath.Split('/')[0];

        if (!_initStarted) return;

        try
        {
            // Idempotent: returns immediately once initialized.
            await webDevView.EnsureCoreWebView2Async();
            Navigate();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] App switch navigate failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
