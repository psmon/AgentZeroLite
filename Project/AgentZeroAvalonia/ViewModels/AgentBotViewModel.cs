using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Akka.Actor;
using Agent.Common;
using Agent.Common.Actors;
using Agent.Common.Agents;
using Agent.Common.Llm;
using Agent.Common.Module;
using Agent.Common.Services;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

public enum ChatItemKind
{
    User, Bot, System, Tool, Progress,
    /// <summary>Text the agent wrote into a terminal (M0041).</summary>
    TerminalOut,
    /// <summary>Text the agent read back from a terminal (M0041).</summary>
    TerminalIn,
    /// <summary>A link the terminal printed; clicking it opens a browser (M0041).</summary>
    Url,
}

/// <summary>One line of the chat: a bubble, a system notice, a tool card or the live progress row.</summary>
public partial class ChatItem : ObservableObject
{
    public ChatItemKind Kind { get; }
    public string Label { get; }
    [ObservableProperty] private string _text;
    [ObservableProperty] private string? _detail;
    public DateTime Time { get; } = DateTime.Now;

    /// <summary>The link for <see cref="ChatItemKind.Url"/> items; null otherwise.</summary>
    public string? Url { get; init; }

    public ChatItem(ChatItemKind kind, string label, string text, string? detail = null)
    {
        Kind = kind;
        Label = label;
        _text = text;
        _detail = detail;
    }

    public bool IsUser => Kind == ChatItemKind.User;
    public bool IsBot => Kind == ChatItemKind.Bot;
    public bool IsSystem => Kind == ChatItemKind.System;
    public bool IsTool => Kind == ChatItemKind.Tool;
    public bool IsProgress => Kind == ChatItemKind.Progress;
    public bool IsTerminalOut => Kind == ChatItemKind.TerminalOut;
    public bool IsTerminalIn => Kind == ChatItemKind.TerminalIn;
    public bool IsUrl => Kind == ChatItemKind.Url;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public string TimeLabel => Time.ToString("HH:mm");
}

/// <summary>
/// The AgentBot pane (M0037): the WPF <c>AgentBotWindow</c> as a view model. Three modes
/// cycle as in the WPF host (<see cref="ChatModeCycle"/>): CHT types into the active
/// terminal, KEY sends keys, AI hands the text to the agent loop and renders its progress
/// as cards. The actor side is the same topology — <c>CreateBot</c>, <c>SetBotUiCallback</c>,
/// <c>SetAgentLoopCallbacks</c> + <c>AgentLoopBindings</c>, <c>StartAgentLoop</c>.
/// The progress/result handlers are plain methods so the tests can drive them without a UI.
/// </summary>
public partial class AgentBotViewModel : ObservableObject
{
    public ObservableCollection<ChatItem> Items { get; } = new();

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private ChatMode _mode = ChatMode.Chat;
    [ObservableProperty] private bool _aiBusy;
    [ObservableProperty] private string _statusLine = "CHT · type into the active terminal";

    // ── M0041: options bar, session header, composer state ───────────────────

    [ObservableProperty] private bool _optionsExpanded;
    [ObservableProperty] private bool _miniKeysExpanded;
    [ObservableProperty] private bool _autoApprove;
    [ObservableProperty] private string _autoApproveDelayText = "0";
    [ObservableProperty] private bool _hideSystemMessages = true;
    [ObservableProperty] private string _sessionGroup = "";
    [ObservableProperty] private string _sessionTab = "";
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string? _modeToast;
    [ObservableProperty] private double _inputMaxHeight = 80;
    [ObservableProperty] private string? _attachmentTag;

    /// <summary>The approval overlay. Owned here so the pane and the floating window share it.</summary>
    public ApprovalToastViewModel Approval { get; }

    /// <summary>Hooks the shell provides: the active terminal, its label, the workspace list, the active folder.</summary>
    public Func<ITerminalSession?>? ActiveSession { get; set; }
    public Func<string?>? ActiveSessionLabel { get; set; }
    public Func<IReadOnlyList<ICliGroupInfo>>? Groups { get; set; }
    public Func<string?>? ActiveDirectory { get; set; }

