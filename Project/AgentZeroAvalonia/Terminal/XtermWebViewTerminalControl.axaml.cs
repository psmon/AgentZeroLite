using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Services;
using AgentZeroAvalonia.Services;

namespace AgentZeroAvalonia.Terminal;

/// <summary>A chord the renderer should intercept and report by name (M0036 fills the table).</summary>
public sealed record HotkeyBinding(string Name, string Key, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Meta = false);

/// <summary>
/// One terminal: xterm.js in a <see cref="NativeWebView"/>, driven by an
/// <see cref="IPtyHost"/> (M0035). A port of the WPF <c>XtermTerminalControl</c> with the
/// transport swapped — assets come from <see cref="LocalAssetServer"/>, output goes in
/// through <c>InvokeScript</c> as base64 batches, input comes back through
/// <c>WebMessageReceived</c>.
///
/// Ownership is the WPF host's: the control owns the PTY host, so closing the tab (or
/// <see cref="Shutdown"/>) is what kills the child; the session's Dispose does not.
/// </summary>
public partial class XtermWebViewTerminalControl : UserControl
{
    private const int MaxPendingChars = 4 * 1024 * 1024;
    private static readonly TimeSpan SyncFrameTimeout = TimeSpan.FromMilliseconds(150);

    private IPtyHost? _host;
    private TerminalLaunchSpec? _spec;
    private bool _webReady;
    private bool _sourceSet;
    private readonly List<string> _pendingOutput = new();
    private int _pendingChars;

    // Renderer-reported viewport size (xterm fit addon).
    private int _cols = 80, _rows = 24;

    private readonly SynchronizedOutputBuffer _sync = new();
    private DispatcherTimer? _syncTimeout;
    private bool _syncReported;
    private bool _screenSeen;
    private int _scriptFailures;

    private readonly StringBuilder _outbox = new();
    private bool _pumping;

    /// <summary>The PTY host once <see cref="StartPty"/> has run.</summary>
    public IPtyHost? PtyHost => _host;

    /// <summary>Renderer-reported column count.</summary>
    public int Columns => _cols;

    /// <summary>What this terminal was launched with — the restart banner relaunches it.</summary>
    public TerminalLaunchSpec? LaunchSpec => _spec;

    /// <summary>Where viewport snapshots and input attempts go. Set by the view after <see cref="StartPty"/>.</summary>
    public XtermTerminalSession? Session { get; set; }

    /// <summary>Chords the renderer intercepts; sent with the appearance.</summary>
    public IReadOnlyList<HotkeyBinding> Hotkeys { get; set; } = Array.Empty<HotkeyBinding>();

    /// <summary>The user clicked inside the renderer — the owner activates this tab.</summary>
    public event EventHandler? TerminalClicked;

    /// <summary>A configured chord was pressed while the renderer had focus.</summary>
    public event EventHandler<string>? HotkeyRequested;

    /// <summary>"Restart terminal" on the banner.</summary>
    public event EventHandler? RestartRequested;

    public XtermWebViewTerminalControl()
    {
        InitializeComponent();
        Web.EnvironmentRequested += OnEnvironmentRequested;
        Web.WebMessageReceived += OnWebMessage;
        Web.NavigationCompleted += (_, e) =>
            AppLogger.Log($"[Xterm] navigation {(e.IsSuccess ? "ok" : "FAILED")} | {e.Request}");
        Web.AdapterCreated += (_, _) => AppLogger.Log($"[Xterm] webview adapter | {Web.AdapterInfo}");
        RestartButton.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_sourceSet) return;
        _sourceSet = true;
        try
        {
            var webgl = TerminalSettingsStore.Load().UseWebGlRenderer ? "webgl=1" : null;
            var url = LocalAssetServer.Shared.UrlFor("index.html", webgl);
            Web.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] WebView navigate failed: {ex.GetType().Name}: {ex.Message}");
            ShowBanner($"Renderer failed to start: {ex.Message}", restart: false);
        }
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs win)
        {
            // Same user-data folder rule as the WPF host: a private, throwaway profile.
            win.UserDataFolder = Path.Combine(Path.GetTempPath(), "AgentZeroLite_Xterm_Avalonia");
        }
#if DEBUG
        e.EnableDevTools = true;
