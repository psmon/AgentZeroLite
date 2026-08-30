using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentZeroWpf.Services;
using Microsoft.Web.WebView2.Core;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// A terminal tab rendered by xterm.js inside a WebView2, driven by a
/// <see cref="ManagedConPtyHost"/>. The WebViewXterm backend's UI control —
/// the counterpart to <c>EasyWindowsTerminalControl.EasyTerminalControl</c>.
///
/// Unlike the HwndHost-based control, this is a normal WPF element with NO
/// airspace: WPF overlays (approval toasts, wedge banners) render above it.
///
/// Assets are served offline from <c>Wasm/xterm/</c> via a virtual host
/// mapping (same pattern as WebDevBridge's mp3.local), so nothing touches the
/// network — CSP in index.html blocks it anyway.
/// </summary>
public partial class XtermTerminalControl : UserControl
{
    private const string VirtualHost = "term.local";

    private ManagedConPtyHost? _host;
    private bool _webReady;
    private bool _initStarted;
    private readonly object _pendingSync = new();
    private readonly List<string> _pendingOutput = new();

    // Renderer-reported viewport size (xterm fit addon). The pseudo-console is
    // resized to match once the renderer signals ready.
    private short _cols = 80, _rows = 24;

    /// <summary>The managed ConPTY host, once <see cref="StartPty"/> has run. The
    /// <see cref="WebViewXtermTerminalSession"/> wraps this.</summary>
    public ManagedConPtyHost? PtyHost => _host;

    public XtermTerminalControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var userData = Path.Combine(Path.GetTempPath(), "AgentZeroLite_Xterm");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Web.EnsureCoreWebView2Async(env);

            try { Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; } catch { }

            var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Wasm", "xterm");
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, assetsRoot, CoreWebView2HostResourceAccessKind.Allow);

            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] WebView2 init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Create + start the managed ConPTY host for this tab. Safe to call before
    /// the WebView2 is ready — output buffers until the renderer signals ready.
    /// Returns the host so the caller can build the <see cref="WebViewXtermTerminalSession"/>.
    /// </summary>
    public ManagedConPtyHost StartPty(string commandLine, string? workingDir)
    {
        if (_host is not null) return _host;

        _host = new ManagedConPtyHost();
        _host.Output += OnHostOutput;
        try
        {
            _host.Start(commandLine, workingDir, _cols, _rows);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] ConPTY start failed: {ex.GetType().Name}: {ex.Message}");
        }
        return _host;
    }

    // ConPTY output → xterm.js. Runs on the host read thread → marshal to UI
    // (CoreWebView2 is STA-bound). Buffer until the renderer is ready.
    private void OnHostOutput(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            lock (_pendingSync)
            {
                if (!_webReady)
                {
                    _pendingOutput.Add(chunk);
                    return;
                }
            }
            PostToWeb("out", chunk);
        }));
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();

            switch (type)
            {
                case "ready":
                    ApplyResizeFromMessage(root);
                    FlushPending();
                    break;
                case "in":
                    if (root.TryGetProperty("data", out var dataEl))
                        _host?.Write((dataEl.GetString() ?? "").AsSpan());
                    break;
                case "resize":
                    ApplyResizeFromMessage(root);
                    _host?.Resize(_cols, _rows);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] web message parse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyResizeFromMessage(JsonElement root)
    {
        if (root.TryGetProperty("cols", out var c) && c.TryGetInt32(out var cols) && cols > 0)
            _cols = (short)Math.Min(cols, short.MaxValue);
        if (root.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rows) && rows > 0)
            _rows = (short)Math.Min(rows, short.MaxValue);
    }

    private void FlushPending()
    {
        List<string> toFlush;
        lock (_pendingSync)
        {
            _webReady = true;
            toFlush = new List<string>(_pendingOutput);
            _pendingOutput.Clear();
        }
        foreach (var chunk in toFlush)
            PostToWeb("out", chunk);
        // Match the pseudo-console to the renderer's fitted size.
        _host?.Resize(_cols, _rows);
    }

    private void PostToWeb(string type, string data)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { type, data });
            Web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] PostWebMessage failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Tear down the managed ConPTY host (call on tab close). The control
    /// owns the host, so this is where the child process is actually killed —
    /// the session's Dispose deliberately does not (mirrors ConPty ownership).</summary>
    public void Shutdown()
    {
        try { _host?.Dispose(); } catch { }
        _host = null;
    }

    /// <summary>Focus the terminal (forwards to xterm.js term.focus()).</summary>
    public void FocusTerminal()
    {
        try
        {
            Web.Focus();
            if (_webReady) Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"focus\"}");
        }
        catch { }
    }
}
