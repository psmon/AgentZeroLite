using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Akka.Actor;
using Agent.Common.Llm;
using AgentZeroWpf.Actors;
using AgentZeroWpf.Services;
using Regex = System.Text.RegularExpressions.Regex;
using RegexOptions = System.Text.RegularExpressions.RegexOptions;
using Match = System.Text.RegularExpressions.Match;

namespace AgentZeroWpf.UI.APP;

public enum ChatMode { Chat, Key, Ai }

public partial class AgentBotWindow : Window
{
    private readonly Func<string>? _getSessionName;
    private readonly Func<ITerminalSession?>? _getActiveSession;
    private readonly Func<string?>? _getActiveDirectory;
    private readonly Func<IReadOnlyList<Module.CliGroupInfo>>? _getGroups;

    // ── AIMODE state ──
    // Inference itself lives in AgentReactorActor at /user/stage/bot/reactor.
    // The window only owns the UI-side bookkeeping needed to:
    //   - guard against double-sends while a turn is in flight,
    //   - track the per-send stopwatch for the "X turns in Yms" footer,
    //   - remember which model/template the active session uses for labels.
    private bool _aiSendInFlight;
    private System.Diagnostics.Stopwatch? _aiSendStopwatch;
    private string? _aiActiveChatFamily;     // "gemma" / "llama31" — for bubble label
    private bool _aiReactorRegistered;       // SetReactorCallbacks + Bindings sent?

    // Akka Actor 연동
    private IActorRef? _botActorRef;
    public void SetBotActorRef(IActorRef? botRef) => _botActorRef = botRef;

    /// <summary>
    /// Hide/show the internal title bar. MainWindow toggles this when docking
    /// the bot into its own layout (embedded) vs showing as a floating window.
    /// </summary>
    public void SetEmbeddedMode(bool embedded)
    {
        if (titleBarRow is null) return;
        titleBarRow.Height = embedded ? new GridLength(0) : new GridLength(32);
    }

    /// <summary>Detach the inner visual tree so it can be reparented elsewhere.</summary>
    public FrameworkElement? DetachContent()
    {
        var root = Content as FrameworkElement;
        Content = null;
        return root;
    }

    /// <summary>Re-attach previously-detached content as this window's child.</summary>
    public void AttachContent(FrameworkElement root)
    {
        Content = root;
    }

    // Synced skill macros
    private readonly List<SlashCommand> _syncedSkills = [];
    private int _slashSelectedIndex = -1;
    private bool _isSlashMode;
    private string? _currentWorkspaceDir;  // tracks workspace for cache switching

    // Welcome messages
    private static readonly string[] WelcomeTemplates =
    [
        "Ah, {0}/{1}! My favorite terminal. Don't tell the others.",
        "Welcome to {0}/{1}! Buckle up, it's gonna be a wild ride.",
        "{0}/{1} activated! The electrons are tingling with excitement.",
        "Behold! {0}/{1} has entered the arena. *crowd goes wild*",
        "Loading {0}/{1}... Just kidding, it's already here. I'm fast like that.",
        "{0}/{1} reporting for duty! All systems nominal... probably.",
        "You picked {0}/{1}? Excellent taste. Chef's kiss.",
        "Switching to {0}/{1}! Hold my bytes, we're going in.",
        "{0}/{1} online! May your commands be bug-free and your logs be clean.",
        "Welcome aboard {0}/{1}! Please keep your hands inside the terminal at all times.",
        "{0}/{1} is alive! It whispered 'finally' when you clicked it.",
        "Entering {0}/{1}... *dramatic hacker music intensifies*",
    ];
    private static readonly Random _welcomeRng = new();
    private string? _lastWelcomeSession;

    // Clipboard attachment (large paste)
    private string? _clipboardAttachment;
    private int _clipboardInsertPos;          // caret position at paste time
    private const int ClipboardPasteThreshold = 200;

    // Chat mode (cycled via Shift+Tab): Chat → Key → Ai → Chat.
    // Ai is skipped in the cycle when no on-device LLM is loaded
    // (LlmService.Llm == null). See CycleChatMode + UpdateChatModeBadge.
    private static ChatMode s_lastChatMode = ChatMode.Chat;
    private ChatMode _chatMode = s_lastChatMode;

    // MD file attachments
    private readonly List<string> _attachedMdFiles = [];
    private const int MaxMdAttachments = 3;
    private int _mdPickerSelectedIndex = -1;

    // Input resize drag
    private bool _isResizingInput;
    private double _resizeStartY;
    private double _resizeStartMaxH;

    // Approval watcher — event-driven via AgentEventStream
    private AgentEventStream? _eventStream;
    private ITerminalSession? _currentSession;

    private bool _autoApprove;
    private int _autoApproveDelaySec = 5;

    public AgentBotWindow(
        Func<string>? getSessionName,
        Func<ITerminalSession?>? getActiveSession,
        Func<string?>? getActiveDirectory = null,
        Func<IReadOnlyList<Module.CliGroupInfo>>? getGroups = null,
        Func<int>? getActiveGroupIndex = null)
    {
        InitializeComponent();
        _getSessionName = getSessionName;
        _getActiveSession = getActiveSession;
        _getActiveDirectory = getActiveDirectory;
        _getGroups = getGroups;

        System.Windows.DataObject.AddPastingHandler(txtInput, OnInputPasting);
        txtInput.PreviewTextInput += OnInputTextComposition;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ThemeHelper.ApplyDarkTitleBar(this);
        RefreshSessionInfo();

        // Apply the remembered mode badge (default AI, or whatever user last selected this session)
        UpdateChatModeBadge();

        // Wire approval toast events
        approvalToast.OptionSelected += OnApprovalToastOption;

        // Load cached skills from .agent-zero/
        LoadCachedSkills();

        // Flush any URL events that arrived before window was ready
        FlushPendingUrls();
        AppLogger.Log($"[AgentBot] Window loaded, pending URLs flushed");
    }

    private void OnApprovalToastOption(int optionIndex)
    {
        var session = _getActiveSession?.Invoke();
        if (session is null) return;
        SendApprovalChoice(session, optionIndex);
        AddSystemMessage($"Approved (option {optionIndex + 1})");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DetachEventStream();
        DisposeVoiceRuntime();
    }

    /// <summary>
    /// Attach the AgentEventStream to the current session.
    /// Called when session changes (tab switch, refresh).
    /// </summary>
    private void AttachEventStream(ITerminalSession session)
    {
        if (_currentSession == session && _eventStream != null) return;

        DetachEventStream();

        _currentSession = session;
        _eventStream = new AgentEventStream(session);
        _eventStream.EventReceived += OnAgentEvent;
    }

    private void DetachEventStream()
    {
        if (_eventStream != null)
        {
            _eventStream.EventReceived -= OnAgentEvent;
            _eventStream.Dispose();
            _eventStream = null;
        }
        _currentSession = null;
    }

    /// <summary>
    /// Handles semantic agent events (approval detected, etc.) from the event stream.
    /// Dispatches to UI thread since events come from ThreadPool timer.
    /// </summary>
    private void OnAgentEvent(AgentEvent evt)
    {
        Dispatcher.BeginInvoke(() => HandleAgentEventOnUI(evt));
    }

    private void HandleAgentEventOnUI(AgentEvent evt)
    {
        switch (evt)
        {
            case ApprovalRequested approval:
                HandleApprovalRequested(approval);
                break;
            case UrlDetected urlEvt:
                HandleUrlDetected(urlEvt);
                break;
        }
    }

    private void HandleApprovalRequested(ApprovalRequested approval)
    {
        var session = _getActiveSession?.Invoke();
        if (session is null) return;

        AppLogger.Log($"[AgentBot] Approval detected: cmd=[{approval.Command}], {approval.Options.Count} options, auto={_autoApprove}");

        if (_autoApprove)
        {
            var delaySec = _autoApproveDelaySec;
            if (delaySec > 0)
            {
                AddSystemMessage($"[Auto-Approve] {approval.Command} (in {delaySec}s...)");
                _ = DelayedAutoApprove(session, delaySec);
            }
            else
            {
                AddSystemMessage($"[Auto-Approve] {approval.Command}");
                SendApprovalChoice(session, 0);
            }
            return;
        }

        // Show interactive approval UI in chat
        ShowApprovalPrompt(approval.Command, approval.Options);
    }

    private readonly Queue<UrlDetected> _pendingUrls = new();
    private readonly Dictionary<string, DateTimeOffset> _urlLastShown = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UrlCooldown = TimeSpan.FromSeconds(30);

