using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Akka.Actor;
using AgentZeroWpf.Actors;
using AgentZeroWpf.Module;
using AgentZeroWpf.Services;

namespace AgentZeroWpf.UI.APP;

public partial class MainWindow : Window
{
    // ConPTY 터미널은 EasyWindowsTerminalControl이 관리

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnGlobalKeyDown;
        txtVersion.Text = AppVersionProvider.GetDisplayVersion();

        // Mirror AppLogger entries into the embedded LOG tab
        AppLogger.EntryAdded += OnAppLogEntryForBottomTab;
    }

    /// <summary>Mirror an AppLogger entry to the embedded LOG tab (bottom panel).</summary>
    private void OnAppLogEntryForBottomTab(string line)
    {
        if (txtBottomLog is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (txtBottomLog.Text.Length > 100_000)
                txtBottomLog.Text = txtBottomLog.Text[^50_000..]; // trim from the front

            bool atBottom = txtBottomLog.VerticalOffset
                           >= txtBottomLog.ExtentHeight - txtBottomLog.ViewportHeight - 2;
            txtBottomLog.AppendText(line + Environment.NewLine);
            if (atBottom) txtBottomLog.ScrollToEnd();
        });
    }

    /// <summary>
    /// Global shortcut handler.
    /// <list type="bullet">
    /// <item>Ctrl+\`     → show/hide the bot (panel if embedded, window if floating)</item>
    /// <item>Ctrl+Shift+\` → embed↔float transition</item>
    /// </list>
    /// </summary>
    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // Ctrl+Shift + 1 / 2 / 3 → switch the bottom panel tab
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.D1 || e.Key == Key.NumPad1) { SwitchBottomTab(BottomTab.Bot);    e.Handled = true; return; }
            if (e.Key == Key.D2 || e.Key == Key.NumPad2) { SwitchBottomTab(BottomTab.Output); e.Handled = true; return; }
            if (e.Key == Key.D3 || e.Key == Key.NumPad3) { SwitchBottomTab(BottomTab.Log);    e.Handled = true; return; }
        }

        if (e.Key != Key.OemTilde) return;

        // Ctrl+Shift+\` → embed/undock (checked first because Shift+Ctrl includes Ctrl)
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleBotEmbedded();
            e.Handled = true;
            return;
        }

        // Ctrl+\` → toggle visibility
        if (mods == ModifierKeys.Control)
        {
            OnSidebarBotClick(btnActivityBot, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>
    /// Moves the bot's content from the floating window into the main window's
    /// <see cref="BotDockHost"/>. The bot window itself is hidden but not disposed
    /// so it can be restored on undock. Adjusts row heights to reveal the dock area.
    /// </summary>
    private void EmbedBot()
    {
        if (_botWindow is null) return;
        if (BotDockHost is null || BotDockArea is null) return;

        var content = _botWindow.DetachContent();
        if (content is null) return;

        _botWindow.SetEmbeddedMode(true);
        _botWindow.Hide();

        BotDockHost.Content = content;
        BotDockRow.Height = new GridLength(280, GridUnitType.Pixel);
        BotSplitterRow.Height = new GridLength(6, GridUnitType.Pixel);

        _isBotEmbedded = true;
        statusLabel.Text = "BOT EMBEDDED";
        UpdateStatusBarBot();
    }

    /// <summary>
    /// Returns the bot's content to its owning window, collapses the embed
    /// row and shows the window so it becomes a regular top-level window.
    /// The float window keeps its own last position/size — no magnet to main.
    /// </summary>
    private void UndockBot()
    {
        if (_botWindow is null) return;
        if (BotDockHost?.Content is not FrameworkElement content) return;

        BotDockHost.Content = null;
        _botWindow.AttachContent(content);
        _botWindow.SetEmbeddedMode(false);

        BotDockRow.Height = new GridLength(0);
        BotSplitterRow.Height = new GridLength(0);

        // Center on first undock if the window has no prior position (0,0 defaults)
        if (_botWindow.Left == 0 && _botWindow.Top == 0)
            _botWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _botWindow.Show();
        _botWindow.Activate();

        _isBotEmbedded = false;
        statusLabel.Text = "BOT FLOATING";
        UpdateStatusBarBot();
    }

    /// <summary>
    /// Flips between embedded (inside main) and floating (separate window) modes.
    /// Creates the bot window lazily if it hasn't been opened yet.
    /// </summary>
    private void ToggleBotEmbedded()
    {
        // Lazy-open the bot on first toggle so users who never summoned it can still use Ctrl+Shift+\`
        if (_botWindow is null || !_botWindow.IsLoaded)
        {
            OnSidebarBotClick(btnActivityBot, new RoutedEventArgs());
            return; // OnSidebarBotClick's creation path honors _isBotEmbedded for initial placement
        }

        if (_isBotEmbedded)
            UndockBot();
        else
            EmbedBot();
    }

    private void OnBotUndockClick(object sender, RoutedEventArgs e) => UndockBot();

    /// <summary>
    /// Called by the floating <see cref="AgentBotWindow"/>'s title-bar "embed" button.
    /// Re-embeds the bot content into this main window's layout.
    /// </summary>
    public void EmbedBotFromBot()
    {
        if (!_isBotEmbedded) EmbedBot();
    }

    // =========================================================================
    //  Bottom Panel Tabs (Pencil design: BOT / OUTPUT / LOG / NOTE / HARNESS)
    // =========================================================================

    private enum BottomTab { Bot, Output, Log, Note }

    private void OnBottomTabBotClick(object sender, RoutedEventArgs e) => SwitchBottomTab(BottomTab.Bot);
    private void OnBottomTabOutputClick(object sender, RoutedEventArgs e) => SwitchBottomTab(BottomTab.Output);
    private void OnBottomTabLogClick(object sender, RoutedEventArgs e) => SwitchBottomTab(BottomTab.Log);
    private void OnBottomTabNoteClick(object sender, RoutedEventArgs e) => OpenNoteTab();

    private void SwitchBottomTab(BottomTab tab)
    {
        EnsureBottomPanelVisible();

        BotDockHost.Visibility     = tab == BottomTab.Bot     ? Visibility.Visible : Visibility.Collapsed;
        txtBottomOutput.Visibility = tab == BottomTab.Output  ? Visibility.Visible : Visibility.Collapsed;
        txtBottomLog.Visibility    = tab == BottomTab.Log     ? Visibility.Visible : Visibility.Collapsed;
        NoteHost.Visibility        = tab == BottomTab.Note    ? Visibility.Visible : Visibility.Collapsed;

        StyleBottomTabButton(btnBottomTabBot,     tab == BottomTab.Bot);
        StyleBottomTabButton(btnBottomTabOutput,  tab == BottomTab.Output);
        StyleBottomTabButton(btnBottomTabLog,     tab == BottomTab.Log);
        StyleBottomTabButton(btnBottomTabNote,    tab == BottomTab.Note);
    }

    /// <summary>Ensure the embedded bottom panel row is expanded and visible.</summary>
    private void EnsureBottomPanelVisible()
    {
        if (!_isBotEmbedded) return;
        if (BotDockRow.Height.Value > 0) return;
        BotDockRow.Height = new GridLength(280, GridUnitType.Pixel);
        BotSplitterRow.Height = new GridLength(6, GridUnitType.Pixel);
    }

    private void StyleBottomTabButton(Button btn, bool selected)
    {
        btn.Background = selected
            ? (System.Windows.Media.Brush)FindResource("EditorBg")
            : System.Windows.Media.Brushes.Transparent;
        btn.Foreground = selected
            ? (System.Windows.Media.Brush)FindResource("CyberCyanBrush")
            : (System.Windows.Media.Brush)FindResource("TextDim");
        btn.BorderThickness = selected ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        btn.BorderBrush = selected
            ? (System.Windows.Media.Brush)FindResource("CyberCyanBrush")
            : null;
    }

    /// <summary>Updates the session filter string and rebuilds the SESSIONS panel.</summary>
    private void OnSessionFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        _sessionFilter = tb.Text ?? "";
        RefreshSessionList();
    }

    /// <summary>Formats a timestamp as a short relative-time hint ("now", "2m", "1h", "3d").</summary>
    private static string FormatRelativeTime(DateTime when)
    {
        var delta = DateTime.Now - when;
        if (delta.TotalSeconds < 45) return "now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h";
        return $"{(int)delta.TotalDays}d";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);

        // DWM 다크모드 타이틀바 적용 (흰색 상단 잔상 제거)
        ThemeHelper.ApplyDarkTitleBar(helper.Handle);

        // Hook WndProc for WM_COPYDATA IPC
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);

        Activated += OnWindowActivated;

        // Tick every 60s so "2m ago" labels stay fresh without user interaction.
        _sessionTickTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60),
        };
        _sessionTickTimer.Tick += (_, _) => RefreshSessionList();
        _sessionTickTimer.Start();

        // Wire up settings/CLI events BEFORE DB init so that a DB failure
        // never leaves the Settings close (X) button orphaned.
        SettingsPanel.CliDefinitionsChanged += RebuildCliContextMenu;
        SettingsPanel.OnboardingDismissed += () =>
        {
            SwitchToCliPanel();
            if (_activeGroupIndex >= 0) ActivateGroup(_activeGroupIndex);
        };

        // DB 초기화 + 상태 복원
        try
        {
            AppDbContext.InitializeDatabase();
            RestoreWindowState();
            RebuildCliContextMenu();
            RestoreCliGroups();
            InitializeDockManager();

            // ConPTY 터미널은 마우스/키보드를 네이티브 처리
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[DB] 초기화 오류: {ex.Message}");
        }
    }

    // ApplyDarkTitleBar → ThemeHelper.ApplyDarkTitleBar 로 이동됨

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        var tabs = _consoleTabs;
        int ati = _activeConsoleTab;
        if (CliPanel.Visibility == Visibility.Visible &&
            ati >= 0 && ati < tabs.Count)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (ati >= 0 && ati < tabs.Count && tabs[ati].Terminal is { } t)
                    FocusTerminal(t);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isAppClosing = true;
        _sessionTickTimer?.Stop();
        AppLogger.EntryAdded -= OnAppLogEntryForBottomTab;

        // DB에 상태 저장 (프로세스 kill 전에 수행)
        try
        {
            SaveWindowState();
            SaveCliGroups();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[DB] 저장 오류: {ex.Message}");
        }

        _botWindow?.Close();

        // Stop all embedded terminal processes
        foreach (var group in _cliGroups)
            foreach (var tab in group.Tabs)
            {
                try { tab.Terminal?.ConPTYTerm?.StopExternalTermOnly(); } catch { }
            }
    }

    // =========================================================================
    //  Log panel integration (replaces LogForm)
    // =========================================================================


    // =========================================================================
    //  Text capture
    // =========================================================================


    // =========================================================================
    //  WM_COPYDATA IPC — receive commands from CLI
    // =========================================================================

    private const int CYCOPYDATA_COMMAND = 0x414C; // "AL" marker — AgentZero Lite (PRO uses "AG" 0x4147)

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)NativeMethods.WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
            if (cds.dwData == (IntPtr)CYCOPYDATA_COMMAND && cds.cbData > 0)
            {
                string json = Marshal.PtrToStringUTF8(cds.lpData, cds.cbData) ?? "";
                AppLogger.Log($"[IPC] WM_COPYDATA 수신 | {json}");
                HandleCliCommand(json);
                handled = true;
                return (IntPtr)1;
            }
        }

        return IntPtr.Zero;
    }

    private void HandleCliCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string command = root.GetProperty("command").GetString() ?? "";
            if (command == "status")
            {
                WriteStatusResponse();
                return;
            }

            if (command == "terminal-list")
            {
                HandleTerminalList();
                return;
            }

            if (command == "terminal-send")
            {
                int groupIdx = root.GetProperty("group_index").GetInt32();
                int tabIdx = root.GetProperty("tab_index").GetInt32();
                string text = root.GetProperty("text").GetString() ?? "";
                HandleTerminalSend(groupIdx, tabIdx, text);
                return;
            }

            if (command == "terminal-key")
            {
                int groupIdx = root.GetProperty("group_index").GetInt32();
                int tabIdx = root.GetProperty("tab_index").GetInt32();
                string key = root.GetProperty("key").GetString() ?? "";
                HandleTerminalKey(groupIdx, tabIdx, key);
                return;
            }

            if (command == "terminal-read")
            {
                int groupIdx = root.GetProperty("group_index").GetInt32();
                int tabIdx = root.GetProperty("tab_index").GetInt32();
                int lastN = root.TryGetProperty("last", out var lp) ? lp.GetInt32() : 0;
                HandleTerminalRead(groupIdx, tabIdx, lastN);
                return;
            }

            if (command == "bot-chat")
            {
                string from = root.TryGetProperty("from", out var fp) ? fp.GetString() ?? "CLI" : "CLI";
                string message = root.GetProperty("message").GetString() ?? "";
                HandleBotChat(from, message);
                return;
            }

            AppLogger.Log($"[IPC] 알 수 없는 명령: {command}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[IPC] 명령 파싱 오류: {ex.Message}");
        }
    }

    private const string StatusMmfName = "AgentZeroLite_Status_Response";
    private const int StatusMmfSize = 8192;

    private void WriteStatusResponse()
    {
        string statusBar = statusLabel.Text;
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"status_bar\":\"{EscapeJson(statusBar)}\"");
        sb.Append(",\"groups\":");
        sb.Append(_cliGroups.Count);
        sb.Append('}');
        IpcMemoryMappedResponseWriter.WriteJson(StatusMmfName, StatusMmfSize, sb.ToString(), "[IPC] Status 응답 쓰기 오류");
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break; // strip CR
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // =========================================================================
    //  IPC: terminal-list — return active terminal sessions
    // =========================================================================

    private const string TerminalListMmfName = "AgentZeroLite_TerminalList_Response";
    private const int TerminalListMmfSize = 32768;

    private void HandleTerminalList()
    {
        string json = CliTerminalIpcHelper.BuildTerminalListJson(_cliGroups, EscapeJson);
        IpcMemoryMappedResponseWriter.WriteJson(TerminalListMmfName, TerminalListMmfSize, json, "[IPC] terminal-list 응답 쓰기 오류");

        // Detailed session inventory for diagnosis — always emitted regardless of build config.
        int totalTabs = 0;
        var inv = new StringBuilder();
        for (int gi = 0; gi < _cliGroups.Count; gi++)
        {
            var g = _cliGroups[gi];
            for (int ti = 0; ti < g.Tabs.Count; ti++)
            {
                var t = g.Tabs[ti];
                totalTabs++;
                var sess = t.Session as ConPtyTerminalSession;
                string id = sess?.InternalId ?? "-";
                string ptyRef = sess is not null ? $"0x{sess.PtyRefHash:X8}" : "-";
                bool running = sess?.IsRunning ?? false;
                int outLen = sess?.OutputLength ?? -1;
                if (inv.Length > 0) inv.Append(" | ");
                inv.Append($"[{gi}:{ti}] label=\"{g.DisplayName}/{t.Title}\" id={id} pty_ref={ptyRef} running={running} out_len={outLen}");
            }
        }
        AppLogger.Log($"[IPC] terminal-list | groups={_cliGroups.Count} tabs={totalTabs} bytes={json.Length}");
        if (inv.Length > 0)
            AppLogger.Log($"[IPC] terminal-list inventory | {inv}");
    }

    // =========================================================================
    //  IPC: terminal-send — send text to a specific terminal
    // =========================================================================

    private const string TerminalSendMmfName = "AgentZeroLite_TerminalSend_Response";
    private const int TerminalSendMmfSize = 1024;

    private void HandleTerminalSend(int groupIdx, int tabIdx, string text)
    {
        string resultJson;
        if (!CliTerminalIpcHelper.TryResolveSession(
                _cliGroups,
                groupIdx,
                tabIdx,
                $"Invalid group_index {groupIdx}. Use terminal-list to see available groups.",
                $"Invalid tab_index {tabIdx} in group {groupIdx}. Use terminal-list to see available tabs.",
                $"Terminal [{groupIdx}:{tabIdx}] is not started. Activate the tab in AgentZero first.",
                out _,
                out _,
                out var session,
                out var errorJson))
        {
            resultJson = errorJson!;
        }
        else
        {
            var cps = session as ConPtyTerminalSession;
            int outLenBefore = session!.OutputLength;
            string label = session.SessionId;
            string id = cps?.InternalId ?? "-";
            string ptyRef = cps is not null ? $"0x{cps.PtyRefHash:X8}" : "-";
            bool running = session.IsRunning;
            string preview = text.Length <= 30 ? text : text[..30] + "…";

            if (!running)
            {
                resultJson = $"{{\"ok\":false,\"error\":\"Terminal [{groupIdx}:{tabIdx}] session is not running (PTY dead). id={id}\"}}";
                AppLogger.Log($"[IPC] terminal-send REJECTED [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} pty_ref={ptyRef} running=false");
            }
            else
            {
                try
                {
                    session.WriteAndSubmit(text);
                    resultJson = $"{{\"ok\":true,\"group_index\":{groupIdx},\"tab_index\":{tabIdx},\"sent_length\":{text.Length}}}";
                    AppLogger.Log($"[IPC] terminal-send [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} pty_ref={ptyRef} running={running} len={text.Length} out_len_before={outLenBefore} preview=\"{preview}\"");
                }
                catch (Exception ex)
                {
                    resultJson = $"{{\"ok\":false,\"error\":\"Write failed: {EscapeJson(ex.Message)}\"}}";
                    AppLogger.Log($"[IPC] terminal-send FAILED [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} error={ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        IpcMemoryMappedResponseWriter.WriteJson(TerminalSendMmfName, TerminalSendMmfSize, resultJson, "[IPC] terminal-send 응답 쓰기 오류");
    }

    // =========================================================================
    //  IPC: terminal-key — send raw key sequence to a terminal
    // =========================================================================

    private void HandleTerminalKey(int groupIdx, int tabIdx, string key)
    {
        string resultJson;
        if (!CliTerminalIpcHelper.TryResolveSession(
                _cliGroups,
                groupIdx,
                tabIdx,
                $"Invalid group_index {groupIdx}.",
                $"Invalid tab_index {tabIdx} in group {groupIdx}.",
                $"Terminal [{groupIdx}:{tabIdx}] is not started.",
                out _,
                out _,
                out var session,
                out var errorJson))
        {
            resultJson = errorJson!;
        }
        else
        {
            string seq = key switch
            {
                "cr" => "\r",
                "lf" => "\n",
                "crlf" => "\r\n",
                "esc" => "\x1B",
                "tab" => "\t",
                "backspace" => "\x08",
                "del" => "\x7F",
                "ctrlc" => "\x03",
                "ctrld" => "\x04",
                "up" => "\x1B[A",
                "down" => "\x1B[B",
                "right" => "\x1B[C",
                "left" => "\x1B[D",
                _ when key.StartsWith("hex:") => ParseHexKey(key[4..]),
                _ => ""
            };

            if (string.IsNullOrEmpty(seq))
            {
                resultJson = $"{{\"ok\":false,\"error\":\"Unknown key: {EscapeJson(key)}. Use terminal-key --help.\"}}";
            }
            else
            {
                var cps = session as ConPtyTerminalSession;
                string label = session!.SessionId;
                string id = cps?.InternalId ?? "-";
                string ptyRef = cps is not null ? $"0x{cps.PtyRefHash:X8}" : "-";
                bool running = session.IsRunning;

                if (!running)
                {
                    resultJson = $"{{\"ok\":false,\"error\":\"Terminal [{groupIdx}:{tabIdx}] session is not running (PTY dead). id={id}\"}}";
                    AppLogger.Log($"[IPC] terminal-key REJECTED [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} pty_ref={ptyRef} running=false key={key}");
                }
                else
                {
                    try
                    {
                        session.Write(seq.AsSpan());
                        resultJson = $"{{\"ok\":true,\"group_index\":{groupIdx},\"tab_index\":{tabIdx},\"key\":\"{EscapeJson(key)}\"}}";
                        AppLogger.Log($"[IPC] terminal-key [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} pty_ref={ptyRef} running={running} key={key} seq_bytes={seq.Length}");
                    }
                    catch (Exception ex)
                    {
                        resultJson = $"{{\"ok\":false,\"error\":\"Key write failed: {EscapeJson(ex.Message)}\"}}";
                        AppLogger.Log($"[IPC] terminal-key FAILED [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} error={ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        IpcMemoryMappedResponseWriter.WriteJson(TerminalSendMmfName, TerminalSendMmfSize, resultJson, "[IPC] terminal-key 응답 쓰기 오류");
    }

    private static string ParseHexKey(string hex)
    {
        try
        {
            byte b = Convert.ToByte(hex, 16);
            return ((char)b).ToString();
        }
        catch { return ""; }
    }

    // PTY-FREEZE-DIAG: filter for the KEY-NO-ECHO check. Only count keys that
    // a healthy shell would normally echo or respond to. Modifiers/IME-in-flight
    // press events fire on every keystroke and never produce echo on their own,
    // so including them turns the log into noise.
    private static bool IsEchoCandidateKey(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.CapsLock or System.Windows.Input.Key.NumLock
            or System.Windows.Input.Key.Scroll
            or System.Windows.Input.Key.ImeProcessed
            or System.Windows.Input.Key.ImeNonConvert or System.Windows.Input.Key.ImeConvert
            or System.Windows.Input.Key.ImeModeChange or System.Windows.Input.Key.HangulMode
            or System.Windows.Input.Key.System
            or System.Windows.Input.Key.Tab
            => false,
        _ => true,
    };

    // =========================================================================
    //  IPC: terminal-read — read console output text from a terminal
    // =========================================================================

    private const string TerminalReadMmfName = "AgentZeroLite_TerminalRead_Response";
    private const int TerminalReadMmfSize = 65536;

    private void HandleTerminalRead(int groupIdx, int tabIdx, int lastN)
    {
        string resultJson;
        if (!CliTerminalIpcHelper.TryResolveSession(
                _cliGroups,
                groupIdx,
                tabIdx,
                $"Invalid group_index {groupIdx}.",
                $"Invalid tab_index {tabIdx} in group {groupIdx}.",
                $"Terminal [{groupIdx}:{tabIdx}] is not started.",
                out _,
                out _,
                out var session,
                out var errorJson))
        {
            resultJson = errorJson!;
        }
        else
        {
            var cps = session as ConPtyTerminalSession;
            string label = session!.SessionId;
            string id = cps?.InternalId ?? "-";
            string ptyRef = cps is not null ? $"0x{cps.PtyRefHash:X8}" : "-";
            bool running = session.IsRunning;

            try
            {
                string text;
                if (lastN > 0)
                {
                    int totalLen = session.OutputLength;
                    int start = Math.Max(0, totalLen - lastN);
                    int len = totalLen - start;
                    text = len > 0 ? session.ReadOutput(start, len) : "";
                }
                else
                {
                    text = session.GetConsoleText();
                }

                text = ApprovalParser.StripAnsiCodes(text);
                resultJson = $"{{\"ok\":true,\"group_index\":{groupIdx},\"tab_index\":{tabIdx},\"length\":{text.Length},\"text\":\"{EscapeJson(text)}\"}}";
                int totalOutLen = session.OutputLength;
                AppLogger.Log($"[IPC] terminal-read [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} pty_ref={ptyRef} running={running} last_n={lastN} out_len={totalOutLen} returned={text.Length}");
            }
            catch (Exception ex)
            {
                resultJson = $"{{\"ok\":false,\"error\":\"Read failed: {EscapeJson(ex.Message)}\"}}";
                AppLogger.Log($"[IPC] terminal-read FAILED [{groupIdx}:{tabIdx}] | label=\"{label}\" id={id} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        IpcMemoryMappedResponseWriter.WriteJson(TerminalReadMmfName, TerminalReadMmfSize, resultJson, "[IPC] terminal-read 응답 쓰기 오류");
    }

    // =========================================================================
    //  IPC: bot-chat — send chat message to AgentBot
    // =========================================================================

    private const string BotChatMmfName = "AgentZeroLite_BotChat_Response";
    private const int BotChatMmfSize = 1024;

    // DONE 파싱: "DONE(from, msg)" 또는 "DONE(msg)" 두 형태 모두 지원
    private static readonly System.Text.RegularExpressions.Regex DonePatternWithFrom = new(
        @"^DONE\((?<from>[^,]+),\s*(?<msg>.+)\)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex DonePatternSimple = new(
        @"^DONE\((?<msg>.+)\)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

    private void HandleBotChat(string from, string message)
    {
        string resultJson;

        // When AgentBot is docked into MainWindow (the default), `_botWindow` is created
        // but `IsLoaded == false` because the window itself was never `.Show()`n — its
        // content was reparented into BotDockHost. `ReceiveExternalChat` still works in
        // that state because the AgentBotWindow instance keeps direct references to its
        // child controls and dispatches UI updates through them. So the only case we
        // genuinely cannot deliver into is when the bot hasn't been instantiated yet
        // (user never clicked the Bot sidebar button).
        if (_botWindow is null)
        {
            resultJson = "{\"ok\":false,\"error\":\"AgentBot is not open. Click the Bot button in AgentZero first.\"}";
        }
        else
        {
            // DONE 프로토콜 감지: "DONE(from, msg)" 또는 "DONE(msg)"
            var doneMatch = DonePatternWithFrom.Match(message);
            string? doneFrom = null;
            string? doneMsg = null;

            if (doneMatch.Success)
            {
                doneFrom = doneMatch.Groups["from"].Value.Trim();
                doneMsg = doneMatch.Groups["msg"].Value.Trim();
            }
            else
            {
                var simpleMatch = DonePatternSimple.Match(message);
                if (simpleMatch.Success)
                {
                    doneFrom = from; // bot-chat의 --from 파라미터 사용
                    doneMsg = simpleMatch.Groups["msg"].Value.Trim();
                }
            }

            if (doneFrom is not null && doneMsg is not null)
            {
                AppLogger.Log($"[IPC] DONE signal detected: from={doneFrom}, msg len={doneMsg.Length}");

                _botWindow.ReceiveExternalChat($"DONE({doneFrom})", doneMsg);
            }
            else
            {
                _botWindow.ReceiveExternalChat(from, message);
            }

            // Route the inbound peer signal into the actor system so the
            // AIMODE reactor can wake up. The Bot decides whether the peer
            // is in an active conversation (continuation cycle) or just
            // dropped (un-asked-for chatter). Use the DONE-extracted
            // payload when present, otherwise the raw message.
            var peerName = doneFrom ?? from;
            var payload = doneMsg ?? message;
            try
            {
                Agent.Common.AppLogger.Log($"[IPC] forwarding peer signal to Bot: peer=\"{peerName}\" len={payload.Length}");
                Actors.ActorSystemManager.System
                    .ActorSelection("/user/stage/bot")
                    .Tell(new Agent.Common.Actors.TerminalSentToBot(peerName, payload));
            }
            catch (System.Exception ex)
            {
                Agent.Common.AppLogger.Log($"[IPC] forwarding peer signal FAILED: {ex.Message}");
            }

            resultJson = $"{{\"ok\":true,\"from\":\"{EscapeJson(from)}\",\"message_length\":{message.Length}}}";
            AppLogger.Log($"[IPC] bot-chat from={from}, len={message.Length}");
        }

        IpcMemoryMappedResponseWriter.WriteJson(BotChatMmfName, BotChatMmfSize, resultJson, "[IPC] bot-chat 응답 쓰기 오류");
    }

    // =========================================================================
    //  DB persistence (Window state, CLI groups/tabs, CLI definitions)
    // =========================================================================

    private void RestoreWindowState()
    {
        var ws = CliWorkspacePersistence.LoadWindowState();
        if (ws is null) return;

        // 화면 밖 검증
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        double left = ws.Left, top = ws.Top;
        if (left < vLeft || left > vRight - 100) left = vLeft;
        if (top < vTop || top > vBottom - 100) top = vTop;

        Left = left;
        Top = top;
        Width = Math.Max(ws.Width, 400);
        Height = Math.Max(ws.Height, 300);
        if (ws.IsMaximized) WindowState = System.Windows.WindowState.Maximized;
        _isBotEmbedded = ws.IsBotDocked;
    }

    private void SaveWindowState()
    {
        bool isMax = WindowState == System.Windows.WindowState.Maximized;
        var bounds = isMax ? RestoreBounds : new Rect(Left, Top, Width, Height);
        CliWorkspacePersistence.SaveWindowState(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            isMax,
            _activeGroupIndex,
            _activeConsoleTab,
            _isBotEmbedded);
    }

    private void SaveCliGroups()
    {
        CliWorkspacePersistence.SaveCliGroups(_cliGroups);
    }

    private void RestoreCliGroups()
    {
        var groups = CliWorkspacePersistence.LoadCliGroups();
        var state = CliWorkspacePersistence.LoadWindowState();
        int lastGroupIdx = state?.LastActiveGroupIndex ?? 0;
        int lastTabIdx = state?.LastActiveTabIndex ?? 0;

        foreach (var grp in groups)
        {
            if (!System.IO.Directory.Exists(grp.DirectoryPath)) continue;

            AddCliGroup(grp.DirectoryPath, autoCreateTab: false);

            foreach (var tab in grp.Tabs)
            {
                // Don't activate any tab during restore — we'll activate the last active one below
                AddConsoleTab(
                    tab.Title,
                    tab.ExePath,
                    tab.Arguments,
                    tab.CliDefinitionId,
                    activate: false);
            }
        }

        // Activate the last active group and tab (only this one initializes the terminal)
        if (_cliGroups.Count > 0)
        {
            int gIdx = Math.Clamp(lastGroupIdx, 0, _cliGroups.Count - 1);
            ActivateGroup(gIdx);
            var tabs = _cliGroups[gIdx].Tabs;
            if (tabs.Count > 0)
            {
                int tIdx = Math.Clamp(lastTabIdx, 0, tabs.Count - 1);
                ActivateConsoleTab(tIdx);
            }
        }
    }

    private void RebuildCliContextMenu()
    {
        ctxConsoleType.Items.Clear();
        foreach (var def in CliWorkspacePersistence.LoadCliDefinitions())
        {
            var item = new System.Windows.Controls.MenuItem { Header = def.Name };
            var defId = def.Id;
            var name = def.Name;
            var exe = def.ExePath;
            var args = def.Arguments;
            item.Click += (_, _) => AddConsoleTab(name, exe, args, defId);
            ctxConsoleType.Items.Add(item);
        }
    }

    // =========================================================================
    //  Sidebar navigation
    // =========================================================================

    private Window? _scrapWindow;
    private AgentBotWindow? _botWindow;
    private IActorRef? _botActorRef;
    private bool _isAppClosing;

    /// <summary>
    /// When true, the bot panel's content is reparented INTO the main window
    /// (embedded docking). When false, the bot is a free-floating window.
    /// Persisted in <c>AppWindowState.IsBotDocked</c>.
    /// </summary>
    private bool _isBotEmbedded = true;

    /// <summary>Current filter string for the SESSIONS panel. Case-insensitive substring match.</summary>
    private string _sessionFilter = "";

    /// <summary>Periodic repaint of the SESSIONS panel so relative times stay fresh.</summary>
    private System.Windows.Threading.DispatcherTimer? _sessionTickTimer;

    private void OnSidebarBotClick(object sender, RoutedEventArgs e)
    {
        // Embedded mode: toggle the dock row visibility instead of the window
        if (_botWindow is not null && _botWindow.IsLoaded && _isBotEmbedded)
        {
            bool isVisible = BotDockRow.Height.Value > 0;
            if (isVisible)
            {
                BotDockRow.Height = new GridLength(0);
                BotSplitterRow.Height = new GridLength(0);
            }
            else
            {
                BotDockRow.Height = new GridLength(280, GridUnitType.Pixel);
                BotSplitterRow.Height = new GridLength(6, GridUnitType.Pixel);
            }
            UpdateStatusBarBot();
            return;
        }

        // Floating mode (already open & visible) → hide
        if (_botWindow is not null && _botWindow.IsLoaded && _botWindow.IsVisible)
        {
            _botWindow.Hide();
            UpdateStatusBarBot();
            return;
        }

        // Exists but hidden → show (reuse window, floating)
        if (_botWindow is not null && _botWindow.IsLoaded)
        {
            _botWindow.Show();
            _botWindow.Activate();
            UpdateStatusBarBot();
            return;
        }

        _botWindow = new AgentBotWindow(
            getSessionName: GetActiveSessionName,
            getActiveSession: GetActiveSession,
            getActiveDirectory: GetActiveDirectoryPath,
            getGroups: () => _cliGroups,
            getActiveGroupIndex: () => _activeGroupIndex);

        // Owner keeps the bot window tied to the main window's lifetime (Alt+Tab
        // cleanliness, minimize-together). Default behavior: embed into main layout.
        _botWindow.Owner = this;

        // X 버튼 → Hide (앱 종료 시를 제외하고 창 재사용)
        _botWindow.Closing += (_, ev) =>
        {
            if (!_isAppClosing && _botWindow is not null)
            {
                ev.Cancel = true;
                _botWindow.Hide();
            }
        };

        _botWindow.Closed += (_, _) =>
        {
            // Bot 액터 종료
            if (_botActorRef is not null)
            {
                ActorSystemManager.System.Stop(_botActorRef);
                _botActorRef = null;
            }
            _botWindow = null;
            UpdateStatusBarBot();
        };

        _botWindow.IsVisibleChanged += (_, _) => UpdateStatusBarBot();

        // Embed BEFORE Show to avoid a brief window flash. AgentBotWindow's
        // Content is populated by InitializeComponent (the ctor), so we can
        // detach/reparent it without ever showing the window.
        if (_isBotEmbedded)
        {
            EmbedBot();
        }
        else
        {
            _botWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _botWindow.Show();
        }
        UpdateStatusBarBot();

        // Bot 액터 생성 (Stage 경유)
        if (ActorSystemManager.IsInitialized && _botActorRef is null)
        {
            var task = ActorSystemManager.Stage.Ask<BotCreated>(
                new CreateBot(), TimeSpan.FromSeconds(3));
            task.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.BeginInvoke(() =>
                {
                    _botActorRef = t.Result.BotRef;
                    _botWindow?.SetBotActorRef(_botActorRef);

                    // UI 콜백 등록
                    _botActorRef.Tell(new SetBotUiCallback((text, type) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (type == BotResponseType.System)
                                _botWindow?.AddSystemMessage(text);
                        });
                    }), ActorRefs.NoSender);
                });
            });
        }

        // Send welcome for the currently active session on first open
        if (_activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
        {
            var g = _cliGroups[_activeGroupIndex];
            var ti = g.ActiveTabIndex;
            if (ti >= 0 && ti < g.Tabs.Count)
            {
                var tabLabel = g.Tabs[ti].Title;
                _botWindow.ShowWelcomeMessage(g.DisplayName, tabLabel);
            }
        }
    }

    private string GetActiveSessionName()
        => CliSessionAccessHelper.GetActiveSessionName(_cliGroups, _activeGroupIndex);

    private ITerminalSession? GetActiveSession()
        => CliSessionAccessHelper.GetActiveSession(_cliGroups, _activeGroupIndex, EnsureSession);

    private string? GetActiveDirectoryPath()
        => _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count
            ? _cliGroups[_activeGroupIndex].DirectoryPath
            : null;

    /// <summary>
    /// Creates a ConPtyTerminalSession if the terminal is started but Session is missing.
    /// Safe to call multiple times — only creates once.
    /// </summary>
    private static void EnsureSession(ConsoleTabInfo tab, EasyWindowsTerminalControl.EasyTerminalControl? terminal, string groupName)
        => CliSessionAccessHelper.EnsureSession(tab, terminal, groupName);

    /// <summary>
    /// Bind the session to the Akka actor system once it's available. Dedup via
    /// LastBoundSessionId/LastBoundHwnd so repeat Loaded events are idempotent.
    /// </summary>
    private static void BindSessionToActors(ConsoleTabInfo tab, EasyWindowsTerminalControl.EasyTerminalControl terminal, string groupName)
    {
        if (!ActorSystemManager.IsInitialized || tab.Session is null) return;

        if (tab.LastBoundSessionId != tab.Session.SessionId)
        {
            ActorSystemManager.Stage.Tell(new CreateTerminalInWorkspace(
                groupName, tab.Title, tab.Session.SessionId), ActorRefs.NoSender);
            ActorSystemManager.Stage.Tell(new BindSessionInWorkspace(
                groupName, tab.Title, tab.Session), ActorRefs.NoSender);
            tab.LastBoundSessionId = tab.Session.SessionId;
            AppLogger.Log($"[Akka] Terminal actor bound: {groupName}/{tab.Title} session={tab.Session.SessionId}");
        }

        try
        {
            if (System.Windows.PresentationSource.FromVisual(terminal) is System.Windows.Interop.HwndSource src
                && src.Handle != tab.LastBoundHwnd)
            {
                ActorSystemManager.Stage.Tell(
                    new UpdateTerminalHwnd(groupName, tab.Title, src.Handle), ActorRefs.NoSender);
                tab.LastBoundHwnd = src.Handle;
                AppLogger.Log($"[Akka] Terminal HWND: {groupName}/{tab.Title} → 0x{src.Handle:X}");
            }
        }
        catch { /* 레이아웃 완료 전이면 HWND 미획득 — 다음 활성화 시 재시도 */ }
    }

    /// <summary>
    /// Poll EnsureSession every 100ms until the ConPTY output log is ready (session
    /// becomes non-null) or 10s elapses. Fixes the timing race where RestartTerm()
    /// returns before the pipe connects, leaving the tab with a null session and
    /// silent input until the next Loaded event.
    /// </summary>
    private static void StartSessionPendingRetry(ConsoleTabInfo tab, EasyWindowsTerminalControl.EasyTerminalControl terminal, string groupName)
    {
        const int MaxAttempts = 100;
        var attempts = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        tab.SessionPendingRetry = timer;
        timer.Tick += (_, _) =>
        {
            attempts++;

            // Abort if the terminal was swapped out (tab closed/reopened) — the new
            // Loaded handler owns retry for the replacement terminal.
            if (!ReferenceEquals(tab.Terminal, terminal))
            {
                timer.Stop();
                tab.SessionPendingRetry = null;
                AppLogger.Log($"[CLI] Session retry aborted: terminal replaced | label={groupName}/{tab.Title} attempts={attempts}");
                return;
            }

            EnsureSession(tab, terminal, groupName);
            if (tab.Session is not null)
            {
                timer.Stop();
                tab.SessionPendingRetry = null;
                BindSessionToActors(tab, terminal, groupName);
                AppLogger.Log($"[CLI] Session retry success | label={groupName}/{tab.Title} attempts={attempts}");
                return;
            }

            if (attempts >= MaxAttempts)
            {
                timer.Stop();
                tab.SessionPendingRetry = null;
                AppLogger.Log($"[CLI] Session retry timed out | label={groupName}/{tab.Title} attempts={attempts} — tab will remain unbound until next activation");
            }
        };
        timer.Start();
    }


    private void OnSidebarCliClick(object sender, RoutedEventArgs e)
    {
        SwitchToCliPanel();
        if (_activeGroupIndex >= 0)
            ActivateGroup(_activeGroupIndex);
    }

    private void OnSidebarSettingsClick(object sender, RoutedEventArgs e)
    {
        // Toggle: re-clicking the settings icon returns to the CLI panel.
        // We deliberately *do not* call ActivateGroup() on return — settings
        // never changed the active group, and ActivateGroup → RebuildDocumentPane
        // calls terminalDocPane.Children.Clear() which destroys whatever
        // split / multi-pane layout the user had built via drag-drop.
        //
        // Hide CliPanel with Visibility.Hidden (not Collapsed). Collapsed
        // removes the panel from the layout pass, so the AvalonDock
        // DockingManager's measurements stop running while settings is up;
        // Hidden keeps the layout pass alive while making the panel
        // invisible. SettingsPanel sits at a higher Z-order in the same
        // grid cell with an opaque background, so it overlays cleanly.
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            DumpDockLayout("settings-close-before");
            SwitchToCliPanel();
            DumpDockLayout("settings-close-after");
            return;
        }

        DumpDockLayout("settings-open-before");
        CliPanel.Visibility = Visibility.Hidden;
        SettingsPanel.Visibility = Visibility.Visible;
        DumpDockLayout("settings-open-after");
    }

    /// <summary>
    /// SETTINGS-LAYOUT-DIAG — temporary instrumentation for the
    /// "right-pane Document jumps to left pane after settings round-trip"
    /// bug. Dumps each tab's Document → parent LayoutDocumentPane mapping
    /// at four phases of the round-trip so the exact moment the mapping
    /// changes is visible in app-log.txt.
    /// Remove once issue #4 (or whichever closes this bug) lands.
    /// </summary>
    private void DumpDockLayout(string phase)
    {
        try
        {
            var groups = _cliGroups;
            if (groups.Count == 0) return;
            var activeContent = dockManager?.ActiveContent;
            var activeTitle = activeContent is AvalonDock.Layout.LayoutDocument adoc
                ? adoc.Title
                : (activeContent?.GetType().Name ?? "(none)");
            AppLogger.Log($"[Settings-Layout-DIAG] phase={phase} | activeContent={activeTitle} cliPanelVis={CliPanel.Visibility} settingsVis={SettingsPanel.Visibility}");
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var grp = groups[gi];
                for (int ti = 0; ti < grp.Tabs.Count; ti++)
                {
                    var tab = grp.Tabs[ti];
                    if (tab.Document is null) continue;
                    var doc = tab.Document;
                    var parent = doc.Parent;
                    var parentName = parent is AvalonDock.Layout.LayoutDocumentPane pane
                        ? $"pane#0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pane):X8}"
                        : (parent?.GetType().Name ?? "(null)");
                    AppLogger.Log($"[Settings-Layout-DIAG] phase={phase} | g={gi} t={ti} title=\"{tab.Title}\" docHash=0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(doc):X8} parent={parentName} isActive={doc.IsActive} isSelected={doc.IsSelected}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Settings-Layout-DIAG] phase={phase} dump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // =========================================================================
    //  CLI Console tabs (embedded in main window) — Group-based + AvalonDock
    // =========================================================================

    private readonly List<CliGroupInfo> _cliGroups = [];
    private int _activeGroupIndex = -1;

    // Convenience accessors for active group
    private List<ConsoleTabInfo> _consoleTabs
        => _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count
            ? _cliGroups[_activeGroupIndex].Tabs : [];
    private int _activeConsoleTab
    {
        get => _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count
            ? _cliGroups[_activeGroupIndex].ActiveTabIndex : -1;
        set { if (_activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count) _cliGroups[_activeGroupIndex].ActiveTabIndex = value; }
    }

    // --- Group management ---

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Workspace Folder",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        AddCliGroup(dlg.SelectedPath);
    }

    private void AddCliGroup(string dirPath, bool autoCreateTab = true)
    {
        var displayName = System.IO.Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(displayName)) displayName = dirPath;

        var group = new CliGroupInfo { DirectoryPath = dirPath, DisplayName = displayName };
        int groupIdx = _cliGroups.Count;

        // Sidebar button
        var btn = MakeGroupButton(displayName, groupIdx);
        group.SidebarButton = btn;
        pnlCliGroups.Children.Add(btn);
        _cliGroups.Add(group);

        // Notify actor system
        if (ActorSystemManager.IsInitialized)
        {
            ActorSystemManager.Stage.Tell(new RegisterWorkspace(displayName, dirPath), ActorRefs.NoSender);
            AppLogger.Log($"[Akka] RegisterWorkspace sent: {displayName} → {dirPath}");
        }
        else
        {
            AppLogger.Log($"[Akka] RegisterWorkspace SKIPPED (ActorSystem not initialized): {displayName}");
        }

        // Switch to CLI panel + activate group
        SwitchToCliPanel();
        ActivateGroup(groupIdx);

        if (autoCreateTab)
            AddConsoleTab("CMD", "cmd.exe", cliDefinitionId: 1);
    }

    private Border MakeGroupButton(string name, int index)
    {
        var tb = new TextBlock
        {
            Text = name,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var close = new System.Windows.Controls.Button
        {
            Content = "×",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Width = 20,
            Height = 20,
            Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            ToolTip = "Remove group",
        };
        int gi = index;
        close.Click += (_, e) =>
        {
            e.Handled = true;
            var result = System.Windows.MessageBox.Show(
                this,
                $"워크스페이스 '{name}'을(를) 제거하시겠습니까?\n\n워크스페이스 내 모든 세션이 닫힙니다.",
                "워크스페이스 제거",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No);
            if (result == System.Windows.MessageBoxResult.Yes)
                RemoveCliGroup(gi);
        };

        var prefix = new TextBlock
        {
            Text = "├ ",
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(prefix, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        dock.Children.Add(prefix);
        dock.Children.Add(close);
        dock.Children.Add(tb); // fills remaining space

        var border = new Border
        {
            Child = dock,
            Padding = new Thickness(4, 6, 4, 6),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = name,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        };
        border.MouseLeftButtonDown += (_, _) =>
        {
            SwitchToCliPanel();
            ActivateGroup(gi);
        };
        return border;
    }

    private void ActivateGroup(int index)
    {
        if (index < 0 || index >= _cliGroups.Count) return;

        // Re-clicking the *same* workspace sidebar button (or the CLI+
        // sidebar button after a Settings round-trip) used to wipe the
        // user's split-pane layout because RebuildDocumentPane unconditionally
        // calls terminalDocPane.Children.Clear() and re-adds every Document
        // into the first pane. The pane structure itself survives, but the
        // Document → Pane mapping doesn't.
        //
        // Only rebuild when the active group actually changes. Sidebar
        // highlight + active-tab refresh below still run on every call so
        // a re-click still keeps the active-tab state coherent.
        var sameGroup = _activeGroupIndex == index;
        _activeGroupIndex = index;

        // Update sidebar highlights
        for (int i = 0; i < _cliGroups.Count; i++)
        {
            _cliGroups[i].SidebarButton.Background = i == index
                ? (System.Windows.Media.Brush)FindResource("ButtonBg")
                : System.Windows.Media.Brushes.Transparent;
        }

        if (!sameGroup)
        {
            // Rebuild AvalonDock documents only on a *real* group switch.
            RebuildDocumentPane();
        }

        // Ensure at least the first tab is selected when no tab is active
        var tabIdx = _activeConsoleTab;
        if (tabIdx < 0 || tabIdx >= _consoleTabs.Count)
            tabIdx = _consoleTabs.Count > 0 ? 0 : -1;
        if (tabIdx >= 0)
            ActivateConsoleTab(tabIdx);
        else
            RefreshSessionList();

        AppLogger.Log($"[CLI] 그룹 전환: {_cliGroups[index].DisplayName} ({_cliGroups[index].DirectoryPath}) sameGroup={sameGroup}");
    }

    private void RebuildDocumentPane()
    {
        _isDockSyncInProgress = true;
        try
        {
            terminalDocPane.Children.Clear();
            var tabs = _consoleTabs;
            for (int i = 0; i < tabs.Count; i++)
            {
                tabs[i].Document.Title = $"{tabs[i].Title} {i + 1}";
                terminalDocPane.Children.Add(tabs[i].Document);
            }
        }
        finally { _isDockSyncInProgress = false; }
    }

    private void RemoveCliGroup(int index)
    {
        if (index < 0 || index >= _cliGroups.Count) return;
        var group = _cliGroups[index];

        // Notify actor system: destroy all initialized terminals first, then unregister workspace
        if (ActorSystemManager.IsInitialized)
        {
            foreach (var tab in group.Tabs)
            {
                if (tab.IsInitialized)
                {
                    ActorSystemManager.Stage.Tell(new DestroyTerminalInWorkspace(
                        group.DisplayName, tab.Title), ActorRefs.NoSender);
                }
            }
            ActorSystemManager.Stage.Tell(new UnregisterWorkspace(group.DisplayName),
                ActorRefs.NoSender);
            AppLogger.Log($"[Akka] Workspace unregistered: {group.DisplayName} ({group.Tabs.Count} tabs)");
        }

        // Close all tabs in this group
        foreach (var tab in group.Tabs)
        {
            if (tab.Terminal is null) continue;
            try { tab.Terminal.ConPTYTerm?.StopExternalTermOnly(); } catch { }
            tab.TerminalHost.Children.Remove(tab.Terminal);
        }

        // Remove documents from AvalonDock if this is the active group
        if (index == _activeGroupIndex)
        {
            foreach (var tab in group.Tabs)
            {
                if (tab.Document.Parent is AvalonDock.Layout.ILayoutContainer parent)
                    parent.RemoveChild(tab.Document);
            }
        }

        pnlCliGroups.Children.Remove(group.SidebarButton);
        _cliGroups.RemoveAt(index);

        // Rebuild sidebar buttons with correct indices
        pnlCliGroups.Children.Clear();
        for (int i = 0; i < _cliGroups.Count; i++)
        {
            int gi = i;
            var btn = MakeGroupButton(_cliGroups[i].DisplayName, gi);
            _cliGroups[i].SidebarButton = btn;
            pnlCliGroups.Children.Add(btn);
        }

        // Activate next group or clear
        if (_cliGroups.Count > 0)
            ActivateGroup(Math.Min(index, _cliGroups.Count - 1));
        else
        {
            _activeGroupIndex = -1;
            terminalDocPane.Children.Clear();
        }

        // Sync SESSIONS panel — terminals of the removed workspace must disappear.
        RefreshSessionList();
        SaveCliGroups();
    }

    private void SwitchToCliPanel()
    {
        CliPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private NoteWindow? _noteWindow;
    private string? _noteEmbeddedDir;

    /// <summary>
    /// Activate the NOTE tab in the bottom panel. Lazily creates a NoteWindow
    /// for the active workspace and reparents its content into the tab host
    /// (terminal is untouched — it just becomes non-visible via tab switch).
    /// </summary>
    private void OpenNoteTab()
    {
        if (_activeGroupIndex < 0 || _activeGroupIndex >= _cliGroups.Count) return;
        var dirPath = _cliGroups[_activeGroupIndex].DirectoryPath;
        if (string.IsNullOrEmpty(dirPath) || !System.IO.Directory.Exists(dirPath)) return;

        // Rebuild note if the active workspace has changed
        if (_noteWindow is not null && _noteEmbeddedDir != dirPath)
        {
            NoteHost.Content = null;
            _noteWindow.Close();
            _noteWindow = null;
        }

        if (_noteWindow is null)
        {
            _noteWindow = new NoteWindow(dirPath) { Owner = this };
            var content = _noteWindow.DetachContent();
            _noteWindow.SetEmbeddedMode(true);
            if (content is not null)
                NoteHost.Content = content;
            _noteWindow.InitializeCore(); // no Window.Show() ⇒ wire up manually
            _noteEmbeddedDir = dirPath;
        }

        SwitchBottomTab(BottomTab.Note);
    }


    private void OnAddConsoleClick(object sender, RoutedEventArgs e)
    {
        if (_activeGroupIndex < 0)
        {
            // No group — prompt to create one first
            OnAddGroupClick(sender, e);
            return;
        }
        ctxConsoleType.IsOpen = true;
    }

    // CLI 메뉴는 DB에서 동적으로 구성됨 (RebuildCliContextMenu)

    private void AddConsoleTab(string title, string exe, string? arguments = null, int cliDefinitionId = 0, bool activate = true,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
    {
        // PTY-FREEZE-DIAG: track every new-ConsoleTabInfo creation. The
        // upstream symptom (claudeA accumulating 5 RestartTerm calls
        // without the user re-adding the shell) means *something* is
        // re-creating the ConsoleTabInfo behind the user's back. The
        // caller / line tells us which code path.
        AppLogger.Log($"[CLI-Init-DIAG] AddConsoleTab CALLED | title=\"{title}\" group={_activeGroupIndex} totalSoFar={_consoleTabs.Count} caller={caller}:{callerLine} activate={activate}");

        if (_activeGroupIndex < 0)
        {
            AppLogger.Log($"[CLI-Init-DIAG] AddConsoleTab ABORTED (no active group) | title=\"{title}\"");
            return;
        }

        int idx = _consoleTabs.Count;
        var termHost = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        // Prevent WPF KeyboardNavigation from consuming Tab/arrow keys
        // so they reach the HWND-based terminal control.
        KeyboardNavigation.SetTabNavigation(termHost, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(termHost, KeyboardNavigationMode.None);
        var doc = new AvalonDock.Layout.LayoutDocument
        {
            Title = $"{title} {idx + 1}",
            Content = termHost,
            CanFloat = true,
            CanClose = true,
        };

        var newTab = new ConsoleTabInfo
        {
            Title = title, Document = doc, TerminalHost = termHost,
            CliDefinitionId = cliDefinitionId,
            ExePath = exe, Arguments = arguments,
            Terminal = null, IsInitialized = false,
        };
        _consoleTabs.Add(newTab);
        AppLogger.Log($"[CLI-Init-DIAG] ConsoleTabInfo created | title=\"{title}\" tabHash=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(newTab):X8} idx={idx}");

        // Add to the active document pane (may differ from terminalDocPane after splits)
        GetActiveDocumentPane().Children.Add(doc);

        if (activate)
            ActivateConsoleTab(idx);
    }

    /// <summary>
    /// Finds the best LayoutDocumentPane to add new documents to.
    /// After splits/rearranges, terminalDocPane may be orphaned.
    /// </summary>
    private AvalonDock.Layout.LayoutDocumentPane GetActiveDocumentPane()
    {
        // 1. If the active document's pane is available, use it
        if (_activeConsoleTab >= 0 && _activeConsoleTab < _consoleTabs.Count)
        {
            var activeDoc = _consoleTabs[_activeConsoleTab].Document;
            if (activeDoc.Parent is AvalonDock.Layout.LayoutDocumentPane activePane)
                return activePane;
        }

        // 2. If terminalDocPane is still in the layout, use it
        if (terminalDocPane.Parent is not null)
            return terminalDocPane;

        // 3. Find any existing DocumentPane in the layout
        var pane = FindDocumentPane(dockManager.Layout.RootPanel);
        if (pane is not null)
            return pane;

        // 4. Last resort: recreate the pane in the root
        var root = dockManager.Layout;
        root.RootPanel ??= new AvalonDock.Layout.LayoutPanel();
        var newPane = new AvalonDock.Layout.LayoutDocumentPane();
        root.RootPanel.Children.Add(newPane);
        terminalDocPane = newPane;
        return newPane;
    }

    private static AvalonDock.Layout.LayoutDocumentPane? FindDocumentPane(AvalonDock.Layout.ILayoutElement? element)
    {
        if (element is AvalonDock.Layout.LayoutDocumentPane pane)
            return pane;
        if (element is AvalonDock.Layout.ILayoutContainer container)
        {
            foreach (var child in container.Children)
            {
                var found = FindDocumentPane(child);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>Creates and starts the ConPTY terminal for a tab that hasn't been initialized yet.</summary>
    private void InitializeTerminal(ConsoleTabInfo tab,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
    {
        // ⚠ CLI diagnostic log — DO NOT REMOVE (핵심 진단 자산, 삭제 금지)
        var tabHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tab);
        AppLogger.Log($"[CLI] InitializeTerminal | title={tab.Title} tabHash=#{tabHash:X8} isInit={tab.IsInitialized} isStarted={tab.IsTerminalStarted} caller={caller}:{callerLine}");
        if (tab.IsInitialized) return;
        tab.IsInitialized = true;  // Set immediately to prevent re-entrant calls from OnDockActiveContentChanged

        string workDir = _cliGroups[_activeGroupIndex].DirectoryPath;
        string rawCmd = string.IsNullOrEmpty(tab.Arguments) ? tab.ExePath : $"{tab.ExePath} {tab.Arguments}";

        // Prepend our exe directory to PATH for *this PTY only*. Why:
        //   - The skills-starter-pack scripts call `& AgentZeroLite.ps1`,
        //     which resolves via PATH. If User PATH is missing or stale
        //     (or points at a sibling project's build), the call fails.
        //   - We *own* this child cmd.exe — it inherits whatever environment
        //     we set here, and pwsh/cmd/claude inherit from cmd.exe in turn.
        //   - The injection lives only inside this PTY; User PATH is untouched.
        // cmd parses `set "PATH=value"` as one token even when value contains
        // semicolons or `%PATH%`, so the quoted form is robust. The outer
        // /c double-quote pair survives because cmd treats the *first* quote
        // and the *last* quote as the /c boundary.
        string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string injectPath = $"set \"PATH={appDir};%PATH%\"";
        string cmdLine = !string.IsNullOrEmpty(workDir) && System.IO.Directory.Exists(workDir)
            ? $"cmd /c \"{injectPath}&&pushd \"{workDir}\"&&{rawCmd}\""
            : $"cmd /c \"{injectPath}&&{rawCmd}\"";

        var terminal = new EasyWindowsTerminalControl.EasyTerminalControl();
        terminal.StartupCommandLine = cmdLine;
        terminal.FontFamilyWhenSettingTheme = new System.Windows.Media.FontFamily("Consolas");
        terminal.FontSizeWhenSettingTheme = 12;
        // Win32InputMode=true sends raw INPUT_RECORDs via the win32-input-mode VT
        // escape, which delivers every keystroke (including modifiers) to the PTY.
        // Tradeoff: Korean IME jamo keystrokes produce virtual-keys outside the
        // ConsoleKey enum (> 255); PSReadLine unwraps those INPUT_RECORDs and
        // throws on the out-of-range VK, crashing PowerShell 7. Standard Windows
        // Terminal keeps this OFF for shells and lets TermControl's TSF handle
        // IME composition natively (final text arrives as UTF-8 VT). We follow
        // the same default so Korean input works with pwsh/cmd/claude.
        terminal.Win32InputMode = false;
        terminal.LogConPTYOutput = true;
        terminal.Theme = new Microsoft.Terminal.Wpf.TerminalTheme
        {
            DefaultBackground = EasyWindowsTerminalControl.EasyTerminalControl.ColorToVal(
                System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
            DefaultForeground = EasyWindowsTerminalControl.EasyTerminalControl.ColorToVal(
                System.Windows.Media.Color.FromRgb(0xD4, 0xD4, 0xD4)),
            DefaultSelectionBackground = 0x264F78,
            CursorStyle = Microsoft.Terminal.Wpf.CursorStyle.BlinkingBar,
            ColorTable = new uint[]
            {
                0x1E1E1E, 0xC74E39, 0x608B4E, 0xDCDCAA,
                0x569CD6, 0xC586C0, 0x4EC9B0, 0xD4D4D4,
                0x808080, 0xF14C4C, 0xB5CEA8, 0xDCDCAA,
                0x9CDCFE, 0xD670D6, 0x4EC9B0, 0xFFFFFF,
            },
        };
        terminal.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        terminal.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;

        // Tab: intercept in PreviewKeyDown before WPF's HwndHost processes it.
        // (HwndHost's built-in Tab handling disrupts terminal cursor/focus state.)
        // With Win32InputMode=false, TermControl's native TSF handles Korean IME
        // composition directly — no ImeProcessed blocking needed.
        terminal.PreviewKeyDown += (_, e) =>
        {
            // PTY-FREEZE-DIAG: if the user reports "keyboard does nothing",
            // the first question is whether WPF saw the key at all. A line
            // here per keystroke proves the key reached the WPF tree; absence
            // of these lines while the user types means focus is parked
            // somewhere the terminal doesn't own.
            AppLogger.Log($"[CLI-Input-DIAG] PreviewKeyDown | tab={tab.Title} key={e.Key} handled={e.Handled}");

            if (e.Key == System.Windows.Input.Key.Tab)
            {
                terminal.ConPTYTerm?.WriteToTerm("\t".AsSpan());
                e.Handled = true;
            }

            // PTY-FREEZE-DIAG: echo verification — snapshot outLen, recheck
            // 800ms later. If unchanged, the key never produced any visible
            // response from the foreground child (shell echo, TUI redraw,
            // anything). Skip noise keys (modifiers, IME composing, repeats)
            // since those legitimately don't echo. Skip Tab too — handled
            // path above writes \t and shells often respond with completion
            // not text growth.
            if (!IsEchoCandidateKey(e.Key)) return;
            var pty = terminal.ConPTYTerm;
            var beforeLen = pty?.ConsoleOutputLog?.Length ?? -1;
            if (beforeLen < 0) return;
            var keyName = e.Key.ToString();
            var tabTitle = tab.Title;
            _ = Task.Delay(800).ContinueWith(_ =>
            {
                var afterLen = pty?.ConsoleOutputLog?.Length ?? -1;
                if (afterLen == beforeLen)
                    AppLogger.Log($"[CLI-Input-DIAG] KEY-NO-ECHO | tab={tabTitle} key={keyName} outLenStable={afterLen} after=800ms");
            }, TaskScheduler.Default);
        };

        // PTY-FREEZE-DIAG: focus trace — pure logging, no IsActive write.
        // Earlier IsActive-from-GotFocus attempt (commit 6143c60, reverted in
        // f679a7a) caused an active-document ricochet loop. These logs only
        // observe; nothing changes the dock state.
        terminal.GotFocus += (_, _) =>
        {
            // PTY-FREEZE-DIAG: snapshot PTY state on focus. If a tab "doesn't
            // accept input" while focused, the next questions are: is the PTY
            // ref still wired? is its output log alive? is the process running?
            // Logging here makes those answers visible at the moment focus
            // settled — useful when correlated with later KEY-NO-ECHO lines.
            var pty = terminal.ConPTYTerm;
            var ptyHash = pty is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pty);
            var outLogNull = pty?.ConsoleOutputLog is null;
            var outLen = pty?.ConsoleOutputLog?.Length ?? -1;
            AppLogger.Log($"[CLI-Input-DIAG] GotFocus  | tab={tab.Title} active={tab.Document?.IsActive == true} ptyHash=0x{ptyHash:X8} outLogNull={outLogNull} outLen={outLen}");
        };
        terminal.LostFocus += (_, _) =>
            AppLogger.Log($"[CLI-Input-DIAG] LostFocus | tab={tab.Title} active={tab.Document?.IsActive == true}");

        tab.TerminalHost.Children.Add(terminal);

        // Capture group name now (before async Loaded) for session ID
        var groupName = _cliGroups[_activeGroupIndex].DisplayName;

        terminal.Loaded += (_, _) =>
        {
            // PTY-FREEZE-DIAG: WPF fires Loaded again whenever the control is
            // reparented (tab activation, dock layout restore, etc). The
            // existing `IsTerminalStarted` guard prevents double-RestartTerm,
            // but the *frequency* of Loaded was previously invisible. If a
            // freeze coincides with an unexpected re-fire, this log is the
            // first signal.
            var ptyHashOnLoad = terminal.ConPTYTerm is null
                ? 0
                : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terminal.ConPTYTerm);
            var tabHashOnLoad = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tab);
            var termHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terminal);
            AppLogger.Log($"[CLI] Loaded fired | label={groupName}/{tab.Title} tabHash=#{tabHashOnLoad:X8} termHash=#{termHash:X8} hasConPTY={terminal.ConPTYTerm is not null} ptyHash=0x{ptyHashOnLoad:X8} isStarted={tab.IsTerminalStarted}");

            if (terminal.ConPTYTerm is null) return;

            if (!tab.IsTerminalStarted)
            {
                terminal.RestartTerm();
                tab.IsTerminalStarted = true;
                // PTY-FREEZE-DIAG: capture the post-RestartTerm state so we
                // can correlate the cmdLine with the actual ConPTYTerm object
                // reference. Two tabs that share a ptyHash here is the strong
                // smoking gun for cross-tab pipe collision.
                var ptyHashAfter = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terminal.ConPTYTerm);
                var outLen = terminal.ConPTYTerm.ConsoleOutputLog?.Length ?? -1;
                AppLogger.Log($"[CLI] RestartTerm(): {cmdLine} | label={groupName}/{tab.Title} ptyHash=0x{ptyHashAfter:X8} outputLog={(outLen >= 0 ? outLen.ToString() : "null")}");
            }

            // Create session if missing (covers both first start and reparent/restore)
            EnsureSession(tab, terminal, groupName);
            BindSessionToActors(tab, terminal, groupName);

            // If session still null, PTY output log hasn't initialized yet.
            // Poll every 100ms until ready or 10s timeout, then bind to actors.
            if (tab.Session is null && tab.SessionPendingRetry is null)
            {
                StartSessionPendingRetry(tab, terminal, groupName);
            }
        };

        tab.Terminal = terminal;
        AppLogger.Log($"[CLI] ConPTY 터미널 생성 (lazy): {cmdLine}, dir={workDir}");
    }

    // --- AvalonDock DockingManager integration ---

    private bool _isDockSyncInProgress;

    private void InitializeDockManager()
    {
        dockManager.Theme = new AvalonDock.Themes.Vs2013DarkTheme();
        dockManager.ActiveContentChanged += OnDockActiveContentChanged;
        dockManager.DocumentClosing += OnDockDocumentClosing;
        dockManager.Layout.Updated += OnLayoutRootUpdated;

        // Context menu for AvalonDock document tabs
        var ctx = new System.Windows.Controls.ContextMenu();

        var ctxRename = new System.Windows.Controls.MenuItem { Header = "Rename" };
        ctxRename.Click += OnDocTabRename;
        ctx.Items.Add(ctxRename);

        ctx.Items.Add(new System.Windows.Controls.Separator());

        var ctxCloseAll = new System.Windows.Controls.MenuItem { Header = "Close All" };
        ctxCloseAll.Click += (_, _) => CloseAllTabs();
        ctx.Items.Add(ctxCloseAll);

        var ctxCloseOthers = new System.Windows.Controls.MenuItem { Header = "Close Others" };
        ctxCloseOthers.Click += OnDocTabCloseOthers;
        ctx.Items.Add(ctxCloseOthers);

        var ctxCloseRight = new System.Windows.Controls.MenuItem { Header = "Close to the Right" };
        ctxCloseRight.Click += OnDocTabCloseRight;
        ctx.Items.Add(ctxCloseRight);

        dockManager.DocumentContextMenu = ctx;
    }

    private ConsoleTabInfo? FindTabByDocument(AvalonDock.Layout.LayoutDocument doc)
        => _consoleTabs.FirstOrDefault(t => t.Document == doc);

    private ConsoleTabInfo? FindTabByContent(object? content)
        => content is Grid host ? _consoleTabs.FirstOrDefault(t => t.TerminalHost == host) : null;

    private AvalonDock.Layout.LayoutDocument? GetContextDocument(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return null;

        // Walk up to ContextMenu
        var cm = mi.Parent as System.Windows.Controls.ContextMenu
                 ?? LogicalTreeHelper.GetParent(mi) as System.Windows.Controls.ContextMenu;
        if (cm is null) return null;

        // AvalonDock sets DataContext on the ContextMenu or PlacementTarget
        if (cm.DataContext is AvalonDock.Controls.LayoutItem li)
            return li.LayoutElement as AvalonDock.Layout.LayoutDocument;

        if ((cm.PlacementTarget as System.Windows.FrameworkElement)?.DataContext
            is AvalonDock.Controls.LayoutItem li2)
            return li2.LayoutElement as AvalonDock.Layout.LayoutDocument;

        // Fallback: use the currently active document
        var activeTab = (_activeConsoleTab >= 0 && _activeConsoleTab < _consoleTabs.Count)
            ? _consoleTabs[_activeConsoleTab] : null;
        return activeTab?.Document;
    }

    private void OnDocTabRename(object sender, RoutedEventArgs e)
    {
        var doc = GetContextDocument(sender);
        if (doc is null) return;
        var tab = FindTabByDocument(doc);
        if (tab is null) return;

        // Simple input dialog via popup
        var dlg = new Window
        {
            Title = "Rename Tab",
            Width = 300, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0D, 0x0D, 0x1A)),
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        var tb = new System.Windows.Controls.TextBox
        {
            Text = tab.Title, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13, Foreground = System.Windows.Media.Brushes.White,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x12, 0x2A)),
            Padding = new Thickness(6, 4, 6, 4),
        };
        var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 60, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        tb.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) { dlg.DialogResult = true; dlg.Close(); } };
        sp.Children.Add(tb);
        sp.Children.Add(okBtn);
        dlg.Content = sp;
        dlg.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text))
        {
            var oldTitle = tab.Title;
            var newTitle = tb.Text.Trim();
            tab.Title = newTitle;
            int idx = _consoleTabs.IndexOf(tab);
            doc.Title = $"{newTitle} {idx + 1}";

            // 액터 시스템에 이름 변경 알림
            if (ActorSystemManager.IsInitialized && tab.IsInitialized
                && _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
            {
                var wsName = _cliGroups[_activeGroupIndex].DisplayName;
                ActorSystemManager.Stage.Tell(
                    new RenameTerminalInWorkspace(wsName, oldTitle, newTitle), ActorRefs.NoSender);
                AppLogger.Log($"[Akka] Terminal renamed: {wsName}/{oldTitle} → {newTitle}");
            }
        }
    }

    private void OnDocTabCloseOthers(object sender, RoutedEventArgs e)
    {
        var doc = GetContextDocument(sender);
        if (doc is null) return;
        var tab = FindTabByDocument(doc);
        if (tab is null) return;
        CloseOtherTabs(_consoleTabs.IndexOf(tab));
    }

    private void OnDocTabCloseRight(object sender, RoutedEventArgs e)
    {
        var doc = GetContextDocument(sender);
        if (doc is null) return;
        var tab = FindTabByDocument(doc);
        if (tab is null) return;
        CloseRightTabs(_consoleTabs.IndexOf(tab));
    }



    private void OnDockActiveContentChanged(object? sender, EventArgs e)
    {
        if (_isDockSyncInProgress) return;

        var tab = FindTabByContent(dockManager.ActiveContent);
        if (tab is null) return;

        int index = _consoleTabs.IndexOf(tab);
        if (index < 0 || index == _activeConsoleTab) return;

        _activeConsoleTab = index;

        // Lazy init
        if (!tab.IsInitialized)
            InitializeTerminal(tab);

        // Focus
        if (tab.Terminal is { } terminal)
        {
            Dispatcher.BeginInvoke(() => FocusTerminal(terminal),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Update bot window
        _botWindow?.RefreshSessionInfo();
        if (_botWindow is not null
            && _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
        {
            var g = _cliGroups[_activeGroupIndex];
            var ti = g.ActiveTabIndex;
            if (ti >= 0 && ti < g.Tabs.Count)
            {
                var tabLabel = $"{g.Tabs[ti].Title}-{ti + 1}";
                _botWindow.ShowWelcomeMessage(g.DisplayName, tabLabel);
            }
        }
    }

    private void OnLayoutRootUpdated(object? sender, EventArgs e)
    {
        // Ensure terminalDocPane stays in layout after splits/rearranges.
        // Fix floating window Owner to prevent parent-blocking.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Pane preservation: re-add if orphaned
                if (terminalDocPane.Parent is null)
                {
                    var root = dockManager.Layout;
                    root.RootPanel ??= new AvalonDock.Layout.LayoutPanel();
                    root.RootPanel.Children.Add(terminalDocPane);
                }

                // Fix floating windows: unset Owner to prevent parent input blocking
                var floats = dockManager.FloatingWindows?.ToArray();
                if (floats is not null)
                {
                    foreach (var fw in floats)
                    {
                        if (fw?.Owner is not null)
                        {
                            fw.Owner = null;
                            fw.ShowInTaskbar = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[Dock] LayoutRootUpdated handler failed", ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool _isClosingTab;

    private void OnDockDocumentClosing(object? sender, AvalonDock.DocumentClosingEventArgs e)
    {
        if (_isClosingTab) return; // Called from CloseConsoleTab, skip (already handled)

        // Let AvalonDock handle the document removal (don't cancel)
        var tab = FindTabByDocument(e.Document);
        if (tab is null) return;

        // 액터 시스템에 터미널 종료 알림 (이전에 누락되었던 경로)
        if (ActorSystemManager.IsInitialized && tab.IsInitialized
            && _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
        {
            var wsName = _cliGroups[_activeGroupIndex].DisplayName;
            ActorSystemManager.Stage.Tell(
                new DestroyTerminalInWorkspace(wsName, tab.Title), ActorRefs.NoSender);
            AppLogger.Log($"[Akka] Terminal destroyed (dock close): {wsName}/{tab.Title}");
        }

        // Cleanup session and terminal
        tab.Session?.Dispose();
        tab.Session = null;
        if (tab.Terminal is not null)
        {
            try { tab.Terminal.ConPTYTerm?.StopExternalTermOnly(); } catch { }
            tab.TerminalHost.Children.Remove(tab.Terminal);
        }

        int index = _consoleTabs.IndexOf(tab);
        if (index < 0) return;
        _consoleTabs.RemoveAt(index);

        // Update document titles
        for (int i = 0; i < _consoleTabs.Count; i++)
            _consoleTabs[i].Document.Title = $"{_consoleTabs[i].Title} {i + 1}";

        if (_consoleTabs.Count > 0)
            Dispatcher.BeginInvoke(() =>
                ActivateConsoleTab(Math.Min(index, _consoleTabs.Count - 1)));
        else
        {
            _activeConsoleTab = -1;
            RefreshSessionList();
        }
    }

    private void ActivateConsoleTab(int index)
    {
        if (index < 0 || index >= _consoleTabs.Count) return;

        // Lazy init: create terminal on first activation
        var activeTab = _consoleTabs[index];
        if (!activeTab.IsInitialized)
            InitializeTerminal(activeTab);

        // Activate document in AvalonDock (triggers tab highlight)
        _isDockSyncInProgress = true;
        try { activeTab.Document.IsActive = true; }
        finally { _isDockSyncInProgress = false; }
        _activeConsoleTab = index;

        // Touch activity timestamp so the SESSIONS panel reflects recency
        activeTab.LastActivityAt = DateTime.Now;

        // Refresh the SESSIONS panel to reflect the new active tab
        RefreshSessionList();

        // Notify actor system of active terminal change
        if (ActorSystemManager.IsInitialized && _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
            ActorSystemManager.Stage.Tell(new SetActiveTerminal(
                _cliGroups[_activeGroupIndex].DisplayName, activeTab.Title), ActorRefs.NoSender);

        int safeIdx = index;
        Dispatcher.BeginInvoke(() =>
        {
            if (safeIdx >= 0 && safeIdx < _consoleTabs.Count && _consoleTabs[safeIdx].Terminal is { } t)
                FocusTerminal(t);
            _botWindow?.RefreshSessionInfo();
            if (_botWindow is not null
                && _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
            {
                var g = _cliGroups[_activeGroupIndex];
                var ti = g.ActiveTabIndex;
                if (ti >= 0 && ti < g.Tabs.Count)
                {
                    var tabLabel = $"{g.Tabs[ti].Title}-{ti + 1}";
                    _botWindow.ShowWelcomeMessage(g.DisplayName, tabLabel);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void FocusTerminal(EasyWindowsTerminalControl.EasyTerminalControl terminal)
    {
        // Step 1: WPF Focus
        terminal.Focus();
        System.Windows.Input.Keyboard.Focus(terminal);

        // Step 2: Win32 SetFocus — ConPTY is HWND-based, WPF Focus alone is not enough
        try
        {
            if (System.Windows.PresentationSource.FromVisual(terminal) is System.Windows.Interop.HwndSource source)
                NativeMethods.SetFocus(source.Handle);
        }
        catch { /* WPF Focus already set */ }
    }

    private void CloseConsoleTab(int index)
    {
        if (index < 0 || index >= _consoleTabs.Count) return;
        var tab = _consoleTabs[index];

        // Notify actor system before cleanup — only if terminal was initialized (actor exists)
        if (ActorSystemManager.IsInitialized && tab.IsInitialized
            && _activeGroupIndex >= 0 && _activeGroupIndex < _cliGroups.Count)
        {
            ActorSystemManager.Stage.Tell(new DestroyTerminalInWorkspace(
                _cliGroups[_activeGroupIndex].DisplayName, tab.Title), ActorRefs.NoSender);
            AppLogger.Log($"[Akka] Terminal destroyed: {_cliGroups[_activeGroupIndex].DisplayName}/{tab.Title}");
        }

        // Cleanup session and terminal
        tab.Session?.Dispose();
        tab.Session = null;
        if (tab.Terminal is not null)
        {
            try { tab.Terminal.ConPTYTerm?.StopExternalTermOnly(); } catch { }
            if (tab.Terminal.Parent is Panel p) p.Children.Remove(tab.Terminal);
        }

        _consoleTabs.RemoveAt(index);

        // Remove from AvalonDock (with guard to prevent re-entrant DocumentClosing)
        _isClosingTab = true;
        try
        {
            if (tab.Document.Parent is AvalonDock.Layout.ILayoutContainer parent)
                parent.RemoveChild(tab.Document);
        }
        finally { _isClosingTab = false; }

        // Update document titles
        for (int i = 0; i < _consoleTabs.Count; i++)
            _consoleTabs[i].Document.Title = $"{_consoleTabs[i].Title} {i + 1}";

        if (_consoleTabs.Count > 0)
            ActivateConsoleTab(Math.Min(index, _consoleTabs.Count - 1));
        else
            _activeConsoleTab = -1;
    }

    private void CloseAllTabs()
    {
        for (int i = _consoleTabs.Count - 1; i >= 0; i--)
            CloseConsoleTab(i);
    }

    private void CloseOtherTabs(int keepIndex)
    {
        for (int i = _consoleTabs.Count - 1; i >= 0; i--)
        {
            if (i == keepIndex) continue;
            CloseConsoleTab(i);
            if (i < keepIndex) keepIndex--;
        }
    }

    private void CloseRightTabs(int fromIndex)
    {
        for (int i = _consoleTabs.Count - 1; i > fromIndex; i--)
            CloseConsoleTab(i);
    }

    // =========================================================================
    //  Custom title bar handlers (Cyberpunk windowless chrome)
    // =========================================================================

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
                ToggleMaximize();
            else
                DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    // =========================================================================
    //  ActivityBar navigation (Phase 1 redesign)
    // =========================================================================

    private void OnActivityBotClick(object sender, RoutedEventArgs e)
        => OnSidebarBotClick(sender, e);


    private void OnActivitySettingsClick(object sender, RoutedEventArgs e)
        => OnSidebarSettingsClick(sender, e);

    // =========================================================================
    //  Sessions panel (Phase 2 redesign)
    // =========================================================================

    /// <summary>
    /// Rebuilds the SESSIONS panel to show <b>all running terminal sessions across
    /// every workspace</b> — not just the active group. A session is "running" when
    /// its ConPTY process has been initialized (<see cref="ConsoleTabInfo.IsInitialized"/>).
    /// Clicking a row switches to the owning workspace + tab in one action, saving the
    /// user from having to navigate via DIRECTORIES first.
    /// </summary>
    private void RefreshSessionList()
    {
        if (pnlSessions is null) return;
        pnlSessions.Children.Clear();

        UpdateStatusBarSession();
        if (_cliGroups.Count == 0) return;

        // Collect initialized (running) sessions across ALL workspaces
        string filter = _sessionFilter.Trim();
        for (int gi = 0; gi < _cliGroups.Count; gi++)
        {
            var group = _cliGroups[gi];
            for (int ti = 0; ti < group.Tabs.Count; ti++)
            {
                var tab = group.Tabs[ti];
                if (!tab.IsInitialized) continue; // only running sessions

                bool isActive = (gi == _activeGroupIndex && ti == _activeConsoleTab);
                int capturedGroup = gi;
                int capturedTab = ti;
                string groupLabel = group.DisplayName;
                string tabLabel = $"{tab.Title} {ti + 1}";

                // Filter: title or workspace name must contain the filter substring
                if (filter.Length > 0
                    && !tabLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !groupLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var row = new Border
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    Margin = new Thickness(0, 0, 0, 1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = isActive
                        ? new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(0x33, 0x26, 0x4F, 0x78))
                        : System.Windows.Media.Brushes.Transparent,
                    ToolTip = $"{groupLabel} / {tabLabel}  (클릭: 이 세션으로 이동)",
                };

                var stack = new StackPanel { Orientation = Orientation.Vertical };

                // Line 1: live dot + icon + session title
                var line1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                // Status badge: green dot for running, cyan for active-running
                var liveDot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = isActive
                        ? (System.Windows.Media.Brush)FindResource("CyberCyanBrush")
                        : (System.Windows.Media.Brush)FindResource("CyberPurpleBrush"),
                    ToolTip = isActive ? "활성 · 실행 중" : "실행 중",
                };
                line1.Children.Add(liveDot);

                var icon = new TextBlock
                {
                    Text = "\uE756",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isActive
                        ? (System.Windows.Media.Brush)FindResource("CyberCyanBrush")
                        : (System.Windows.Media.Brush)FindResource("TextDim"),
                };
                var title = new TextBlock
                {
                    Text = tabLabel,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isActive
                        ? (System.Windows.Media.Brush)FindResource("TextPrimary")
                        : (System.Windows.Media.Brush)FindResource("TextDim"),
                    FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                };
                line1.Children.Add(icon);
                line1.Children.Add(title);
                stack.Children.Add(line1);

                // Line 2: workspace origin + last activity hint
                var origin = new TextBlock
                {
                    Text = $"{groupLabel}  ·  {FormatRelativeTime(tab.LastActivityAt)}",
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 9,
                    Margin = new Thickness(18, 0, 0, 0),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
                    Opacity = 0.7,
                };
                stack.Children.Add(origin);

                row.Child = stack;

                // Double-click → rename the session inline via a lightweight prompt
                var tabRef = tab;
                row.PreviewMouseLeftButtonDown += (_, ev) =>
                {
                    if (ev.ClickCount != 2) return;
                    ev.Handled = true;
                    var newName = PromptRenameSession(tabRef.Title);
                    if (string.IsNullOrEmpty(newName) || newName == tabRef.Title) return;
                    tabRef.Title = newName;
                    tabRef.Document.Title = $"{newName} {capturedTab + 1}";
                    SaveCliGroups();
                    RefreshSessionList();
                };

                // Click → switch group + activate tab in a single user action
                row.MouseLeftButtonUp += (_, _) => NavigateToSession(capturedGroup, capturedTab);
                row.MouseEnter += (_, _) =>
                {
                    if (!(capturedGroup == _activeGroupIndex && capturedTab == _activeConsoleTab))
                        row.Background = (System.Windows.Media.Brush)FindResource("ButtonHover");
                };
                row.MouseLeave += (_, _) =>
                {
                    if (!(capturedGroup == _activeGroupIndex && capturedTab == _activeConsoleTab))
                        row.Background = System.Windows.Media.Brushes.Transparent;
                };

                // Right-click: navigate or close the session (cross-workspace safe)
                var menu = new ContextMenu();

                var miActivate = new MenuItem { Header = "이 세션으로 이동" };
                miActivate.Click += (_, _) => NavigateToSession(capturedGroup, capturedTab);
                menu.Items.Add(miActivate);

                menu.Items.Add(new Separator());

                var miClose = new MenuItem { Header = "세션 닫기" };
                miClose.Click += (_, _) => CloseSessionAcrossWorkspaces(capturedGroup, capturedTab);
                menu.Items.Add(miClose);

                row.ContextMenu = menu;

                pnlSessions.Children.Add(row);
            }
        }
    }

    /// <summary>
    /// Switches to the owning workspace (if different from the active one) and
    /// activates the requested tab. Combines two steps that previously required
    /// sidebar + tab clicks into a single action.
    /// </summary>
    private void NavigateToSession(int groupIndex, int tabIndex)
    {
        if (groupIndex < 0 || groupIndex >= _cliGroups.Count) return;

        if (groupIndex != _activeGroupIndex)
            ActivateGroup(groupIndex);

        var group = _cliGroups[groupIndex];
        if (tabIndex >= 0 && tabIndex < group.Tabs.Count)
            ActivateConsoleTab(tabIndex);
    }

    /// <summary>
    /// Closes a session in any workspace. If it belongs to a non-active workspace,
    /// switches there first so <see cref="CloseConsoleTab"/> (which operates on the
    /// active group) can act on the correct tab.
    /// </summary>
    private void CloseSessionAcrossWorkspaces(int groupIndex, int tabIndex)
    {
        if (groupIndex < 0 || groupIndex >= _cliGroups.Count) return;

        if (groupIndex != _activeGroupIndex)
            ActivateGroup(groupIndex);

        var group = _cliGroups[groupIndex];
        if (tabIndex >= 0 && tabIndex < group.Tabs.Count)
            CloseConsoleTab(tabIndex);
    }

    /// <summary>
    /// Shows a small modal prompt for renaming a session. Returns the trimmed new
    /// name, or null if the user cancelled / left it empty / didn't change it.
    /// </summary>
    private string? PromptRenameSession(string currentName)
    {
        var win = new Window
        {
            Title = "세션 이름 변경",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = (System.Windows.Media.Brush)FindResource("PanelBg"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
        };

        var root = new StackPanel { Margin = new Thickness(14) };

        var label = new TextBlock
        {
            Text = "새 세션 이름",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
        };

        var tb = new System.Windows.Controls.TextBox
        {
            Text = currentName,
            Style = (Style)FindResource("DarkTextBox"),
            Height = 26,
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var okBtn = new Button
        {
            Content = "변경",
            Style = (Style)FindResource("AccentButton"),
            Padding = new Thickness(14, 4, 14, 4),
        };
        var cancelBtn = new Button
        {
            Content = "취소",
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(6, 0, 0, 0),
        };

        string? result = null;
        void Commit()
        {
            result = tb.Text.Trim();
            win.Close();
        }

        okBtn.Click += (_, _) => Commit();
        cancelBtn.Click += (_, _) => win.Close();
        tb.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) Commit();
            else if (ev.Key == Key.Escape) win.Close();
        };

        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(label);
        root.Children.Add(tb);
        root.Children.Add(btnRow);
        win.Content = root;

        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        win.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>
    /// Updates the StatusBar session label to reflect the active session name.
    /// Called from RefreshSessionList and when group/tab changes.
    /// </summary>
    private void UpdateStatusBarSession()
    {
        if (statusSessionText is null) return;

        if (_activeGroupIndex < 0 || _activeGroupIndex >= _cliGroups.Count)
        {
            statusSessionText.Text = "no session";
            return;
        }

        var g = _cliGroups[_activeGroupIndex];
        if (_activeConsoleTab < 0 || _activeConsoleTab >= _consoleTabs.Count)
        {
            statusSessionText.Text = g.DisplayName;
            return;
        }

        var t = _consoleTabs[_activeConsoleTab];
        statusSessionText.Text = $"{t.Title} — {g.DisplayName}";
    }

    /// <summary>
    /// Updates the StatusBar bot-status label. Called when the bot window is
    /// shown/hidden so the status reflects the current bot visibility.
    /// </summary>
    private void UpdateStatusBarBot()
    {
        if (statusBotText is null) return;

        bool botExists = _botWindow is not null && _botWindow.IsLoaded;
        bool embeddedVisible = botExists && _isBotEmbedded && BotDockRow.Height.Value > 0;
        bool floatingVisible = botExists && !_isBotEmbedded && _botWindow!.IsVisible;
        bool isActive = embeddedVisible || floatingVisible;

        string label = !botExists ? "⚡ Bot: Inactive"
            : _isBotEmbedded ? (embeddedVisible ? "⚡ Bot: Docked" : "⚡ Bot: Hidden")
            : (floatingVisible ? "⚡ Bot: Floating" : "⚡ Bot: Hidden");

        statusBotText.Text = label;
        statusBotText.Foreground = isActive
            ? (System.Windows.Media.Brush)FindResource("CyberPurpleBrush")
            : (System.Windows.Media.Brush)FindResource("TextDim");
    }

    /// <summary>
    /// Handler for the "+ New Session" button in the SidePanel. Delegates to the
    /// existing add-console flow which opens the CLI-type context menu.
    /// </summary>
    private void OnNewSessionClick(object sender, RoutedEventArgs e)
        => OnAddConsoleClick(sender, e);
}
