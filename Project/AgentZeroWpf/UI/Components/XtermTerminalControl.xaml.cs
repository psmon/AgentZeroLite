using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Agent.Common.Services;
using AgentZeroWpf.Services;
using Microsoft.Web.WebView2.Core;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// A terminal tab rendered by xterm.js inside a WebView2, driven by a
/// <see cref="ManagedConPtyHost"/>. The terminal's UI control.
///
/// A normal WPF element with no HwndHost airspace, so WPF overlays (approval
/// toasts, wedge banners) render above it — which the native control it replaced
/// could never do.
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

    /// <summary>Renderer-reported column count (xterm fit addon). Feeds the
    /// host-side link detector's soft-wrap heuristic.</summary>
    public int Columns => _cols;

    /// <summary>
    /// Where the renderer's viewport snapshots go. The session is created after
    /// this control (see MainWindow.InitializeWebViewTerminal), so snapshots that
    /// arrive before then are simply dropped - the session falls back to the tail
    /// of the stream until the next one lands, 250 ms later at worst.
    /// </summary>
    public WebViewXtermTerminalSession? Session { get; set; }

    /// <summary>
    /// Raised when the user clicks inside the terminal. The renderer lives in a
    /// WebView2 child HWND, so a click there produces no WPF input event and the
    /// dock manager never learns that this tab is the one being used. The host
    /// listens and activates the owning document.
    /// </summary>
    public event EventHandler? TerminalClicked;

    /// <summary>Log the first snapshot only — after that they arrive every 250 ms
    /// while output flows, and the interesting question is whether any arrive at all.</summary>
    private bool _screenSeen;

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
            var webgl = TerminalSettingsStore.Load().UseWebGlRenderer ? "?webgl=1" : "";
            Web.CoreWebView2.Navigate($"https://{VirtualHost}/index.html{webgl}");
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

    /// <summary>
    /// Synchronized output (DEC 2026), held here because xterm.js does not implement
    /// it. An Ink TUI wraps each repaint in it to say "do not show the middle of
    /// this"; without it the clear, the home and the redraw each reach the screen
    /// separately and the cursor is visibly somewhere new every time.
    /// </summary>
    private readonly Agent.Common.Services.SynchronizedOutputBuffer _sync = new();

    /// <summary>
    /// A frame that never closes must not freeze the terminal. Real terminals give
    /// up after a beat; so does this.
    /// </summary>
    private bool _syncReported;
    private System.Windows.Threading.DispatcherTimer? _syncTimeout;
    private static readonly TimeSpan SyncFrameTimeout = TimeSpan.FromMilliseconds(150);

    // ConPTY output → xterm.js. Runs on the host read thread → marshal to UI
    // (CoreWebView2 is STA-bound). Buffer until the renderer is ready.
    private void OnHostOutput(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // One frame in, one write out — the renderer then paints it once.
            var ready = _sync.Append(chunk);
            ArmSyncTimeout(_sync.IsBuffering);

            // Logged once: does this program actually use synchronized output? "The
            // cursor still wanders" has two very different answers depending.
            if (!_syncReported && _sync.FramesCoalesced > 0)
            {
                _syncReported = true;
                AppLogger.Log("[Xterm] synchronized output in use (DEC 2026) — frames are coalesced");
            }
            if (ready.Length == 0) return;

            lock (_pendingSync)
            {
                if (!_webReady)
                {
                    _pendingOutput.Add(ready);
                    return;
                }
            }
            PostToWeb("out", ready);
        }));
    }

    private void ArmSyncTimeout(bool buffering)
    {
        if (!buffering)
        {
            _syncTimeout?.Stop();
            return;
        }
        if (_syncTimeout is null)
        {
            _syncTimeout = new System.Windows.Threading.DispatcherTimer { Interval = SyncFrameTimeout };
            _syncTimeout.Tick += (_, _) =>
            {
                _syncTimeout!.Stop();
                var held = _sync.Flush();
                if (held.Length == 0) return;
                AppLogger.Log($"[Xterm] synchronized frame did not close within " +
                              $"{SyncFrameTimeout.TotalMilliseconds:0} ms; releasing {held.Length} chars");
                lock (_pendingSync)
                {
                    if (!_webReady) { _pendingOutput.Add(held); return; }
                }
                PostToWeb("out", held);
            };
        }
        _syncTimeout.Stop();
        _syncTimeout.Start();
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
                    PostAppearance();      // before the flush, so buffered output lands already styled
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
                case "screen":
                    // The renderer is the terminal emulator, so it is the only thing
                    // that knows what is on screen. See TerminalConsoleBuffer for why
                    // that is not the same question as "what came out of the pipe".
                    var screen = root.TryGetProperty("data", out var scr) ? scr.GetString() : null;
                    Session?.SetScreenSnapshot(screen);
                    if (!_screenSeen)
                    {
                        _screenSeen = true;
                        AppLogger.Log($"[Xterm] first viewport snapshot | chars={screen?.Length ?? 0} " +
                                      $"bound={(Session is null ? "no session yet" : "session")}");
                    }
                    break;
                case "renderer":
                    AppLogger.Log(
                        $"[Xterm] renderer={(root.TryGetProperty("name", out var rn) ? rn.GetString() : "?")}" +
                        (root.TryGetProperty("reason", out var rr) ? $" ({rr.GetString()})" : ""));
                    break;
                case "fontstatus":
                    // Reported once per config apply. "loaded=false" means the stack
                    // fell through to a fallback - the terminal still works, it just
                    // is not the font that was asked for.
                    AppLogger.Log(
                        $"[Xterm] font | family={(root.TryGetProperty("family", out var ff) ? ff.GetString() : "?")} " +
                        $"loaded={(root.TryGetProperty("loaded", out var fl) && fl.ValueKind == JsonValueKind.True)} " +
                        $"cellWidth={(root.TryGetProperty("cellWidth", out var cw) ? cw.ToString() : "?")}");
                    break;
                case "activate":
                    TerminalClicked?.Invoke(this, EventArgs.Empty);
                    break;
                case "link":
                    // Ctrl+click on a URL inside xterm.js (web-links addon /
                    // OSC 8). Open in the user's default browser — scheme is
                    // validated by the opener, never trust the page blindly.
                    if (root.TryGetProperty("url", out var urlEl))
                        ExternalLinkOpener.TryOpen(urlEl.GetString(), "xterm-click");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] web message parse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Hands the renderer the appearance from <see cref="TerminalSettings"/>.
    /// Appearance is owned in C# and travels as a message, which is why the font is a
    /// CSS stack rather than an installed family — the renderer is a browser, so the
    /// shipped JetBrains Mono and any fallback the user names both just work.
    /// </summary>
    public void PostAppearance()
    {
        try
        {
            var s = TerminalSettingsStore.Load();
            var t = s.EffectiveTheme;
            PostJsonToWeb(new
            {
                type = "config",
                fontFamily = s.EffectiveFontFamily,
                fontSize = s.EffectiveFontSize,
                lineHeight = s.EffectiveLineHeight,
                cursorBlink = s.CursorBlink,
                theme = new
                {
                    background = t.Background,
                    foreground = t.Foreground,
                    cursor = t.Cursor,
                    selectionBackground = t.SelectionBackground,
                    black = t.Black,
                    red = t.Red,
                    green = t.Green,
                    yellow = t.Yellow,
                    blue = t.Blue,
                    magenta = t.Magenta,
                    cyan = t.Cyan,
                    white = t.White,
                    brightBlack = t.BrightBlack,
                    brightRed = t.BrightRed,
                    brightGreen = t.BrightGreen,
                    brightYellow = t.BrightYellow,
                    brightBlue = t.BrightBlue,
                    brightMagenta = t.BrightMagenta,
                    brightCyan = t.BrightCyan,
                    brightWhite = t.BrightWhite,
                },
            });
            AppLogger.Log($"[Xterm] appearance | font=\"{s.EffectiveFontFamily}\" " +
                          $"size={s.EffectiveFontSize} lineHeight={s.EffectiveLineHeight:0.##} " +
                          $"theme={s.ThemeName} bg={t.Background}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] appearance failed: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>Post an arbitrary message object; <see cref="PostToWeb"/> is the
    /// two-field shorthand for the hot path.</summary>
    private void PostJsonToWeb(object message)
    {
        try
        {
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] PostWebMessage failed: {ex.GetType().Name}: {ex.Message}");
        }
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