    private bool IsUrlDuplicate(string url)
    {
        if (_urlLastShown.TryGetValue(url, out var lastShown)
            && DateTimeOffset.UtcNow - lastShown < UrlCooldown)
        {
            return true;
        }
        _urlLastShown[url] = DateTimeOffset.UtcNow;

        // Evict old entries
        if (_urlLastShown.Count > 100)
        {
            var cutoff = DateTimeOffset.UtcNow - UrlCooldown;
            foreach (var key in _urlLastShown.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _urlLastShown.Remove(key);
        }
        return false;
    }

    private void HandleUrlDetected(UrlDetected urlEvt)
    {
        if (IsUrlDuplicate(urlEvt.Url))
        {
            AppLogger.Log($"[AgentBot] URL skipped (cooldown 30s): {urlEvt.Url}");
            return;
        }

        AppLogger.Log($"[AgentBot] URL detected: url=[{urlEvt.Url}], IsLoaded={IsLoaded}");

        if (!IsLoaded)
        {
            _pendingUrls.Enqueue(urlEvt);
            return;
        }

        var sessionLabel = _getSessionName?.Invoke() ?? "Terminal";
        AddUrlBubble(urlEvt.Url, sessionLabel);
    }

    private void FlushPendingUrls()
    {
        while (_pendingUrls.TryDequeue(out var urlEvt))
        {
            AppLogger.Log($"[AgentBot] URL flushed (deferred): {urlEvt.Url}");
            var sessionLabel = _getSessionName?.Invoke() ?? "Terminal";
            AddUrlBubble(urlEvt.Url, sessionLabel);
        }
    }

    private static readonly SolidColorBrush UrlBubbleBg = new(Color.FromRgb(0x1A, 0x2B, 0x2B));
    private static readonly SolidColorBrush UrlBubbleHoverBg = new(Color.FromRgb(0x24, 0x3D, 0x3D));

    private void AddUrlBubble(string url, string source)
    {
        var border = new Border
        {
            Background = UrlBubbleBg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(4, 2, 40, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = "Click to open in browser",
        };

        var grid = new Grid();

        // Main content
        var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };

        // Source label
        stack.Children.Add(new TextBlock
        {
            Text = $"[{source}]",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
            Margin = new Thickness(0, 0, 0, 3),
        });

        // URL text
        stack.Children.Add(new TextBlock
        {
            Text = url,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            TextDecorations = TextDecorations.Underline,
        });

        grid.Children.Add(stack);

        // Link icon (top-right corner)
        grid.Children.Add(new TextBlock
        {
            Text = "\uE8A7",  // OpenInNewWindow
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Opacity = 0.7,
        });

        border.Child = grid;

        // Hover effect
        border.MouseEnter += (_, _) => border.Background = UrlBubbleHoverBg;
        border.MouseLeave += (_, _) => border.Background = UrlBubbleBg;

        // Entire card click → open in browser
        // Use Preview (tunneling) to avoid ScrollViewer swallowing the event
        // on the first bubble before layout is complete
        border.PreviewMouseLeftButtonUp += (_, e) =>
        {
            OpenUrl(url);
            e.Handled = true;
        };

        // Right-click menu
        var openItem = new MenuItem { Header = "Open in browser" };
        openItem.Click += (_, _) => OpenUrl(url);
        var copyItem = new MenuItem { Header = "Copy URL" };
        copyItem.Click += (_, _) => Clipboard.SetText(url);
        border.ContextMenu = new ContextMenu { Items = { openItem, copyItem } };

        pnlMessages.Children.Add(border);
        scrollChat.ScrollToEnd();
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[URL] Failed to open: {url}", ex);
        }
    }

    public void RefreshSessionInfo()
    {
        var name = _getSessionName?.Invoke() ?? "No Terminal Session";
        txtSessionInfo.Inlines.Clear();
        txtSessionInfo.Inlines.Add(new System.Windows.Documents.Run(name)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
        });

        // Attach event stream to current session
        var session = _getActiveSession?.Invoke();
        if (session != null)
        {
            AttachEventStream(session);
        }
        else
        {
            DetachEventStream();
        }