    /// <summary>Opens a link in the OS browser. Set by the shell; null in tests.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Marshals actor callbacks to the UI thread; the tests leave it synchronous.</summary>
    public Action<Action> Post { get; set; } = a => a();

    /// <summary>Injected clock for toasts and auto-approve delays; the tests make it instant.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, c) => Task.Delay(t, c);

    /// <summary>
    /// Overrides how messages reach the bot actor. The tests set it to record; in the app it
    /// stays null and the real <see cref="IActorRef"/> is used.
    /// </summary>
    public Action<object>? BotTellOverride { get; set; }

    /// <summary>Raised after an item is added — the view scrolls to it.</summary>
    public event Action? ItemAdded;

    private IActorRef? _bot;
    private bool _loopWired;
    private string? _pendingAiRequest;
    private bool _attaching;
    private ChatItem? _progress;
    private System.Diagnostics.Stopwatch? _aiStopwatch;

    private AgentEventStream? _eventStream;
    private ITerminalSession? _streamSession;
    private readonly BotSessionAnnouncer _announcer = new();
    private readonly UrlNoticeThrottle _urlThrottle = new();
    private ClipboardAttachment? _attachment;
    private CancellationTokenSource? _modeToastCts;

    public string ModeLabel => ChatModeCycle.Label(Mode);
    public bool IsAiMode => Mode == ChatMode.Ai;
    public bool IsKeyMode => Mode == ChatMode.Key;
    public bool HasAttachment => _attachment is not null;

    public AgentBotViewModel()
    {
        Approval = new ApprovalToastViewModel { Post = a => Post(a) };
        Approval.OptionSelected += OnApprovalOptionSelected;
    }

    /// <summary>The delay box, clamped. Invalid text keeps the previous value, as in the WPF host.</summary>
    public int AutoApproveDelaySeconds { get; private set; }

    partial void OnAutoApproveDelayTextChanged(string value)
    {
        if (BotOptions.TryParseDelay(value) is { } seconds) AutoApproveDelaySeconds = seconds;
    }

    partial void OnAutoApproveChanged(bool value)
        => AddSystem(value
            ? $"Auto-approve enabled (delay: {AutoApproveDelaySeconds}s). Approval prompts will be accepted automatically."
            : "Auto-approve disabled.");

