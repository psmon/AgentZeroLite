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
    private string _entryPath = DefaultEntry;

    public WebDevPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>Optional — change the loaded sub-app path (e.g. "voice-test/index.html").</summary>
    public string EntryPath
    {
        get => _entryPath;
        set
        {
            _entryPath = string.IsNullOrWhiteSpace(value) ? DefaultEntry : value;
            lblWebDevApp.Text = "  ·  " + _entryPath.Split('/')[0];
            if (_initStarted && webDevView.CoreWebView2 is not null)
                Navigate();
        }
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { _bridge?.Detach(); } catch { }
        try { _host?.Dispose(); } catch { }
        try { webDevView.Dispose(); } catch { }
    }
}