        // Reload skill cache when workspace changes
        var newDir = _getActiveDirectory?.Invoke();
        if (!string.Equals(newDir, _currentWorkspaceDir, StringComparison.OrdinalIgnoreCase))
        {
            _currentWorkspaceDir = newDir;
            LoadCachedSkills();
            AppLogger.Log($"[AgentBot] Workspace changed → {newDir ?? "null"}, skills={_syncedSkills.Count}");
        }
    }

    public void ShowWelcomeMessage(string groupName, string tabName)
    {
        var sessionKey = $"{groupName}/{tabName}";

        // Always update the pinned session header bar
        UpdateSessionHeader(groupName, tabName);

        if (sessionKey == _lastWelcomeSession) return;   // don't repeat for same tab
        _lastWelcomeSession = sessionKey;

        // Compact one-line notification in chat (like approval alerts)
        AddSystemMessage($"[Session] {groupName} / {tabName}");
    }

    private void UpdateSessionHeader(string groupName, string tabName)
    {
        txtSessionInfo.Inlines.Clear();
        txtSessionInfo.Inlines.Add(new System.Windows.Documents.Run(groupName)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)),
            FontWeight = FontWeights.Bold,
        });
        txtSessionInfo.Inlines.Add(new System.Windows.Documents.Run(" / ")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
        });
        txtSessionInfo.Inlines.Add(new System.Windows.Documents.Run(tabName)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)),
            FontWeight = FontWeights.Bold,
        });
    }

    // ------------------------------------------------------------------
    //  Title bar
    // ------------------------------------------------------------------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Request the owning window to re-embed this bot back into its layout.
    /// The owner is expected to be the MainWindow, which exposes <c>EmbedBotFromBot()</c>.
    /// </summary>
    private void OnEmbedClick(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mw)
            mw.EmbedBotFromBot();
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    private void OnToggleOptionsClick(object sender, RoutedEventArgs e)
    {
        var collapsed = pnlOptionsBar.Visibility == Visibility.Collapsed;
        pnlOptionsBar.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        // E70D = ChevronDown, E70E = ChevronUp
        btnToggleOptions.Content = collapsed ? "\uE70E" : "\uE70D";
    }

    // ------------------------------------------------------------------
    //  Chat bubbles
    // ------------------------------------------------------------------

    private void AddUserMessage(string text, string? target = null)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78)),
            CornerRadius = new CornerRadius(8, 8, 2, 8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(40, 2, 4, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        if (target is not null)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"→ {target}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xB2, 0xEB)),
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            });
            stack.Children.Add(CreateSelectableText(text, 12,
                new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3))));
            border.Child = stack;
        }
        else
        {
            border.Child = CreateSelectableText(text, 12,
                new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)));
        }

        pnlMessages.Children.Add(border);
        scrollChat.ScrollToEnd();
    }

    internal void AddSystemMessage(string text)
    {
        if (chkHideSysMsg.IsChecked == true)
            return;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4, 2, 40, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        border.Child = CreateSelectableText(text, 12,
            new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)));
        pnlMessages.Children.Add(border);
        scrollChat.ScrollToEnd();
    }

    // Live, in-place status line for AIMODE generation. Same visual as a
    // system message but its text mutates on every progress callback so the
    // chat history doesn't fill up with "🔁 generating… 64 tok / 128 tok / …".
    private TextBlock? _aiStatusText;
    private Border? _aiStatusBorder;

    /// <summary>
    /// Show or update the single AIMODE status bubble with <paramref name="text"/>.
    /// Creates the bubble on first call; subsequent calls mutate the same
    /// TextBlock. Use <see cref="ClearAiGenerationStatus"/> when the loop ends.
    /// </summary>
    internal void SetAiGenerationStatus(string text)
    {
        if (chkHideSysMsg.IsChecked == true)
            return;

        if (_aiStatusText is not null)
        {
            _aiStatusText.Text = text;
            scrollChat.ScrollToEnd();
            return;
        }

        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4, 2, 40, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = tb,
        };
        pnlMessages.Children.Add(border);
        scrollChat.ScrollToEnd();
        _aiStatusText = tb;
        _aiStatusBorder = border;
    }

    /// <summary>Remove the active AIMODE status bubble (if any).</summary>
    internal void ClearAiGenerationStatus()
    {
        if (_aiStatusBorder is null) return;
        try { pnlMessages.Children.Remove(_aiStatusBorder); } catch { }
        _aiStatusBorder = null;
        _aiStatusText = null;
    }

    /// <summary>
    /// Renders a real-time AIMODE terminal exchange as a left-side chat card.
    /// Called from the AgentToolLoop's OnTurnCompleted callback for
    /// send_to_terminal (outbound, "→") and read_terminal (inbound, "←")
    /// turns so the user sees the conversation flowing live in the AgentBot
    /// window instead of having to alt-tab to the terminal.
    ///
    /// LLM-loop safety: this method only writes to <c>pnlMessages</c>.
    /// The LLM's KV cache is not touched. The next agent turn sees only the
    /// raw tool_result the host returned, never anything rendered here, so
    /// there is no risk of feedback amplification.
    ///
    /// Bypasses <c>chkHideSysMsg</c> — this is conversation data, not
    /// status noise.
    /// </summary>
    internal void AddTerminalExchangeBubble(string direction, string terminalLabel, string text)
    {
        // Truncate noisy long terminal dumps so a 5KB read doesn't overrun
        // the chat panel. Full content remains in the underlying log.
        const int maxLen = 800;
        var display = text.Length <= maxLen ? text : text[..maxLen] + "  …(truncated)";

        // Direction colour: outbound (sent to terminal) = mint green;
        // inbound (read back) = sky cyan. Same left-aligned layout, same
        // muted card body for visual unity.
        var arrowColor = direction == "→"
            ? Color.FromRgb(0x4E, 0xC9, 0xB0)   // outbound: mint
            : Color.FromRgb(0x9C, 0xDC, 0xFE);  // inbound: cyan

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{direction} {terminalLabel}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new SolidColorBrush(arrowColor),
            Margin = new Thickness(0, 0, 0, 2),
        });
        stack.Children.Add(CreateSelectableText(display, 11,
            new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0))));

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x32)),  // dark slate
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4, 2, 40, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            BorderBrush = new SolidColorBrush(arrowColor) { Opacity = 0.4 },
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
        pnlMessages.Children.Add(border);
        scrollChat.ScrollToEnd();
    }

    /// <summary>
    /// Renders an on-device LLM response as a left-aligned chat bubble (not
    /// a system message). Crucially this bypasses the
    /// <c>chkHideSysMsg</c> toggle — even with system messages hidden the
    /// model's actual answer must always be visible. Visually distinct from
    /// the user bubble (right-aligned blue) so the conversation reads
    /// naturally: user → bot → user → bot.
    /// </summary>
    internal void AddBotMessage(string text, string label)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"◂ {label}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)),  // violet — same as AI mode badge
            Margin = new Thickness(0, 0, 0, 2),
        });
        stack.Children.Add(CreateSelectableText(text, 12,
            new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3))));

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2C, 0x4E)),  // muted violet, distinct from user blue
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4, 2, 40, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = stack,
        };
        pnlMessages.Children.Add(border);
        scrollChat.ScrollToEnd();
    }


    // ------------------------------------------------------------------
    //  + Menu button
    // ------------------------------------------------------------------

    private async void OnPlusClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Log("[AgentBot +] OnPlusClick ENTER");
        try
        {
            ctxPlusMenu.Items.Clear();

            var workspaceDir = _getActiveDirectory?.Invoke();
            bool hasClaudeSkills = false;
            bool hasLiteSkill = false;
            if (!string.IsNullOrEmpty(workspaceDir))
            {
                var skillsDir = Path.Combine(workspaceDir, ".claude", "skills");
                hasClaudeSkills = Directory.Exists(skillsDir);
                var liteSkillMd = Path.Combine(skillsDir, LiteStarterSkillName, "SKILL.md");
                hasLiteSkill = File.Exists(liteSkillMd);
            }

            // Detect Claude CLI on PATH (required for Import / Sync to make sense)
            var claudeCheck = await RunShellAsync("where claude");
            bool hasClaudeCli = claudeCheck.ExitCode == 0;

            // Detect starter pack shipped next to the exe
            var starterPackDir = FindStarterPackDir();
            bool hasStarterPack = starterPackDir != null;

            AppLogger.Log(
                $"[AgentBot +] cli={hasClaudeCli} skills={hasClaudeSkills} " +
                $"liteSkill={hasLiteSkill} pack={hasStarterPack} ({starterPackDir})");

            // Import: agent-zero-lite 가 현재 워크스페이스에 아직 설치되어 있지 않을 때
            if (hasClaudeCli && hasStarterPack && !hasLiteSkill)
            {
                var miImport = new MenuItem { Header = "Import Starter Skills" };
                miImport.Click += OnImportStarterSkillsClick;
                ctxPlusMenu.Items.Add(miImport);
            }

            if (hasClaudeSkills)
            {
                var miSync = new MenuItem { Header = "Skill Sync" };
                miSync.Click += OnSkillSyncClick;
                ctxPlusMenu.Items.Add(miSync);
            }

            // AgentZeroCLI Helper: 항상 노출. 설치 없이 현재 터미널 AI에게 CLI 사용법을 1회 학습
            var miCliHelper = new MenuItem { Header = "AgentZeroCLI Helper" };
            miCliHelper.Click += OnAgentZeroCliHelperClick;
            ctxPlusMenu.Items.Add(miCliHelper);

            ctxPlusMenu.PlacementTarget = btnPlus;
            ctxPlusMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            ctxPlusMenu.IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[AgentBot +] OnPlusClick FAILED", ex);
        }
    }

    // ------------------------------------------------------------------
    //  Auto-Approve checkbox
    // ------------------------------------------------------------------

    private void OnAutoApproveChanged(object sender, RoutedEventArgs e)
    {
        _autoApprove = chkAutoApprove.IsChecked == true;
        AddSystemMessage(_autoApprove
            ? $"Auto-approve enabled (delay: {_autoApproveDelaySec}s). Approval prompts will be accepted automatically."
            : "Auto-approve disabled.");
    }

    private void OnAutoApproveDelayChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(txtAutoApproveDelay.Text, out var val) && val >= 0 && val <= 30)
            _autoApproveDelaySec = val;
    }

    private async Task DelayedAutoApprove(ITerminalSession session, int delaySec)
    {
        await Task.Delay(delaySec * 1000);
        // After delay, verify auto-approve is still enabled (user might have toggled it off)
        if (!_autoApprove) return;
        SendApprovalChoice(session, 0);
    }

    // ------------------------------------------------------------------
    //  Clipboard attachment (large paste)
    // ------------------------------------------------------------------

    private void OnInputPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText)) return;

        var text = e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) as string;
        if (text is null || text.Length <= ClipboardPasteThreshold) return;

        e.CancelCommand();
        _clipboardAttachment = text;
        _clipboardInsertPos = txtInput.CaretIndex;   // remember where the user intended to paste
        txtClipboardTag.Text = $"[클립보드 {text.Length}자]";
        pnlClipboardTag.Visibility = Visibility.Visible;
    }

    private void OnClipboardTagClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_clipboardAttachment is null) return;
        var preview = _clipboardAttachment.Length > 300
            ? _clipboardAttachment[..300] + "..."
            : _clipboardAttachment;
        AddSystemMessage($"📋 클립보드 미리보기:\n{preview}");
    }

    private void OnClipboardTagRemove(object sender, RoutedEventArgs e)
    {
        _clipboardAttachment = null;
        pnlClipboardTag.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    //  Input resize handle
    // ------------------------------------------------------------------

    private void OnResizeHandleMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isResizingInput = true;
        _resizeStartY = e.GetPosition(this).Y;
        _resizeStartMaxH = txtInput.MaxHeight;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeHandleMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isResizingInput) return;
        var delta = _resizeStartY - e.GetPosition(this).Y; // drag up = bigger
        var newMax = Math.Clamp(_resizeStartMaxH + delta, 40, 400);
        txtInput.MaxHeight = newMax;
    }

    private void OnResizeHandleMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isResizingInput) return;
        _isResizingInput = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ------------------------------------------------------------------
    //  Send (normal chat → terminal)
    // ------------------------------------------------------------------

    private void OnSendClick(object sender, RoutedEventArgs e) => SendCurrentInput();

    // ------------------------------------------------------------------
    //  Mini key buttons (send special keys to active terminal)
    // ------------------------------------------------------------------

    private void OnMiniKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        var control = tag switch
        {
            "left"  => TerminalControl.LeftArrow,
            "up"    => TerminalControl.UpArrow,
            "down"  => TerminalControl.DownArrow,
            "right" => TerminalControl.RightArrow,
            "enter" => TerminalControl.Enter,
            "tab"   => TerminalControl.Tab,
            "esc"   => TerminalControl.Escape,
            _       => (TerminalControl?)null,
        };
        if (control is null) return;

        var session = _getActiveSession?.Invoke();
        if (session is null)
        {
            AddSystemMessage("No active terminal.");
            return;
        }

        session.SendControl(control.Value);
        txtInput.Focus();
    }

    private void SendCurrentInput()
    {
        if (_isSlashMode && pnlSlashPopup.Visibility == Visibility.Visible)
        {
            SendSelectedSlashCommand();
            return;
        }

        var rawInput = txtInput.Text;
        var hasClip = _clipboardAttachment is not null;

        // Build text to send — insert clipboard content at the original paste position
        string textToSend;
        string displayText;

        if (hasClip && !string.IsNullOrEmpty(rawInput))
        {
            var pos = Math.Clamp(_clipboardInsertPos, 0, rawInput.Length);
            var before = rawInput[..pos];
            var after = rawInput[pos..];
            textToSend = before + _clipboardAttachment + after;
            displayText = $"{before}📋[클립보드 {_clipboardAttachment!.Length}자]{after}";
            AppLogger.Log($"[BOT-CLIP] rawInput='{rawInput}' ({rawInput.Length}자), clipPos={_clipboardInsertPos}, before='{before}', after='{after}', totalSend={textToSend.Length}자");
        }
        else if (hasClip)
        {
            textToSend = _clipboardAttachment!;
            displayText = $"📋[클립보드 {_clipboardAttachment!.Length}자]";
            AppLogger.Log($"[BOT-CLIP] rawInput=EMPTY, clipOnly={_clipboardAttachment!.Length}자");
        }
        else
        {
            textToSend = rawInput.Trim();
            displayText = rawInput.Trim();
        }

        if (string.IsNullOrEmpty(textToSend) || textToSend == "/") return;

        // ── AI MODE ────────────────────────────────────────────────────────
        // Route the input through the on-device tool loop instead of the
        // terminal. The loop will use list_terminals / read_terminal /
        // send_to_terminal / send_key / done to act on the workspace.
        if (_chatMode == ChatMode.Ai)
        {
            var aiInput = textToSend;
            txtInput.Clear();
            _ = SendThroughAiToolLoopAsync(aiInput, displayText);
            _clipboardAttachment = null;
            pnlClipboardTag.Visibility = Visibility.Collapsed;
            return;
        }
        // ───────────────────────────────────────────────────────────────────

        var session = _getActiveSession?.Invoke();
        if (session is null)
        {
            AddSystemMessage("No active terminal.");
            return;
        }

        // "clear" command: reset terminal screen without sending input
        if (textToSend.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            session.SendControl(TerminalControl.ClearScreen);
            AddSystemMessage("Terminal screen cleared.");
            txtInput.Clear();
            return;
        }

        var sessionLabel = _getSessionName?.Invoke() ?? "Terminal";
        AddUserMessage(displayText, sessionLabel);
        AppLogger.Log($"[BOT-SEND] first50='{textToSend[..Math.Min(50, textToSend.Length)]}', total={textToSend.Length}자, chunked={textToSend.Length > ClipboardPasteThreshold}, session={session.SessionId}, running={session.IsRunning}");

        // Multi-line detection: if text contains newlines, we need an extra Enter
        // because some shells treat pasted newlines as line separators within
        // a single input block and may not execute the final line.
        var isMultiLine = textToSend.Contains('\n');

        // Large text: send via async write queue (backpressure + adaptive chunking)
        if (textToSend.Length > ClipboardPasteThreshold)
        {
            _ = SendLargeTextAsync(session, textToSend, isMultiLine);
        }
        else
        {
            session.WriteAndSubmit(textToSend);
            if (isMultiLine)
                session.SendControl(TerminalControl.Enter);
        }
        txtInput.Clear();

        // 액터 시스템에 병행 전달 (기존 콜백 경로는 유지)
        _botActorRef?.Tell(new UserInput(textToSend), ActorRefs.NoSender);

        _clipboardAttachment = null;
        pnlClipboardTag.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// AI mode entry — Tell-based send through AgentBotActor → AgentReactorActor.
    /// Inference no longer runs on the UI task; this method just (a) handles
    /// lazy-load + readiness checks, (b) renders the user bubble, and
    /// (c) Tells <c>StartReactor(userRequest)</c>. The reactor pumps progress
    /// + final result back via the callbacks registered once in
    /// <see cref="EnsureReactorWiring"/>.
    /// </summary>
    private async Task SendThroughAiToolLoopAsync(string userRequest, string displayText)
    {
        AddUserMessage(displayText, "AI");

        if (_aiSendInFlight)
        {
            AddSystemMessage("⏳ A previous AI turn is still running. Press the ↻ button to reset, or wait for it to finish.");
            return;
        }

        // Backend-aware readiness gate.
        //   Local: lazy-load the GGUF if it's on disk (one-time per process);
        //   External: REST has no "load" — just verify settings + model id.
        var savedSettings = LlmSettingsStore.Load();
        if (savedSettings.ActiveBackend == Agent.Common.Llm.LlmActiveBackend.Local)
        {
            if (LlmService.Llm is null)
            {
                var savedEntry = LlmModelCatalog.FindById(savedSettings.ModelId);
                var savedModelPath = LlmModelLocator.ResolveExistingOrTarget(savedEntry);

                if (!File.Exists(savedModelPath))
                {
                    AddSystemMessage($"AI mode needs a loaded LLM, and the saved model ({savedEntry.DisplayName}) is not on disk. Open Settings → LLM, download / pick a model, then click Load.");
                    return;
                }

                AddSystemMessage($"⌛ Loading {savedEntry.DisplayName}  ·  backend={savedSettings.Backend}  ·  one-time per process");
                var loadSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await LlmService.LoadAsync(savedSettings, savedModelPath);
                    loadSw.Stop();
                    AddSystemMessage($"✓ Loaded {savedEntry.DisplayName}  ·  {loadSw.ElapsedMilliseconds}ms  ·  ready");
                    AppLogger.Log($"[AIMODE] lazy-loaded model id={savedSettings.ModelId} backend={savedSettings.Backend} elapsed={loadSw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"✗ Load failed: {ex.Message}. Open Settings → LLM for diagnostics.");
                    AppLogger.LogError("[AIMODE] lazy load failed", ex);
                    return;
                }
            }

            if (LlmService.Llm is null)
            {
                AddSystemMessage("AI mode requires a loaded LLM. Open Settings → LLM and click Load.");
                return;
            }
        }
        else
        {
            // External: validate provider+model are configured before kicking
            // the reactor. The reactor itself returns a typed failure if the
            // factory still returns null, but failing fast here gives a better
            // user message.
            if (savedSettings.CreateExternalProvider() is null
                || string.IsNullOrEmpty(savedSettings.ResolveExternalModel()))
            {
                AddSystemMessage($"AI mode (External / {savedSettings.External.Provider}) is not configured. Open Settings → LLM → External and pick a provider, model, and (for OpenAI) API key.");
                return;
            }
        }
        if (_getGroups is null)
        {
            AddSystemMessage("AI mode is not wired (host did not provide a groups callback).");
            return;
        }
        if (_botActorRef is null)
        {
            AddSystemMessage("AI mode is not wired (bot actor reference missing).");
            return;
        }

        EnsureReactorWiring();

        string displayName;
        if (savedSettings.ActiveBackend == Agent.Common.Llm.LlmActiveBackend.Local)
        {
            var entry = LlmModelCatalog.FindById(LlmService.CurrentSettings.ModelId);
            _aiActiveChatFamily = entry.ChatFamily;
            displayName = entry.DisplayName;

            if (entry.ChatFamily.Equals("llama31", StringComparison.OrdinalIgnoreCase)
                && LlmService.CurrentSettings.Backend == Agent.Common.Llm.LocalLlmBackend.Vulkan)
            {
                AddSystemMessage("ℹ️ Note: Nemotron + Vulkan + GBNF is unstable on the current llama.cpp build. If the loop stalls or returns malformed JSON, switch backend to Cpu in Settings → LLM and reload.");
            }
            AppLogger.Log($"[AIMODE] StartReactor sent (backend=Local, model={entry.Id}, family={entry.ChatFamily})");
        }
        else
        {
            _aiActiveChatFamily = "external";
            var modelId = savedSettings.ResolveExternalModel();
            displayName = $"{savedSettings.External.Provider} · {modelId}";

            // Heads-up: the AIMODE toolchain is Gemma-4-shaped. Non-Gemma
            // external models won't emit the JSON envelopes the reactor expects;
            // user gets fail-fast messages from ExternalAgentToolLoop.
            if (!modelId.Contains("gemma", StringComparison.OrdinalIgnoreCase))
            {
                AddSystemMessage($"⚠️ AIMODE toolchain is standardised on Gemma 4. Model '{modelId}' is non-Gemma — expect malformed JSON envelopes. Switch to Webnori / google/gemma-4-e4b for reliable AIMODE.");
            }
            AppLogger.Log($"[AIMODE] StartReactor sent (backend=External, provider={savedSettings.External.Provider}, model={modelId})");
        }

        SetAiGenerationStatus($"💭 thinking…  ·  {displayName}");
        _aiSendStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _aiSendInFlight = true;

        _botActorRef.Tell(new Agent.Common.Actors.StartReactor(userRequest));
    }

    /// <summary>
    /// Idempotently registers the UI-side delegates and host bindings on the
    /// AgentBotActor so it can drive the AgentReactorActor child. Called from
    /// the first SendThroughAiToolLoopAsync that has all dependencies (bot
    /// actor + getGroups). Bindings are factories (Func&lt;&gt;) so they pull
    /// fresh values per turn — settings/template/host can change between
    /// sends without re-registering.
    /// </summary>
    private void EnsureReactorWiring()
    {
        if (_aiReactorRegistered) return;
        if (_botActorRef is null || _getGroups is null) return;

        _botActorRef.Tell(new Agent.Common.Actors.SetReactorCallbacks(
            OnProgress: p => Dispatcher.BeginInvoke(new Action(() => HandleReactorProgress(p))),
            OnResult: r => Dispatcher.BeginInvoke(new Action(() => HandleReactorResult(r)))));

        var getGroups = _getGroups;
        _botActorRef.Tell(new Agent.Common.Actors.ReactorBindings(
            HostFactory: () => new WorkspaceTerminalToolHost(getGroups),
            OptionsFactory: () =>
            {
                // Options are read from whichever backend is the source of
                // truth right now: Local pulls from LlmService.CurrentSettings
                // (which mirrors the loaded model's runtime), External pulls
                // from the persisted JSON because there's no "loaded" config.
                var settings = Agent.Common.Llm.LlmSettingsStore.Load();
                if (settings.ActiveBackend == Agent.Common.Llm.LlmActiveBackend.Local)
                {
                    var s = LlmService.CurrentSettings;
                    var entry = LlmModelCatalog.FindById(s.ModelId);
                    var isLlama31 = entry.ChatFamily.Equals("llama31", StringComparison.OrdinalIgnoreCase);
                    var isVulkan = s.Backend == Agent.Common.Llm.LocalLlmBackend.Vulkan;
                    var effectiveTemp = (isLlama31 && isVulkan) ? 0.0f : s.Temperature;
                    var perTurnCap = Math.Max(256, s.AgentToolLoopMaxTokens);
                    AppLogger.Log($"[AIMODE] options: backend=Local maxTokens={perTurnCap} temp={effectiveTemp:0.00} family={entry.ChatFamily}");
                    return new Agent.Common.Llm.Tools.AgentToolLoopOptions
                    {
                        MaxTokensPerTurn = perTurnCap,
                        Temperature = effectiveTemp,
                    };
                }
                else
                {
                    var perTurnCap = Math.Max(256, settings.External.MaxTokens);
                    var effectiveTemp = settings.Temperature;
                    AppLogger.Log($"[AIMODE] options: backend=External provider={settings.External.Provider} model={settings.ResolveExternalModel()} maxTokens={perTurnCap} temp={effectiveTemp:0.00}");
                    return new Agent.Common.Llm.Tools.AgentToolLoopOptions
                    {
                        MaxTokensPerTurn = perTurnCap,
                        Temperature = effectiveTemp,
                    };
                }
            },
            ToolLoopFactory: (opts, host) =>
            {
                var settings = Agent.Common.Llm.LlmSettingsStore.Load();
                if (settings.ActiveBackend == Agent.Common.Llm.LlmActiveBackend.Local)
                {
                    var llm = LlmService.Llm as Agent.Common.Llm.LlamaSharpLocalLlm;
                    if (llm is null) return null;
                    var entry = LlmModelCatalog.FindById(LlmService.CurrentSettings.ModelId);
                    var template = entry.ChatFamily.Equals("llama31", StringComparison.OrdinalIgnoreCase)
                        ? Agent.Common.Llm.Tools.ChatTemplates.Llama31
                        : Agent.Common.Llm.Tools.ChatTemplates.Gemma;
                    return new Agent.Common.Llm.Tools.AgentToolLoop(llm, host, opts, template);
                }
                else
                {
                    var provider = settings.CreateExternalProvider();
                    var model = settings.ResolveExternalModel();
                    if (provider is null || string.IsNullOrEmpty(model)) return null;
                    return new Agent.Common.Llm.Tools.ExternalAgentToolLoop(provider, model, host, opts);
                }
            }));

        _aiReactorRegistered = true;
        AppLogger.Log("[AIMODE] Reactor wiring registered");
    }

    /// <summary>UI-side handler for ReactorProgress. Dispatches by phase.</summary>
    private void HandleReactorProgress(Agent.Common.Actors.ReactorProgress p)
    {
        switch (p.Phase)
        {
            case Agent.Common.Actors.ReactorPhase.Thinking:
                // Reactor entered Thinking — the in-place status bubble already
                // shows "💭 thinking…" from SendThroughAiToolLoopAsync; nothing
                // to do until Generating starts.
                break;

            case Agent.Common.Actors.ReactorPhase.Generating:
                {
                    var saved = LlmSettingsStore.Load();
                    var displayName = saved.ActiveBackend == Agent.Common.Llm.LlmActiveBackend.Local
                        ? LlmModelCatalog.FindById(LlmService.CurrentSettings.ModelId).DisplayName
                        : $"{saved.External.Provider} · {saved.ResolveExternalModel()}";
                    SetAiGenerationStatus(
                        p.Tokens == 0
                            ? $"💭 generating…  ·  {displayName}"
                            : $"💭 generating…  ·  {displayName}  ·  {p.Tokens} tok");
                }
                break;

            case Agent.Common.Actors.ReactorPhase.Acting:
                if (p.ToolCall is not null)
                {
                    // Re-use the existing per-turn renderer by reconstructing a
                    // ToolTurn — keeps bubble styling identical to pre-actor.
                    try
                    {
                        var argsObj = System.Text.Json.Nodes.JsonNode.Parse(p.ToolCall.ArgsJson)
                            ?.AsObject()
                            ?? new System.Text.Json.Nodes.JsonObject();
                        var fakeTurn = new Agent.Common.Llm.Tools.ToolTurn(
                            new Agent.Common.Llm.Tools.ToolCall(p.ToolCall.Tool, argsObj),
                            p.ToolCall.Result);
                        RenderToolTurnLive(fakeTurn);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogError("[AIMODE] HandleReactorProgress Acting render failed", ex);
                    }
                }
                break;

            case Agent.Common.Actors.ReactorPhase.Done:
            case Agent.Common.Actors.ReactorPhase.Error:
                // Final reckoning happens in HandleReactorResult — the
                // matching ReactorResult always follows.
                break;
        }
    }

    /// <summary>UI-side handler for ReactorResult — terminal of one StartReactor.</summary>
    private void HandleReactorResult(Agent.Common.Actors.ReactorResult r)
    {
        ClearAiGenerationStatus();
        var family = _aiActiveChatFamily ?? "gemma";
        var label = family.Equals("llama31", StringComparison.OrdinalIgnoreCase)
            ? "Nemotron"
            : "Gemma";
        var sw = _aiSendStopwatch;
        sw?.Stop();
        var elapsed = sw?.ElapsedMilliseconds ?? r.ElapsedMs;

        if (r.Success)
        {
            AddBotMessage(r.FinalMessage, label);
            AddSystemMessage($"✓ done  ·  {elapsed}ms · {r.TurnCount} turn(s)");
        }
        else
        {
            AddBotMessage($"⚠ {r.FailureReason ?? r.FinalMessage}", label);
            AddSystemMessage($"failed after {elapsed}ms · {r.TurnCount} turn(s)");
            // Same Vulkan-stall recovery hint as before.
            if (r.TurnCount == 0
                && family.Equals("llama31", StringComparison.OrdinalIgnoreCase)
                && LlmService.CurrentSettings.Backend == Agent.Common.Llm.LocalLlmBackend.Vulkan)
            {
                AddSystemMessage("→ Try: Settings → AI Mode → Backend = Cpu → Save Options → Unload → Load. CPU + Nemotron + GBNF is verified stable.");
            }
        }

        AppLogger.Log($"[AIMODE] result success={r.Success} turns={r.TurnCount} elapsed={elapsed}ms final=\"{r.FinalMessage}\"");
        _aiSendInFlight = false;
        _aiSendStopwatch = null;
    }

    private static async Task SendLargeTextAsync(ITerminalSession session, string text, bool isMultiLine)
    {
        await session.WriteAsync(text.AsMemory());
        // WriteAsync already appends \r via WriteLoopAsync.
        // For multi-line content, send an extra Enter to ensure the last line executes.
        if (isMultiLine)
        {
            await Task.Delay(100);
            session.SendControl(TerminalControl.Enter);
        }
    }

    private void SendSelectedSlashCommand()
    {
        if (_slashSelectedIndex < 0 || _slashSelectedIndex >= pnlSlashItems.Children.Count)
            return;

        var selected = (Border)pnlSlashItems.Children[_slashSelectedIndex];
        var cmdName = (string)selected.Tag;

        var session = _getActiveSession?.Invoke();
        if (session is null)
        {
            AddSystemMessage("No active terminal.");
            HideSlashPopup();
            txtInput.Clear();
            return;
        }

        var sessionLabel = _getSessionName?.Invoke() ?? "Terminal";
        AddUserMessage(cmdName, sessionLabel);
        session.WriteAndSubmit(cmdName);
        HideSlashPopup();
        txtInput.Clear();
    }

    // ==================================================================
    //  SkillSync
    // ==================================================================

    private async void OnSkillSyncClick(object sender, RoutedEventArgs e)
    {
        var session = _getActiveSession?.Invoke();
        if (session is null) { AddSystemMessage("No active terminal."); return; }

        var sessionName = _getSessionName?.Invoke() ?? "unknown";
        btnPlus.IsEnabled = false;

        AddSystemMessage("Sending /skills to terminal...");
        var logBefore = session.OutputLength;
        session.WriteAndSubmit("/skills");

        // Wait for skill list to render (interactive list needs time)
        await Task.Delay(3500);

        // ESC to close — retry up to 3 times, then Ctrl+C as fallback
        for (int attempt = 0; attempt < 3; attempt++)
        {
            session.SendControl(TerminalControl.Escape);
            await Task.Delay(500);

            // Check if output stopped growing (list closed)
            var midLen = session.OutputLength;
            await Task.Delay(300);
            if (session.OutputLength == midLen) break;
        }
        // Fallback: Ctrl+C in case ESC didn't work
        session.SendControl(TerminalControl.Interrupt);
        await Task.Delay(500);

        var logAfter = session.OutputLength;
        var rawOutput = logAfter > logBefore
            ? session.ReadOutput(logBefore, logAfter - logBefore)
            : session.GetConsoleText();

        AppLogger.Log($"[AgentBot] SkillSync raw length={rawOutput.Length}");
        // Dump raw output for regex debugging (first 2000 chars)
        var rawSnippet = rawOutput.Length > 2000 ? rawOutput[..2000] : rawOutput;
        AppLogger.Log($"[AgentBot] SkillSync raw >>>>\n{rawSnippet}\n<<<< END RAW");

        var cleanText = ApprovalParser.StripAnsiCodes(rawOutput);
        AppLogger.Log($"[AgentBot] SkillSync clean length={cleanText.Length}");
        var cleanSnippet = cleanText.Length > 2000 ? cleanText[..2000] : cleanText;
        AppLogger.Log($"[AgentBot] SkillSync clean >>>>\n{cleanSnippet}\n<<<< END CLEAN");

        var parsed = ParseSkillsFromOutput(cleanText);
        AppLogger.Log($"[AgentBot] SkillSync parsed {parsed.Count} skills");

        // Enrich descriptions from SKILL.md frontmatter in workspace
        var workspaceDir = _getActiveDirectory?.Invoke();
        if (!string.IsNullOrEmpty(workspaceDir))
            EnrichSkillDescriptions(parsed, Path.Combine(workspaceDir, ".claude", "skills"));

        foreach (var s in parsed)
            AppLogger.Log($"[AgentBot] SkillSync skill: {s.Name} — {s.Description}");

        if (parsed.Count == 0)
        {
            AddSystemMessage($"No skills detected ({rawOutput.Length} bytes). Is this a Claude Code terminal?");
            btnPlus.IsEnabled = true;
            return;
        }

        _syncedSkills.Clear();
        _syncedSkills.AddRange(parsed);

        // Persist to .agent-zero/ cache
        SaveSkillsCache(_syncedSkills, workspaceDir);

        AddSystemMessage($"Skill macros for session \"{sessionName}\" have been applied. ({_syncedSkills.Count} skills)");
        btnPlus.IsEnabled = true;
    }

    // ==================================================================
    //  Import Starter Skills (from Assets/skills-starter-pack)
    // ==================================================================

    private const string LiteStarterSkillName = "agent-zero-lite";

    private async void OnImportStarterSkillsClick(object sender, RoutedEventArgs e)
    {
        btnPlus.IsEnabled = false;

        var workspaceDir = _getActiveDirectory?.Invoke();
        if (string.IsNullOrEmpty(workspaceDir))
        {
            AddSystemMessage("No active workspace detected. Open a terminal first.");
            btnPlus.IsEnabled = true;
            return;
        }

        AddSystemMessage($"Workspace: {workspaceDir}");

        var claudeSkillsDir = Path.Combine(workspaceDir, ".claude", "skills");
        if (!Directory.Exists(claudeSkillsDir))
        {
            Directory.CreateDirectory(claudeSkillsDir);
            AddSystemMessage($"Created: {claudeSkillsDir}");
        }

        var starterPackDir = FindStarterPackDir();
        if (starterPackDir == null)
        {
            AddSystemMessage("Starter pack not found next to AgentZeroLite.exe.");
            btnPlus.IsEnabled = true;
            return;
        }
        AddSystemMessage($"Source: {starterPackDir}");

        AddSystemMessage($"Importing {LiteStarterSkillName}...");
        NotifyBot("Starter skill import started.");

        int copied = 0, skipped = 0;

        await Task.Run(() =>
        {
            var srcDir = Path.Combine(starterPackDir, LiteStarterSkillName);
            var destDir = Path.Combine(claudeSkillsDir, LiteStarterSkillName);

            if (!Directory.Exists(srcDir))
            {
                Dispatcher.Invoke(() => AddSystemMessage($"  Skip: {LiteStarterSkillName} (source missing)"));
                skipped++;
                return;
            }

            var destSkillMd = Path.Combine(destDir, "SKILL.md");
            if (Directory.Exists(destDir) && File.Exists(destSkillMd))
            {
                bool isComplete = true;
                foreach (var srcFile in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcDir, srcFile);
                    if (!File.Exists(Path.Combine(destDir, rel))) { isComplete = false; break; }
                }

                if (isComplete)
                {
                    Dispatcher.Invoke(() => AddSystemMessage($"  OK: {LiteStarterSkillName} (already complete)"));
                    skipped++;
                    return;
                }
            }

            if (Directory.Exists(destDir))
                Directory.Delete(destDir, true);

            CopyDirectory(srcDir, destDir);
            copied++;
            Dispatcher.Invoke(() => AddSystemMessage($"  Copied: {LiteStarterSkillName}"));
        });

        var summary = $"Import complete: {copied} copied, {skipped} skipped.";
        AddSystemMessage(summary);
        NotifyBot(summary);

        btnPlus.IsEnabled = true;
    }

    private void NotifyBot(string message) => ReceiveExternalChat("Setup", message);

    // ==================================================================
    //  AgentZeroCLI Helper (one-shot teaching — no skill install required)
    // ==================================================================

    private async void OnAgentZeroCliHelperClick(object sender, RoutedEventArgs e)
    {
        btnPlus.IsEnabled = false;
        try
        {
            // 1. Verify AgentZeroLite CLI is resolvable on PATH.
            //    Prefer .ps1 (the wrapper the helper text references) and fall back to .exe.
            var psCheck = await RunShellAsync("where AgentZeroLite.ps1");
            var exeCheck = psCheck.ExitCode == 0 ? psCheck : await RunShellAsync("where AgentZeroLite.exe");
            bool onPath = exeCheck.ExitCode == 0;

            if (!onPath)
            {
                AddSystemMessage(
                    "AgentZeroLite CLI가 현재 터미널의 PATH에 없습니다.\n" +
                    "  1) Settings → AgentZero CLI → Register PATH 를 눌러 경로를 등록하세요.\n" +
                    "  2) AgentZero Lite를 재시작한 뒤 이 기능을 다시 실행하세요.\n" +
                    "  (이유: 현재 터미널 세션이 이전 PATH를 캐싱하고 있어, 재시작 전에는 새로 등록된 CLI를 인식하지 못할 수 있습니다.)");
                return;
            }

            // 2. Populate the chat input with a language-agnostic teaching prompt.
            //    User reviews and hits Send themselves — this is a one-shot
            //    lesson for whichever AI is running in the active terminal.
            txtInput.Text = BuildAgentZeroCliHelperPrompt();
            txtInput.CaretIndex = txtInput.Text.Length;
            txtInput.Focus();

            AddSystemMessage(
                "AgentZeroCLI Helper 문구를 입력창에 넣었습니다. 활성 터미널의 AI에게 전송하면 " +
                "그 세션 한정으로 AgentZeroLite CLI 사용법을 학습시킬 수 있습니다.");
        }
        finally
        {
            btnPlus.IsEnabled = true;
        }
    }

    private static string BuildAgentZeroCliHelperPrompt()
    {
        return
            "[AgentZero Lite — one-shot Terminal CLI briefing]\n" +
            "\n" +
            "You are running inside a ConPTY terminal tab hosted by AgentZero Lite. " +
            "You can drive *other* terminal tabs in the same window (other AI sessions, shells, build logs, etc.) " +
            "and the AgentBot chat panel through the AgentZero Lite CLI. " +
            "The CLI is already on PATH, so you can invoke it directly — no skill install required.\n" +
            "\n" +
            "## How to invoke (shell-agnostic form first)\n" +
            "- **Any shell (bash / Git Bash / pwsh / cmd)**: `AgentZeroLite.exe -cli <command>` — call the exe directly. Works everywhere.\n" +
            "- **PowerShell only**: `AgentZeroLite.ps1 <command>` is a thin wrapper around the exe. Do NOT use `.ps1` from bash — it will be parsed as a shell script and fail.\n" +
            "- If GUI not running: `AgentZeroLite.exe -cli open-win` starts it (single-instance).\n" +
            "\n" +
            "## Core commands\n" +
            "- `AgentZeroLite.exe -cli terminal-list` — list active sessions (use the group_index / tab_index it prints)\n" +
            "- `AgentZeroLite.exe -cli terminal-send <grp> <tab> <text>` — send text + Enter to another tab\n" +
            "- `AgentZeroLite.exe -cli terminal-key  <grp> <tab> <key>` — control key: cr / esc / tab / ctrlc / up / down / hex:XX, ...\n" +
            "- `AgentZeroLite.exe -cli terminal-read <grp> <tab> [--last N]` — read terminal output (ANSI stripped)\n" +
            "- `AgentZeroLite.exe -cli bot-chat <msg> --from <name>` — show a notice in the AgentBot panel (display only, no LLM trigger)\n" +
            "- `AgentZeroLite.exe -cli status` — Lite app state\n" +
            "- Global options: `--no-wait` (fire-and-forget), `--timeout N` (ms, default 5000)\n" +
            "\n" +
            "## TUI Enter handling (important)\n" +
            "- Claude Code / bash / pwsh / cmd: `terminal-send` alone submits (text + Enter in one call)\n" +
            "- OpenAI Codex and similar: `terminal-send` → wait 1 s → `terminal-key <g> <t> cr` (two steps)\n" +
            "\n" +
            "## Typical workflow\n" +
            "1. `terminal-list` to pick a target tab → 2. `terminal-send` to deliver the instruction → " +
            "3. wait for the response (short: 1–2 s, normal: 3 s, long/code: 5–6 s, Claude Code Opus on a complex topic: 60–90 s) → " +
            "4. `terminal-read ... --last 3000` to read the result.\n" +
            "\n" +
            "## Cautions\n" +
            "- Never `terminal-send` to your own tab — it loops your own input back. The `*` marker in `terminal-list` is the active tab (yours).\n" +
            "- Lite does NOT provide mouse / keyboard / screenshot / UI-tree automation — those live in the PRO edition only.\n" +
            "- The AgentZero Lite GUI must be running for the CLI to respond.\n" +
            "\n" +
            "Use this briefing as your reference when you need to reach for the CLI. " +
            "For now, a short acknowledgement that you've read and understood is enough.";
    }

    /// <summary>Locate the skills-starter-pack directory shipped next to the exe.</summary>
    private static string? FindStarterPackDir()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.Combine(exeDir, "Assets", "skills-starter-pack");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            if (dirName == "__pycache__") continue;
            CopyDirectory(subDir, Path.Combine(destDir, dirName));
        }
    }

    private static async Task<(int ExitCode, string Output)> RunShellAsync(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (1, "Failed to start process");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    // ==================================================================
    //  Approval prompt display
    // ==================================================================

    private void ShowApprovalPrompt(string command, IReadOnlyList<ApprovalParser.ApprovalOption> options)
    {
        // Brief chat message (no buttons — keeps chat clean)
        var cmdPreview = string.IsNullOrEmpty(command)
            ? "unknown command"
            : (command.Length > 50 ? command[..50] + "…" : command);
        AddSystemMessage($"⚡ Approval: {cmdPreview}");

        // Show interactive toast overlay (10s auto-hide, closeable, mutable)
        approvalToast.Show(command, options.ToList());
    }

    private static async void SendApprovalChoice(ITerminalSession session, int optionIndex)
    {
        // Option 0 (Yes) = already selected (>) → just Enter
        // Option 1 = Down×1 then Enter
        // Option 2 = Down×2 then Enter
        for (int i = 0; i < optionIndex; i++)
        {
            session.SendControl(TerminalControl.DownArrow);
            await Task.Delay(150);
        }
        await Task.Delay(100);
        session.SendControl(TerminalControl.Enter);
    }

    // ------------------------------------------------------------------
    //  External AI trigger (from AgentCLI via tell-ai IPC)
    // ------------------------------------------------------------------

    /// <summary>
    /// Triggers the AgentBot AI LLM with an external prompt (from tell-ai CLI).
    /// Must be called on the UI thread. Fire-and-forget: returns whether the
    /// trigger was accepted; the actual LLM response streams in the chat panel.
    /// </summary>
    /// <param name="message">User prompt text (injected into AI conversation)</param>
    /// <param name="from">Sender name (displayed as [from] prefix in chat)</param>
    /// <param name="reason">Out: rejection reason when returning false</param>
    /// <returns>true if LLM stream was started, false if rejected</returns>

    // ------------------------------------------------------------------
    //  External chat (from AgentCLI via IPC)
    // ------------------------------------------------------------------

    /// <summary>
    /// Receives a chat message from an external source (e.g. AgentCLI bot-chat command).
    /// Must be called on the UI thread (MainWindow dispatches via WM_COPYDATA handler).
    /// </summary>
    public void ReceiveExternalChat(string from, string message)
    {
        Dispatcher.Invoke(() =>
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x8B, 0x4E)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(4, 2, 40, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"[{from}]",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8)),
                Margin = new Thickness(0, 0, 0, 2),
            });
            stack.Children.Add(CreateSelectableText(message, 12,
                new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4))));

            border.Child = stack;
            pnlMessages.Children.Add(border);
            scrollChat.ScrollToEnd();
        });
    }

    // ------------------------------------------------------------------
    //  ESC + Ctrl+C sequence
    // ------------------------------------------------------------------

    private async void SendEscSequence()
    {
        var session = _getActiveSession?.Invoke();
        if (session is null) return;
        session.SendControl(TerminalControl.Escape);
        await Task.Delay(300);
        session.SendControl(TerminalControl.Interrupt);
    }

    // ==================================================================
    //  VT / parsing helpers
    // ==================================================================

    private static List<SlashCommand> ParseSkillsFromOutput(string consoleText)
    {
        var skills = new List<SlashCommand>();
        if (string.IsNullOrEmpty(consoleText)) return skills;

        // Claude Code /skills format after ANSI stripping (spaces may collapse):
        //   √ on       agent-zero · project · ~181 tok      (normal)
        //   √ onagent-zero· project · ~181 tok              (spaces collapsed)
        //   √oncodescan-analysis ·project·~108tok           (all spaces collapsed)
        //   🔒 on       skill-creator:skill-creator · plugin ·~87tok
        var matches = Regex.Matches(consoleText,
            @"(?:on|off)\s*([\w][\w:.-]*(?::[\w:.-]+)?)\s*·\s*(\w+)\s*·\s*(~\d+\s*tok)",
            RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            var rawName = m.Groups[1].Value.Trim();
            var skillType = m.Groups[2].Value.Trim();   // project, plugin, user, etc.
            var tokens = m.Groups[3].Value.Trim();       // ~181 tok

            // For namespaced skills like "skill-creator:skill-creator", use the part after ':'
            var displayName = rawName.Contains(':') ? rawName.Split(':')[^1] : rawName;
            var desc = $"{skillType} · {tokens}";

            if (!skills.Any(s => s.Name.Equals("/" + displayName, StringComparison.OrdinalIgnoreCase)))
                skills.Add(new SlashCommand("/" + displayName, desc));
        }

        // Fallback: legacy format "skill-name · ~123 description tokens"
        if (skills.Count == 0)
        {
            var legacyMatches = Regex.Matches(consoleText,
                @"([a-z][a-z0-9_-]+)\s*·\s*(~\d+\s*(?:description\s*)?tokens?)",
                RegexOptions.IgnoreCase);

            foreach (Match m in legacyMatches)
            {
                var name = m.Groups[1].Value.Trim();
                var desc = m.Groups[2].Value.Trim();
                if (!skills.Any(s => s.Name.Equals("/" + name, StringComparison.OrdinalIgnoreCase)))
                    skills.Add(new SlashCommand("/" + name, desc));
            }
        }

        return skills;
    }

    /// <summary>
    /// Read SKILL.md frontmatter description for each skill and replace the token-count placeholder.
    /// Truncates to 30 chars max.
    /// </summary>
    private static void EnrichSkillDescriptions(List<SlashCommand> skills, string skillsDir)
    {
        if (!Directory.Exists(skillsDir)) return;

        foreach (var skill in skills)
        {
            // skill.Name is "/agent-zero" — strip leading '/'
            var folderName = skill.Name.TrimStart('/');
            var skillMd = Path.Combine(skillsDir, folderName, "SKILL.md");
            if (!File.Exists(skillMd)) continue;

            try
            {
                var desc = ReadSkillDescription(skillMd);
                if (!string.IsNullOrEmpty(desc))
                    skill.Description = desc.Length > 30 ? desc[..30] + "…" : desc;
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Extract the first meaningful line of the 'description' field from YAML frontmatter.
    /// </summary>
    private static string? ReadSkillDescription(string skillMdPath)
    {
        bool inFrontmatter = false;
        bool inDescription = false;

        foreach (var line in File.ReadLines(skillMdPath))
        {
            var trimmed = line.Trim();

            if (trimmed == "---")
            {
                if (!inFrontmatter) { inFrontmatter = true; continue; }
                break; // end of frontmatter
            }

            if (!inFrontmatter) continue;

            // "description: single line" or "description: |"
            if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var inline = trimmed["description:".Length..].Trim();
                if (inline.Length > 0 && inline != "|" && inline != ">")
                    return inline;
                inDescription = true;
                continue;
            }

            // Multi-line description (indented continuation)
            if (inDescription)
            {
                if (line.StartsWith(' ') || line.StartsWith('\t'))
                {
                    if (trimmed.Length > 0)
                        return trimmed; // first non-empty line
                }
                else break; // new key — description ended
            }
        }

        return null;
    }

    // ==================================================================
    //  .agent-zero/ local storage (per-workspace skill cache)
    // ==================================================================

    private const string AgentZeroDir = ".agent-zero";
    private const string SkillsCacheFile = "skills-cache.json";

    /// <summary>
    /// Resolve .agent-zero/ directory path for a workspace. Creates if needed.
    /// </summary>
    private static string? EnsureAgentZeroDir(string? workspaceDir)
    {
        if (string.IsNullOrEmpty(workspaceDir)) return null;
        var dir = Path.Combine(workspaceDir, AgentZeroDir);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    private void LoadCachedSkills()
    {
        _syncedSkills.Clear();

        var workspaceDir = _getActiveDirectory?.Invoke();
        _currentWorkspaceDir = workspaceDir;
        if (string.IsNullOrEmpty(workspaceDir)) return;

        var cacheFile = Path.Combine(workspaceDir, AgentZeroDir, SkillsCacheFile);
        if (!File.Exists(cacheFile)) return;

        try
        {
            var json = File.ReadAllText(cacheFile);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<SkillCacheEntry>>(json);
            if (entries is null || entries.Count == 0) return;

            foreach (var e in entries)
                _syncedSkills.Add(new SlashCommand(e.Name, e.Description));

            // Re-enrich descriptions from current SKILL.md (may have been updated)
            var skillsDir = Path.Combine(workspaceDir, ".claude", "skills");
            EnrichSkillDescriptions(_syncedSkills, skillsDir);

            AppLogger.Log($"[AgentBot] Loaded {_syncedSkills.Count} cached skills from {cacheFile}");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[AgentBot] Failed to load skills cache", ex);
        }
    }

    private static void SaveSkillsCache(List<SlashCommand> skills, string? workspaceDir)
    {
        var dir = EnsureAgentZeroDir(workspaceDir);
        if (dir is null) return;

        try
        {
            var entries = skills.Select(s => new SkillCacheEntry { Name = s.Name, Description = s.Description }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(entries,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, SkillsCacheFile), json);
            AppLogger.Log($"[AgentBot] Saved {entries.Count} skills to cache");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[AgentBot] Failed to save skills cache", ex);
        }
    }

    private sealed class SkillCacheEntry
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    // ==================================================================
    //  Slash popup (autocomplete)
    // ==================================================================

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = txtInput.Text;

        if (text.StartsWith('/') && !text.Contains(' ') && _syncedSkills.Count > 0)
        {
            var query = text.ToLowerInvariant();
            var matches = query == "/"
                ? _syncedSkills.ToList()
                : _syncedSkills
                    .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (matches.Count > 0) { ShowSlashPopup(matches); return; }
        }

        HideSlashPopup();
    }

    private void ShowSlashPopup(List<SlashCommand> matches)
    {
        pnlSlashItems.Children.Clear();
        _slashSelectedIndex = 0;
        _isSlashMode = true;

        for (int i = 0; i < matches.Count; i++)
        {
            var cmd = matches[i];
            var item = new Border
            {
                Padding = new Thickness(10, 5, 10, 5),
                Cursor = Cursors.Hand,
                Background = i == 0
                    ? new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78))
                    : Brushes.Transparent,
                Tag = cmd.Name,
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = cmd.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)),
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 10, 0),
            });
            sp.Children.Add(new TextBlock
            {
                Text = cmd.Description,
                Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            item.Child = sp;
            item.MouseLeftButtonDown += (_, _) =>
            {
                var session = _getActiveSession?.Invoke();
                if (session is not null)
                {
                    var slabel = _getSessionName?.Invoke() ?? "Terminal";
                    AddUserMessage(cmd.Name, slabel);
                    session.WriteAndSubmit(cmd.Name);
                }
                HideSlashPopup();
                txtInput.Clear();
            };
            pnlSlashItems.Children.Add(item);
        }

        pnlSlashPopup.Visibility = Visibility.Visible;
    }

    private void HideSlashPopup()
    {
        pnlSlashPopup.Visibility = Visibility.Collapsed;
        _slashSelectedIndex = -1;
        _isSlashMode = false;
    }

    private void UpdateSlashSelection()
    {
        for (int i = 0; i < pnlSlashItems.Children.Count; i++)
        {
            if (pnlSlashItems.Children[i] is Border b)
            {
                b.Background = i == _slashSelectedIndex
                    ? new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78))
                    : Brushes.Transparent;

                if (i == _slashSelectedIndex)
                    b.BringIntoView();
            }
        }
    }

    // ------------------------------------------------------------------
    //  Input key handling
    // ------------------------------------------------------------------

    private void CycleChatMode()
    {
        var previous = _chatMode;
        _chatMode = ChatModeCycle.Next(_chatMode, IsAiModeAvailable());
        s_lastChatMode = _chatMode;

        // AIMODE session boundary: leaving AIMODE asks the reactor actor to
        // dispose its loop + KV cache so the next entry starts fresh. Same
        // semantics as before — just behind a Tell instead of a direct call.
        if (previous == ChatMode.Ai && _chatMode != ChatMode.Ai)
        {
            RequestAiSessionReset("left AIMODE");
        }

        UpdateChatModeBadge();
    }

    /// <summary>
    /// Live per-turn renderer. Routes <c>send_to_terminal</c> and
    /// <c>read_terminal</c> turns into chat-card style bubbles so the user
    /// can read the conversation as it happens, while administrative tools
    /// (<c>list_terminals</c>, <c>send_key</c>) stay as compact system
    /// notes. Pure UI — does not feed anything back into the LLM context.
    /// </summary>
    private void RenderToolTurnLive(Agent.Common.Llm.Tools.ToolTurn turn)
    {
        var tool = turn.Call.Tool;
        var args = turn.Call.Args;

        switch (tool)
        {
            case "send_to_terminal":
            {
                int g = ReadInt(args, "group");
                int t = ReadInt(args, "tab");
                string text = ReadString(args, "text") ?? "";
                AddTerminalExchangeBubble("→", FormatTerminalLabel(g, t), text);
                break;
            }
            case "read_terminal":
            {
                int g = ReadInt(args, "group");
                int t = ReadInt(args, "tab");
                // tool_result is JSON: { ok, group_index, tab_index, length, text }
                var (ok, terminalText) = TryExtractReadTerminalText(turn.ToolResult);
                if (ok)
                {
                    AddTerminalExchangeBubble("←", FormatTerminalLabel(g, t), terminalText);
                }
                else
                {
                    AddSystemMessage($"⚙ read_terminal({{\"group\":{g},\"tab\":{t}}}) → error");
                }
                break;
            }
            case "wait":
            {
                // Compact line so a chain of waits during a long discussion
                // doesn't dominate the chat. Pull the actual seconds from
                // the tool result (it echoes the clamped value) so the user
                // sees the real wait, not just what the model requested.
                int seconds = TryExtractWaitedSeconds(turn.ToolResult)
                    ?? ReadInt(args, "seconds");
                AddSystemMessage($"⏳ waited {seconds}s");
                break;
            }
            default:
            {
                // Administrative / control tools — keep compact system note
                var argsJson = args.ToJsonString();
                AddSystemMessage($"⚙ {tool}({argsJson})");
                break;
            }
        }
    }

    private static int? TryExtractWaitedSeconds(string toolResultJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(toolResultJson);
            if (doc.RootElement.TryGetProperty("waited_seconds", out var v)
                && v.TryGetInt32(out var n))
                return n;
        }
        catch { }
        return null;
    }

    private string FormatTerminalLabel(int g, int t)
    {
        try
        {
            var groups = _getGroups?.Invoke();
            if (groups is not null && g >= 0 && g < groups.Count)
            {
                var grp = groups[g];
                if (grp.Tabs is not null && t >= 0 && t < grp.Tabs.Count)
                    return $"{grp.Tabs[t].Title} (T{g}:{t})";
            }
        }
        catch { /* fall back to bare indices */ }
        return $"Terminal ({g}:{t})";
    }

    private static int ReadInt(System.Text.Json.Nodes.JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv
            && jv.TryGetValue<int>(out var i) ? i : 0;

    private static string? ReadString(System.Text.Json.Nodes.JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var v) && v is System.Text.Json.Nodes.JsonValue jv
            && jv.TryGetValue<string>(out var s) ? s : null;

    private static (bool ok, string text) TryExtractReadTerminalText(string toolResultJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(toolResultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean()
                && root.TryGetProperty("text", out var textEl))
            {
                return (true, textEl.GetString() ?? "");
            }
        }
        catch { }
        return (false, "");
    }

    /// <summary>
    /// Tell the bot to dispose its reactor child + clear introductions. The
    /// next AIMODE send rebuilds a fresh KV cache + re-introduces terminals.
    /// Called when (a) the user cycles out of AIMODE, (b) the user clicks
    /// the New Session button, (c) the window closes.
    /// </summary>
    private void RequestAiSessionReset(string reason)
    {
        ClearAiGenerationStatus();
        _aiSendInFlight = false;
        _aiSendStopwatch = null;
        try
        {
            (_botActorRef ?? ActorSystemManager.System.ActorSelection("/user/stage/bot").Anchor)
                .Tell(new Agent.Common.Actors.ResetReactorSession());
        }
        catch { /* actor may already be torn down on app exit */ }
        AppLogger.Log($"[AIMODE] session reset requested ({reason})");
    }

    /// <summary>
    /// AI mode is available if EITHER the LLM is already loaded OR a saved
    /// model file is present on disk (lazy-load on first AIMODE send picks
    /// it up). Without this we'd refuse to enter AI mode until the user
    /// manually Loads in Settings, which defeats the lazy-load UX added
    /// alongside <see cref="SendThroughAiToolLoopAsync"/>.
    /// </summary>
    private static bool IsAiModeAvailable()
    {
        try
        {
            var saved = LlmSettingsStore.Load();
            if (saved.ActiveBackend == Agent.Common.Llm.LlmActiveBackend.Local)
            {
                if (LlmService.Llm is not null) return true;
                var entry = LlmModelCatalog.FindById(saved.ModelId);
                return File.Exists(LlmModelLocator.ResolveExistingOrTarget(entry));
            }
            // External: a configured provider + model id is enough — no
            // local artefacts on disk to check.
            return saved.CreateExternalProvider() is not null
                && !string.IsNullOrEmpty(saved.ResolveExternalModel());
        }
        catch
        {
            return false;
        }
    }

    private void UpdateChatModeBadge()
    {
        // Defensive downgrade only when AI mode is genuinely unreachable —
        // no loaded LLM AND no settings/model file the lazy loader could
        // pick up. With those present, the send-time path in
        // SendThroughAiToolLoopAsync handles the load on first use.
        var aiAvailable = IsAiModeAvailable();
        var stabilized = ChatModeCycle.StabilizeForBadge(_chatMode, aiAvailable);
        if (stabilized != _chatMode)
        {
            _chatMode = stabilized;
            s_lastChatMode = _chatMode;
            ShowModeToast("AI mode needs a model — Settings → AI Mode (download or pick one)");
            // fall through to render the Chat badge
        }

        switch (_chatMode)
        {
            case ChatMode.Chat:
                btnCycleMode.Content = "CHT";
                btnCycleMode.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
                txtInput.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
                ShowModeToast("CHT : Terminal send mode");
                break;
            case ChatMode.Key:
                btnCycleMode.Content = "KEY";
                btnCycleMode.Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF));
                txtInput.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
                ShowModeToast("KEY : Key send mode");
                break;
            case ChatMode.Ai:
                btnCycleMode.Content = "AI";
                btnCycleMode.Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)); // violet — distinct from CHT/KEY
                txtInput.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
                ShowModeToast("AI : On-device agent mode (input → tool loop)");
                break;
        }

        // The "new session" button is only meaningful in AI mode — outside
        // AIMODE there's no reactor / KV cache to clear.
        btnAiNewSession.Visibility = _chatMode == ChatMode.Ai
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Manual reset of the active AIMODE conversation. Tells the reactor to
    /// dispose its KV cache + the bot to clear introductions. Next send
    /// starts a fresh conversation. Mirrors Settings → AI Mode → New Session.
    /// </summary>
    private void OnAiNewSessionClick(object sender, RoutedEventArgs e)
    {
        RequestAiSessionReset("user clicked New Session");
        AddSystemMessage("✨ AI session reset — next send starts a clean conversation.");
    }

    private CancellationTokenSource? _modeToastCts;

    private async void ShowModeToast(string message)
    {
        _modeToastCts?.Cancel();
        _modeToastCts = new CancellationTokenSource();
        var ct = _modeToastCts.Token;

        txtModeToast.Text = message;
        pnlModeToast.Visibility = Visibility.Visible;

        try
        {
            await Task.Delay(2000, ct);
            pnlModeToast.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
    }

    private void OnCycleModeClick(object sender, RoutedEventArgs e)
        => CycleChatMode();

    private void OnToggleKeysClick(object sender, RoutedEventArgs e)
    {
        var collapsed = pnlMiniKeys.Visibility == Visibility.Collapsed;
        pnlMiniKeys.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        // E76C = ChevronRight, E76B = ChevronLeft
        btnToggleKeys.Content = collapsed ? "\uE76B" : "\uE76C";
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Shift+Tab = cycle chat mode (CHT → KEY → AI → CHT)
        if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CycleChatMode();
            e.Handled = true;
            return;
        }

        // Esc while an AI turn is running cancels it. The reactor actor
        // accepts CancelReactor and tips back to Idle; the result handler
        // clears the in-flight flag and the status bubble.
        if (e.Key == Key.Escape && _aiSendInFlight && _botActorRef is not null)
        {
            _botActorRef.Tell(new Agent.Common.Actors.CancelReactor());
            e.Handled = true;
            return;
        }

        // Slash command popup navigation
        if (_isSlashMode && pnlSlashPopup.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (_slashSelectedIndex < pnlSlashItems.Children.Count - 1)
                    { _slashSelectedIndex++; UpdateSlashSelection(); }
                    e.Handled = true; return;
                case Key.Up:
                    if (_slashSelectedIndex > 0)
                    { _slashSelectedIndex--; UpdateSlashSelection(); }
                    e.Handled = true; return;
                case Key.Tab:
                    if (_slashSelectedIndex >= 0 && _slashSelectedIndex < pnlSlashItems.Children.Count)
                    {
                        var selected = (Border)pnlSlashItems.Children[_slashSelectedIndex];
                        txtInput.Text = (string)selected.Tag;
                        txtInput.CaretIndex = txtInput.Text.Length;
                    }
                    e.Handled = true; return;
                case Key.Enter:
                    SendSelectedSlashCommand();
                    e.Handled = true; return;
                case Key.Escape:
                    HideSlashPopup(); txtInput.Clear();
                    SendEscSequence();
                    e.Handled = true; return;
            }
        }

        // ── KEY mode: forward special keys to terminal, block text input ──
        if (_chatMode == ChatMode.Key)
        {
            var session = _getActiveSession?.Invoke();
            if (session is not null)
            {
                // Navigation & control keys
                switch (e.Key)
                {
                    case Key.Escape:
                        SendEscSequence();
                        e.Handled = true; return;
                    case Key.Up:
                        session.SendControl(TerminalControl.UpArrow);
                        e.Handled = true; return;
                    case Key.Down:
                        session.SendControl(TerminalControl.DownArrow);
                        e.Handled = true; return;
                    case Key.Left:
                        session.SendControl(TerminalControl.LeftArrow);
                        e.Handled = true; return;
                    case Key.Right:
                        session.SendControl(TerminalControl.RightArrow);
                        e.Handled = true; return;
                    case Key.Tab:
                        session.SendControl(TerminalControl.Tab);
                        e.Handled = true; return;
                    case Key.Enter:
                        session.SendControl(TerminalControl.Enter);
                        e.Handled = true; return;
                    case Key.Back:
                        session.SendControl(TerminalControl.Backspace);
                        e.Handled = true; return;
                    case Key.Space:
                        session.SendControl(TerminalControl.Space);
                        e.Handled = true; return;
                    case Key.Delete:
                        session.SendControl(TerminalControl.Delete);
                        e.Handled = true; return;
                    case Key.Home:
                        session.SendControl(TerminalControl.Home);
                        e.Handled = true; return;
                    case Key.End:
                        session.SendControl(TerminalControl.End);
                        e.Handled = true; return;
                    case Key.PageUp:
                        session.SendControl(TerminalControl.PageUp);
                        e.Handled = true; return;
                    case Key.PageDown:
                        session.SendControl(TerminalControl.PageDown);
                        e.Handled = true; return;
                }

                // Ctrl combinations → send as control character (0x01–0x1A)
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key >= Key.A && e.Key <= Key.Z)
                {
                    var ctrlChar = (char)(e.Key - Key.A + 1);
                    session.Write(new ReadOnlySpan<char>(ref ctrlChar));
                    e.Handled = true; return;
                }

                // All other printable characters (letters, digits, punctuation)
                // fall through to PreviewTextInput → OnInputTextComposition.
                // Do NOT set e.Handled here — that would block PreviewTextInput.
            }
            return;
        }

        // ── NORMAL mode ──

        // Shift+Enter = newline (let AcceptsReturn handle it), Enter alone = send
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                return; // allow default newline insertion
            SendCurrentInput();
            e.Handled = true;
            return;
        }

        // Tab in NORMAL mode: block insertion (AcceptsTab is on for KEY mode capture)
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            return;
        }

        // Escape: 입력 클리어
        if (e.Key == Key.Escape)
        {
            txtInput.Clear();
            e.Handled = true;
        }
    }

    // Window-level ESC: fires regardless of which child has focus (chat area, bot card, etc.)
    // so ReAct can still be interrupted even when txtInput is not focused.

    /// <summary>
    /// KEY mode: forward all printable characters (letters, digits, punctuation)
    /// directly to the terminal. PreviewTextInput fires after WPF resolves the
    /// actual character from the keyboard layout, so ., /, [, ], !, #, = etc.
    /// are all handled automatically without individual Key enum mapping.
    /// </summary>
    private void OnInputTextComposition(object sender, TextCompositionEventArgs e)
    {
        if (_chatMode != ChatMode.Key || string.IsNullOrEmpty(e.Text)) return;

        var session = _getActiveSession?.Invoke();
        if (session is null) return;

        session.Write(e.Text.AsSpan());
        e.Handled = true;
    }

    // ==================================================================
    //  AI Mode — send to configured LLM via SSE streaming
    // ==================================================================

    private static System.Windows.Controls.TextBox CreateSelectableText(
        string text, double fontSize, System.Windows.Media.Brush foreground)
    {
        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        var selectAllItem = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
        var menu = new ContextMenu { Items = { copyItem, selectAllItem } };

        var tb = new System.Windows.Controls.TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = fontSize,
            Foreground = foreground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Cursor = Cursors.IBeam,
            IsTabStop = false,
            FocusVisualStyle = null,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x37, 0x94, 0xFF)),
            ContextMenu = menu,
        };

        copyItem.Click += (_, _) => { if (tb.SelectedText.Length > 0) Clipboard.SetText(tb.SelectedText); };
        selectAllItem.Click += (_, _) => tb.SelectAll();

        return tb;
    }


    // ------------------------------------------------------------------
    //  Data models
    // ------------------------------------------------------------------

    public sealed class SlashCommand(string name, string description)
    {
        public string Name { get; } = name;
        public string Description { get; set; } = description;
    }

    // ApprovalOption and ApprovalPrompt records are now in ApprovalParser.cs
}