    partial void OnModeChanged(ChatMode value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(IsAiMode));
        OnPropertyChanged(nameof(IsKeyMode));
        StatusLine = value switch
        {
            ChatMode.Chat => "CHT · type into the active terminal",
            ChatMode.Key => "KEY · keys go straight to the terminal",
            _ => "AI · " + AgentLoopWiring.ActiveModelLabel(),
        };
    }

    // ── actors ───────────────────────────────────────────────────────────────

    /// <summary>Create (or find) the bot actor and register the UI callback. Idempotent.</summary>
    public void AttachActors()
    {
        if (_bot is not null || _attaching || !ActorSystemManager.IsInitialized) return;
        _attaching = true;
        try
        {
            var task = ActorSystemManager.Stage.Ask<BotCreated>(new CreateBot(), TimeSpan.FromSeconds(3));
            task.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully)
                {
                    _attaching = false;
                    AppLogger.Log("[Bot] CreateBot failed: " + t.Exception?.GetBaseException().Message);
                    return;
                }
                Post(() =>
                {
                    _attaching = false;
                    if (_bot is not null) return;
                    _bot = t.Result.BotRef;
                    _bot.Tell(new SetBotUiCallback((text, type) => Post(() =>
                    {
                        if (type == BotResponseType.System) AddSystem(text);
                        else if (type == BotResponseType.Error) AddSystem("⚠ " + text);
                        else Add(ChatItemKind.Bot, "Bot", text);
                    })), ActorRefs.NoSender);
                    AppLogger.Log($"[Bot] actor attached: {_bot.Path}");
                    if (_pendingAiRequest is { } pending)
                    {
                        _pendingAiRequest = null;
                        StartAi(pending, echoUser: false);
                    }
                });
            });
        }
        catch (Exception ex)
        {
            _attaching = false;
            AppLogger.Log($"[Bot] attach failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TellBot(object message)
    {
        if (BotTellOverride is { } custom) { custom(message); return; }
        _bot?.Tell(message, ActorRefs.NoSender);
    }

    // ── M0041: the active terminal ───────────────────────────────────────────

    /// <summary>
    /// The shell calls this whenever the active terminal changes (workspace switch, tab
    /// switch, tab closed). It keeps the header current, announces the session once, and
    /// moves the <see cref="AgentEventStream"/> so approvals and links are watched on the
    /// terminal the user is actually looking at — the WPF <c>RefreshSessionInfo</c>.
    /// </summary>
    public void OnActiveSessionChanged(string? group, string? tab)
    {
        var session = ActiveSession?.Invoke();
        AttachEventStream(session);

        var botSession = session is null || group is null || tab is null
            ? null
            : new BotSession(group, tab);

        SessionGroup = botSession?.Group ?? "";
        SessionTab = botSession?.Tab ?? "";
        HasSession = botSession is not null;

        if (_announcer.Announce(botSession) is { } notice) AddSystem(notice);
    }

    private void AttachEventStream(ITerminalSession? session)
    {
        if (ReferenceEquals(_streamSession, session) && _eventStream is not null) return;

        DetachEventStream();
        if (session is null) return;

        _streamSession = session;
        _eventStream = new AgentEventStream(session);
        _eventStream.EventReceived += OnAgentEvent;
    }

    private void DetachEventStream()
    {
        if (_eventStream is not null)
        {
            _eventStream.EventReceived -= OnAgentEvent;
            _eventStream.Dispose();
            _eventStream = null;
        }
        _streamSession = null;
    }

    /// <summary>Events arrive off a pool thread; everything below runs on the UI thread.</summary>
    private void OnAgentEvent(AgentEvent evt) => Post(() => HandleAgentEvent(evt));

    /// <summary>Public for the tests — they drive it directly instead of running a PTY.</summary>
    public void HandleAgentEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case ApprovalRequested approval: HandleApprovalRequested(approval); break;
            case UrlDetected url: HandleUrlDetected(url); break;
            case ApprovalDismissed: Approval.Hide(); break;
        }
    }

    private void HandleApprovalRequested(ApprovalRequested approval)
    {
        var session = ActiveSession?.Invoke();
        if (session is null) return;

        AppLogger.Log($"[Bot] approval detected: cmd=[{approval.Command}], {approval.Options.Count} options, auto={AutoApprove}");

        if (AutoApprove)
        {
            var delaySec = AutoApproveDelaySeconds;
            AddSystem(delaySec > 0
                ? $"[Auto-Approve] {approval.Command} (in {delaySec}s...)"
                : $"[Auto-Approve] {approval.Command}");
            _ = AutoApproveAsync(session, delaySec);
            return;
        }

        var preview = approval.Command.Length > 50 ? approval.Command[..50] + "…" : approval.Command;
        AddSystem($"⚡ Approval: {(string.IsNullOrEmpty(preview) ? "unknown command" : preview)}");

        var options = approval.Options
            .Select((o, i) => new ToastOption(i, $"{o.Number}. {o.Text}"))
            .ToList();
        Approval.Show(approval.Command, options);
    }

    private async Task AutoApproveAsync(ITerminalSession session, int delaySeconds)
    {
        try
        {
            if (delaySeconds > 0)
                await Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None).ConfigureAwait(false);

            // The user may have switched auto-approve off while we waited.
            await ApprovalAutoResponder
                .SendAsync(session, optionIndex: 0, Delay, stillWanted: () => AutoApprove)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Bot] auto-approve failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnApprovalOptionSelected(int index)
    {
        var session = ActiveSession?.Invoke();
        if (session is null) return;
        _ = ApprovalAutoResponder.SendAsync(session, index, Delay);
    }

    private void HandleUrlDetected(UrlDetected evt)
    {
        if (!_urlThrottle.ShouldShow(evt.Url, DateTimeOffset.UtcNow))
        {
            AppLogger.Log($"[Bot] URL skipped (cooldown): {evt.Url}");
            return;
        }
        Add(ChatItemKind.Url, ActiveSessionLabel?.Invoke() ?? "Terminal", evt.Url, url: evt.Url);
    }

    /// <summary>Opens a URL bubble's link in the OS browser.</summary>
    [RelayCommand]
    public void OpenLink(ChatItem? item)
    {
        if (item?.Url is { Length: > 0 } url) OpenUrl?.Invoke(url);
    }

    private void EnsureLoopWiring()
    {
        if (_loopWired || _bot is null || Groups is null) return;
        _bot.Tell(new SetAgentLoopCallbacks(
            OnProgress: p => Post(() => ApplyProgress(p)),
            OnResult: r => Post(() => ApplyResult(r))));
        _bot.Tell(AgentLoopWiring.Build(Groups, () => ActiveDirectory?.Invoke()));
        _loopWired = true;
        AppLogger.Log("[AIMODE] AgentLoop wiring registered");
    }

    // ── commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    public void CycleMode()
    {
        var aiAvailable = AgentLoopWiring.Unavailability() is null;
        var wasAi = Mode == ChatMode.Ai;
        var next = ChatModeCycle.Next(Mode, aiAvailable);
        Mode = next;

        // Leaving AI mode ends the agent session, as in the WPF host — otherwise the next
        // AI turn silently continues a conversation the user thought they had walked away from.
        if (wasAi && next != ChatMode.Ai)
        {
            TellBot(new ResetAgentLoopMemory());
            AiBusy = false;
            RemoveProgress();
        }

        var notice = ModeLabel switch
        {
            "CHT" => "CHT : Terminal send mode",
            "KEY" => "KEY : Key send mode",
            _ => "AI : agent mode (input → tool loop)",
        };
        AddSystem(notice);
        ShowModeToast(notice);
    }

    [RelayCommand]
    public void Send()
    {
        var (toSend, display) = ClipboardAttachment.Compose(Input, _attachment);
        if (string.IsNullOrEmpty(toSend) || toSend == "/") return;

        Input = "";
        ClearAttachment();

        switch (Mode)
        {
            case ChatMode.Chat: SendToTerminal(toSend, display); break;
            case ChatMode.Key: SendKeyName(toSend.Trim()); break;
            case ChatMode.Ai: StartAi(toSend, displayText: display); break;
        }
    }

    // ── M0041: composer extras ───────────────────────────────────────────────

    /// <summary>Holds a large paste back as a chip instead of flooding the one-line composer.</summary>
    public void AttachClipboard(string text, int caretIndex)
    {
        _attachment = new ClipboardAttachment(text, caretIndex);
        AttachmentTag = _attachment.Tag;
        OnPropertyChanged(nameof(HasAttachment));
    }

    [RelayCommand]
    public void ClearAttachment()
    {
        if (_attachment is null) return;
        _attachment = null;
        AttachmentTag = null;
        OnPropertyChanged(nameof(HasAttachment));
    }

    /// <summary>Shows the first few hundred characters of the held paste in the transcript.</summary>
    [RelayCommand]
    public void PreviewAttachment()
    {
        if (_attachment is null) return;
        AddNotice($"📋 Clipboard preview:\n{_attachment.Preview}");
    }

    [RelayCommand] public void ToggleOptions() => OptionsExpanded = !OptionsExpanded;
    [RelayCommand] public void ToggleMiniKeys() => MiniKeysExpanded = !MiniKeysExpanded;

    /// <summary>A button on the mini key pad: <c>left</c>, <c>up</c>, <c>enter</c>, <c>esc</c>…</summary>
    [RelayCommand]
    public void SendMiniKey(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        var session = ActiveSession?.Invoke();
        if (session is null) { AddNotice("No active terminal."); return; }

        if (tag.Equals("esc", StringComparison.OrdinalIgnoreCase))
        {
            _ = KeyChordTranslator.SendEscapeSequenceAsync(session, Delay);
            return;
        }

        var control = tag.ToLowerInvariant() switch
        {
            "left" => TerminalControl.LeftArrow,
            "right" => TerminalControl.RightArrow,
            "up" => TerminalControl.UpArrow,
            "down" => TerminalControl.DownArrow,
            "enter" => TerminalControl.Enter,
            "tab" => TerminalControl.Tab,
            _ => (TerminalControl?)null,
        };
        if (control is { } c) SendControl(c);
    }

    private void ShowModeToast(string text)
    {
        _modeToastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _modeToastCts = cts;
        ModeToast = text;
        _ = HideModeToastAsync(cts.Token);
    }

    private async Task HideModeToastAsync(CancellationToken ct)
    {
        try
        {
            await Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) Post(() => ModeToast = null);
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    public void NewSession()
    {
        _bot?.Tell(new ResetAgentLoopMemory());
        AiBusy = false;
        RemoveProgress();
        AddNotice("↻ New session — agent memory and introductions cleared.");
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!AiBusy) return;
        _bot?.Tell(new CancelAgentLoop());
        AddNotice("■ Cancel requested.");
    }

    // ── CHT / KEY ────────────────────────────────────────────────────────────

    private void SendToTerminal(string text, string? displayText = null)
    {
        var session = ActiveSession?.Invoke();
        if (session is null)
        {
            AddNotice("No active terminal — open or select a terminal tab and try again.");
            return;
        }

        // "clear" wipes the screen rather than running anything — the WPF shortcut.
        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            session.NoteInputAttempt("bot");
            session.SendControl(TerminalControl.ClearScreen);
            AddSystem("Terminal screen cleared.");
            return;
        }

        Add(ChatItemKind.User, ActiveSessionLabel?.Invoke() ?? session.SessionId, displayText ?? text);
        session.NoteInputAttempt("bot");

        // Long or multi-line text needs the async write queue and a trailing Enter.
        _ = TerminalTextSender.SendAsync(session, text, Delay);

        // The actor layer sees the same input — the WPF host sends this alongside the write.
        TellBot(new UserInput(text));
    }

    /// <summary>KEY mode: a key alias (<c>esc</c>, <c>ctrlc</c>, <c>up</c>…) or literal characters.</summary>
    private void SendKeyName(string key)
    {
        var session = ActiveSession?.Invoke();
        if (session is null)
        {
            AddNotice("No active terminal.");
            return;
        }
        var seq = Cli.CliCommandRouter.KeySequence(key.ToLowerInvariant());
        if (seq.Length == 0) seq = key;
        session.NoteInputAttempt("bot");
        session.Write(seq.AsSpan());
        Add(ChatItemKind.User, "KEY", key);
    }

    /// <summary>KEY mode from the view: a control key pressed in the input box.</summary>
    public void SendControl(TerminalControl control)
    {
        var session = ActiveSession?.Invoke();
        if (session is null) return;
        session.NoteInputAttempt("bot");
        session.SendControl(control);
    }

    /// <summary>KEY mode from the view: characters typed in the input box go straight through.</summary>
    public void SendRaw(string text)
    {
        var session = ActiveSession?.Invoke();
        if (session is null || string.IsNullOrEmpty(text)) return;
        session.NoteInputAttempt("bot");
        session.Write(text.AsSpan());
    }

    // ── AI ───────────────────────────────────────────────────────────────────

    /// <summary>Switch to AI mode and start a turn — the CLI's <c>bot-ask</c>.</summary>
    public void AskAi(string request)
    {
        if (Mode != ChatMode.Ai) Mode = ChatMode.Ai;
        StartAi(request);
    }

    private void StartAi(string request, bool echoUser = true, string? displayText = null)
    {
        if (echoUser) Add(ChatItemKind.User, "AI", displayText ?? request);
        if (AiBusy)
        {
            AddNotice("⏳ A previous AI turn is still running. Press ■ to cancel or ↻ to reset, or wait for it to finish.");
            return;
        }
        if (AgentLoopWiring.Unavailability() is { } why)
        {
            AddNotice(why);
            return;
        }
        if (_bot is null)
        {
            // First use: the bot actor is created asynchronously; run this request once it is.
            _pendingAiRequest = request;
            AttachActors();
            AddNotice("Starting the agent...");
            return;
        }
        EnsureLoopWiring();
        AiBusy = true;
        _aiStopwatch = System.Diagnostics.Stopwatch.StartNew();
        ShowProgress($"💭 thinking…  ·  {AgentLoopWiring.ActiveModelLabel()}");
        _bot.Tell(new StartAgentLoop(request));
        AppLogger.Log($"[AIMODE] StartAgentLoop sent | {AgentLoopWiring.ActiveModelLabel()} | len={request.Length}");
    }

    /// <summary>Thinking → spinner row; Generating → token count; Acting → a tool card.</summary>
    public void ApplyProgress(AgentLoopProgress p)
    {
        switch (p.Phase)
        {
            case AgentLoopPhase.Thinking:
                ShowProgress($"💭 thinking…  ·  {AgentLoopWiring.ActiveModelLabel()}" + (p.Round > 0 ? $"  ·  round {p.Round}" : ""));
                break;
            case AgentLoopPhase.Generating:
                ShowProgress(p.Tokens == 0
                    ? $"💭 generating…  ·  {AgentLoopWiring.ActiveModelLabel()}"
                    : $"💭 generating…  ·  {AgentLoopWiring.ActiveModelLabel()}  ·  {p.Tokens} tok");
                break;
            case AgentLoopPhase.Acting:
                if (p.ToolCall is { } call)
                {
                    RemoveProgress();
                    RenderToolTurn(call);
                    ShowProgress($"💭 thinking…  ·  {AgentLoopWiring.ActiveModelLabel()}  ·  round {p.Round}");
                }
                break;
        }
    }

    /// <summary>The final message (or the failure), then a timing line; the progress row goes away.</summary>
    public void ApplyResult(AgentLoopResult r)
    {
        RemoveProgress();
        var sw = _aiStopwatch;
        sw?.Stop();
        var elapsed = sw?.ElapsedMilliseconds ?? r.ElapsedMs;
        if (r.Success)
        {
            Add(ChatItemKind.Bot, "AgentBot", r.FinalMessage);
            AddSystem($"✓ done  ·  {elapsed}ms · {r.TurnCount} turn(s)");
        }
        else
        {
            Add(ChatItemKind.Bot, "AgentBot", "⚠ " + (r.FailureReason ?? r.FinalMessage));
            AddNotice($"failed after {elapsed}ms · {r.TurnCount} turn(s)");
        }
        AppLogger.Log($"[AIMODE] result success={r.Success} turns={r.TurnCount} elapsed={elapsed}ms"
                      + (r.Success ? "" : $" reason=\"{r.FailureReason ?? r.FinalMessage}\""));
        AiBusy = false;
        _aiStopwatch = null;
    }

    /// <summary>
    /// Draws one finished tool turn. Terminal reads and writes become exchange bubbles so a
    /// conversation with another agent reads like one; everything else stays a compact card.
    /// </summary>
    private void RenderToolTurn(AgentLoopToolCallInfo call)
    {
        switch (ToolTurnPresenter.Present(call.Tool, call.ArgsJson, call.Result))
        {
            case TerminalExchangeView x:
                Add(x.Outgoing ? ChatItemKind.TerminalOut : ChatItemKind.TerminalIn,
                    $"{x.Arrow} {TerminalLabel(x.Group, x.Tab)}",
                    Compact(x.Text, 1200));
                break;

            case WaitedView w:
                AddSystem($"⏳ waited {w.Seconds}s");
                break;

            case FailedView f:
                AddSystem($"⚙ {f.Detail}");
                break;

            default:
                Add(ChatItemKind.Tool, "🔧 " + call.Tool, Compact(call.ArgsJson, 160), Compact(call.Result, 1200));
                break;
        }
    }

    /// <summary>Names a terminal the way the user sees it, falling back to bare indices.</summary>
    private string TerminalLabel(int group, int tab)
    {
        try
        {
            var groups = Groups?.Invoke();
            if (groups is not null && group >= 0 && group < groups.Count)
            {
                var tabs = groups[group].TabsView;
                if (tabs is not null && tab >= 0 && tab < tabs.Count)
                    return $"{tabs[tab].Title} (T{group}:{tab})";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Bot] terminal label lookup failed: {ex.GetType().Name}");
        }
        return $"Terminal ({group}:{tab})";
    }

    // ── bot-chat (peer → bot) ────────────────────────────────────────────────

    private static readonly Regex DoneWithFrom = new(@"^DONE\((?<from>[^,]+),\s*(?<msg>.+)\)$", RegexOptions.Singleline);
    private static readonly Regex DoneSimple = new(@"^DONE\((?<msg>.+)\)$", RegexOptions.Singleline);

    /// <summary>
    /// A message from the CLI (<c>bot-chat</c>): shown in the chat and forwarded to the bot
    /// actor as a peer signal so an active agent loop can react — the WPF <c>HandleBotChat</c>.
    /// </summary>
    public void ReceiveExternalChat(string from, string message)
    {
        string? doneFrom = null, doneMsg = null;
        var m = DoneWithFrom.Match(message);
        if (m.Success)
        {
            doneFrom = m.Groups["from"].Value.Trim();
            doneMsg = m.Groups["msg"].Value.Trim();
        }
        else
        {
            var s = DoneSimple.Match(message);
            if (s.Success)
            {
                doneFrom = from;
                doneMsg = s.Groups["msg"].Value.Trim();
            }
        }
        if (doneFrom is not null && doneMsg is not null)
            Add(ChatItemKind.Bot, $"DONE({doneFrom})", doneMsg);
        else
            Add(ChatItemKind.Bot, from, message);

        var peer = doneFrom ?? from;
        var payload = doneMsg ?? message;
        try
        {
            if (ActorSystemManager.IsInitialized)
                ActorSystemManager.System.ActorSelection("/user/stage/bot").Tell(new TerminalSentToBot(peer, payload));
            AppLogger.Log($"[IPC] peer signal forwarded to Bot: peer=\"{peer}\" len={payload.Length}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[IPC] peer signal forward FAILED: {ex.Message}");
        }
    }

    // ── items ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Background chatter — auto-approve lines, session banners, mode changes, timings.
    /// Suppressed while <see cref="HideSystemMessages"/> is on, which is the default, as in
    /// the WPF host: these otherwise bury the conversation.
    /// </summary>
    public void AddSystem(string text)
    {
        if (HideSystemMessages) return;
        Add(ChatItemKind.System, "", text);
    }

    /// <summary>
    /// Something the user asked for or must act on — "no active terminal", a failure, a
    /// cancel. Always shown.
    /// </summary>
    /// <remarks>
    /// The WPF host routes these through the same suppressed path and then works around the
    /// confusion with a log line (<c>AgentBotWindow.xaml.cs:1000</c> notes that a hidden
    /// "no active terminal" makes the bot look broken). It already has bypass paths for
    /// conversation data, so this splits the two cases instead of inheriting the trap.
    /// </remarks>
    public void AddNotice(string text) => Add(ChatItemKind.System, "", text);

    private ChatItem Add(ChatItemKind kind, string label, string text, string? detail = null, string? url = null)
    {
        var item = new ChatItem(kind, label, text, detail) { Url = url };
        Items.Add(item);
        ItemAdded?.Invoke();
        return item;
    }

    private void ShowProgress(string text)
    {
        if (_progress is null)
        {
            _progress = Add(ChatItemKind.Progress, "", text);
            return;
        }
        _progress.Text = text;
    }

    private void RemoveProgress()
    {
        if (_progress is null) return;
        Items.Remove(_progress);
        _progress = null;
    }

    /// <summary>Drops the terminal subscription — the shell calls this on shutdown.</summary>
    public void Detach()
    {
        DetachEventStream();
        _modeToastCts?.Cancel();
        _modeToastCts = null;
        Approval.Hide();
    }

    private static string Compact(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", "");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