#endif
    }

    // ── PTY ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn the PTY for this tab. Safe before the renderer is ready — output buffers
    /// until it signals <c>ready</c>. Returns null (and shows the reason) on failure.
    /// </summary>
    public IPtyHost? StartPty(TerminalLaunchSpec spec)
    {
        if (_host is not null) return _host;
        _spec = spec;
        try
        {
            var host = PtyHostFactory.Start(spec, _cols, _rows);
            host.Output += OnHostOutput;
            host.Exited += OnHostExited;
            _host = host;
            AppLogger.Log($"[Xterm] pty started | {PtyHostFactory.BackendName} | {host.Diagnostics}");
            return host;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] pty start failed: {ex.GetType().Name}: {ex.Message}");
            ShowBanner($"Could not start '{spec.DisplayName}': {ex.Message}", restart: true);
            return null;
        }
    }

    private void OnHostExited()
    {
        AppLogger.Log($"[Xterm] pty exited | {_spec?.DisplayName ?? "?"} | {_host?.Diagnostics}");
        Dispatcher.UIThread.Post(() => ShowBanner("The process has exited.", restart: true));
    }

    // PTY output → renderer. Runs on the host read thread → marshal to UI, run the
    // DEC 2026 frame buffer there, then coalesce into the outbox.
    private void OnHostOutput(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var ready = _sync.Append(chunk);
            ArmSyncTimeout(_sync.IsBuffering);
            if (!_syncReported && _sync.FramesCoalesced > 0)
            {
                _syncReported = true;
                AppLogger.Log("[Xterm] synchronized output in use (DEC 2026) — frames are coalesced");
            }
            if (ready.Length == 0) return;
            Enqueue(ready);
        });
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
            _syncTimeout = new DispatcherTimer { Interval = SyncFrameTimeout };
            _syncTimeout.Tick += (_, _) =>
            {
                _syncTimeout!.Stop();
                var held = _sync.Flush();
                if (held.Length == 0) return;
                AppLogger.Log($"[Xterm] synchronized frame did not close within {SyncFrameTimeout.TotalMilliseconds:0} ms; releasing {held.Length} chars");
                Enqueue(held);
            };
        }
        _syncTimeout.Stop();
        _syncTimeout.Start();
    }

    private void Enqueue(string text)
    {
        if (!_webReady)
        {
            // Bounded: a tab that never renders must not grow without limit.
            while (_pendingChars + text.Length > MaxPendingChars && _pendingOutput.Count > 0)
            {
                _pendingChars -= _pendingOutput[0].Length;
                _pendingOutput.RemoveAt(0);
            }
            _pendingOutput.Add(text);
            _pendingChars += text.Length;
            return;
        }
        _outbox.Append(text);
        _ = PumpAsync();
    }

    /// <summary>
    /// One writer, in order: whatever accumulated while the previous script ran goes
    /// out as the next batch, so a slow renderer gets fewer, larger writes rather than
    /// a queue of small ones.
    /// </summary>
    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_outbox.Length > 0)
            {
                var text = _outbox.ToString();
                _outbox.Clear();
                foreach (var b64 in XtermMessages.ChunkUtf8Base64(text))
                    await InvokeAsync(XtermMessages.BuildRecvScript(new { type = "out64", data = b64 }));
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task InvokeAsync(string script)
    {
        try
        {
            await Web.InvokeScript(script);
        }
        catch (Exception ex)
        {
            if (_scriptFailures++ < 3)
                AppLogger.Log($"[Xterm] InvokeScript failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Post(object message) => _ = InvokeAsync(XtermMessages.BuildRecvScript(message));

    // ── renderer → host ───────────────────────────────────────────────────────

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        var body = e.Body;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleWebMessage(body));
            return;
        }
        HandleWebMessage(body);
    }

    private void HandleWebMessage(string? body)
    {
        if (!XtermMessages.TryParseInbound(body, out var root))
        {
            AppLogger.Log($"[Xterm] unparseable web message | {(body is null ? "null" : body.Length > 120 ? body[..120] + "…" : body)}");
            return;
        }
        try
        {
            switch (XtermMessages.Type(root))
            {
                case "ready":
                    ApplyResizeFromMessage(root);
                    PostAppearance();      // before the flush, so buffered output lands already styled
                    FlushPending();
                    break;
                case "in":
                    var data = XtermMessages.Str(root, "data");
                    if (!string.IsNullOrEmpty(data))
                    {
                        Session?.NoteInputAttempt("keyboard");
                        _host?.Write(data.AsSpan());
                    }
                    break;
                case "resize":
                    ApplyResizeFromMessage(root);
                    _host?.Resize(_cols, _rows);
                    break;
                case "screen":
                    var screen = XtermMessages.Str(root, "data");
                    Session?.SetScreenSnapshot(screen);
                    if (!_screenSeen)
                    {
                        _screenSeen = true;
                        AppLogger.Log($"[Xterm] first viewport snapshot | chars={screen?.Length ?? 0} bound={(Session is null ? "no session yet" : "session")}");
                    }
                    break;
                case "renderer":
                    AppLogger.Log($"[Xterm] renderer={XtermMessages.Str(root, "name") ?? "?"}" +
                                  (XtermMessages.Str(root, "reason") is { } reason ? $" ({reason})" : ""));
                    break;
                case "fontstatus":
                    AppLogger.Log($"[Xterm] font | family={XtermMessages.Str(root, "family") ?? "?"} " +
                                  $"loaded={(root.TryGetProperty("loaded", out var fl) && fl.ValueKind == JsonValueKind.True)} " +
                                  $"cellWidth={(root.TryGetProperty("cellWidth", out var cw) ? cw.ToString() : "?")}");
                    break;
                case "activate":
                    TerminalClicked?.Invoke(this, EventArgs.Empty);
                    break;
                case "link":
                    OpenLink(XtermMessages.Str(root, "url"));
                    break;
                case "hotkey":
                    var name = XtermMessages.Str(root, "name");
                    if (!string.IsNullOrEmpty(name)) HotkeyRequested?.Invoke(this, name);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] web message failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https"))
        {
            AppLogger.Log($"[Xterm] link refused (scheme {uri.Scheme})");
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        AppLogger.Log($"[Xterm] link → default browser | {uri}");
        _ = top.Launcher.LaunchUriAsync(uri);
    }

    private void ApplyResizeFromMessage(JsonElement root)
    {
        var cols = XtermMessages.Int(root, "cols", 0);
        var rows = XtermMessages.Int(root, "rows", 0);
        if (cols > 0) _cols = Math.Min(cols, short.MaxValue);
        if (rows > 0) _rows = Math.Min(rows, short.MaxValue);
    }

    private void FlushPending()
    {
        _webReady = true;
        foreach (var chunk in _pendingOutput) _outbox.Append(chunk);
        _pendingOutput.Clear();
        _pendingChars = 0;
        _ = PumpAsync();
        // Match the pseudo-console to the renderer's fitted size.
        _host?.Resize(_cols, _rows);
    }

    /// <summary>Hand the renderer the appearance from <see cref="TerminalSettings"/> (+ the hotkey table).</summary>
    public void PostAppearance()
    {
        try
        {
            var s = TerminalSettingsStore.Load();
            var t = s.EffectiveTheme;
            Post(new
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
                    black = t.Black, red = t.Red, green = t.Green, yellow = t.Yellow,
                    blue = t.Blue, magenta = t.Magenta, cyan = t.Cyan, white = t.White,
                    brightBlack = t.BrightBlack, brightRed = t.BrightRed, brightGreen = t.BrightGreen,
                    brightYellow = t.BrightYellow, brightBlue = t.BrightBlue, brightMagenta = t.BrightMagenta,
                    brightCyan = t.BrightCyan, brightWhite = t.BrightWhite,
                },
                hotkeys = Hotkeys.Select(h => new { name = h.Name, key = h.Key, ctrl = h.Ctrl, alt = h.Alt, shift = h.Shift, meta = h.Meta }).ToArray(),
            });
            AppLogger.Log($"[Xterm] appearance | font=\"{s.EffectiveFontFamily}\" size={s.EffectiveFontSize} " +
                          $"lineHeight={s.EffectiveLineHeight:0.##} theme={s.ThemeName} hotkeys={Hotkeys.Count}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Xterm] appearance failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── health banner ─────────────────────────────────────────────────────────

    /// <summary>Reflect the session's health FSM (Alive → Stale → Dead) on the banner.</summary>
    public void ApplyHealth(TerminalHealthState state)
    {
        switch (state)
        {
            case TerminalHealthState.Dead:
                ShowBanner("No response to input — the terminal looks wedged.", restart: true);
                break;
            case TerminalHealthState.Stale:
                ShowBanner("Waiting for the terminal to respond…", restart: false);
                break;
            default:
                if (_host?.IsRunning == true) HideBanner();
                break;
        }
    }

    private void ShowBanner(string text, bool restart)
    {
        HealthText.Text = text;
        RestartButton.IsVisible = restart;
        HealthBanner.IsVisible = true;
    }

    private void HideBanner() => HealthBanner.IsVisible = false;

    // ── lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Kill the child (tab close / restart). The control owns the PTY host.</summary>
    public void Shutdown()
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            host.Output -= OnHostOutput;
            host.Exited -= OnHostExited;
            try { host.Dispose(); } catch { }
        }
        _syncTimeout?.Stop();
    }

    /// <summary>Focus the renderer (forwards to xterm.js <c>term.focus()</c>).</summary>
    public void FocusTerminal()
    {
        try
        {
            Web.Focus();
            if (_webReady) Post(new { type = "focus" });
        }
        catch { }
    }

    /// <summary>Clear the renderer's viewport.</summary>
    public void ClearScreen()
    {
        if (_webReady) Post(new { type = "clear" });
    }
}
