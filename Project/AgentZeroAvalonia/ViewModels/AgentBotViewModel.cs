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

public enum ChatItemKind { User, Bot, System, Tool, Progress }

/// <summary>One line of the chat: a bubble, a system notice, a tool card or the live progress row.</summary>
public partial class ChatItem : ObservableObject
{
    public ChatItemKind Kind { get; }
    public string Label { get; }
    [ObservableProperty] private string _text;
    [ObservableProperty] private string? _detail;
    public DateTime Time { get; } = DateTime.Now;

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

    /// <summary>Hooks the shell provides: the active terminal, its label, the workspace list, the active folder.</summary>
    public Func<ITerminalSession?>? ActiveSession { get; set; }
    public Func<string?>? ActiveSessionLabel { get; set; }
    public Func<IReadOnlyList<ICliGroupInfo>>? Groups { get; set; }
    public Func<string?>? ActiveDirectory { get; set; }

    /// <summary>Marshals actor callbacks to the UI thread; the tests leave it synchronous.</summary>
    public Action<Action> Post { get; set; } = a => a();

    /// <summary>Raised after an item is added — the view scrolls to it.</summary>
    public event Action? ItemAdded;

    private IActorRef? _bot;
    private bool _loopWired;
    private string? _pendingAiRequest;
    private bool _attaching;
    private ChatItem? _progress;
    private System.Diagnostics.Stopwatch? _aiStopwatch;

    public string ModeLabel => ChatModeCycle.Label(Mode);
    public bool IsAiMode => Mode == ChatMode.Ai;
    public bool IsKeyMode => Mode == ChatMode.Key;

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
        var next = ChatModeCycle.Next(Mode, aiAvailable);
        Mode = next;
        AddSystem(ModeLabel switch
        {
            "CHT" => "CHT : Terminal send mode",
            "KEY" => "KEY : Key send mode",
            _ => "AI : agent mode (input → tool loop)",
        });
    }

    [RelayCommand]
    public void Send()
    {
        var text = Input;
        if (string.IsNullOrWhiteSpace(text)) return;
        Input = "";
        switch (Mode)
        {
            case ChatMode.Chat: SendToTerminal(text); break;
            case ChatMode.Key: SendKeyName(text.Trim()); break;
            case ChatMode.Ai: StartAi(text); break;
        }
    }

    [RelayCommand]
    public void NewSession()
    {
        _bot?.Tell(new ResetAgentLoopMemory());
        AiBusy = false;
        RemoveProgress();
        AddSystem("↻ New session — agent memory and introductions cleared.");
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!AiBusy) return;
        _bot?.Tell(new CancelAgentLoop());
        AddSystem("■ Cancel requested.");
    }

    // ── CHT / KEY ────────────────────────────────────────────────────────────

    private void SendToTerminal(string text)
    {
        var session = ActiveSession?.Invoke();
        if (session is null)
        {
            AddSystem("No active terminal — open or select a terminal tab and try again.");
            return;
        }
        Add(ChatItemKind.User, ActiveSessionLabel?.Invoke() ?? session.SessionId, text);
        session.NoteInputAttempt("bot");
        session.WriteAndEnter(text);
    }

    /// <summary>KEY mode: a key alias (<c>esc</c>, <c>ctrlc</c>, <c>up</c>…) or literal characters.</summary>
    private void SendKeyName(string key)
    {
        var session = ActiveSession?.Invoke();
        if (session is null)
        {
            AddSystem("No active terminal.");
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

    private void StartAi(string request, bool echoUser = true)
    {
        if (echoUser) Add(ChatItemKind.User, "AI", request);
        if (AiBusy)
        {
            AddSystem("⏳ A previous AI turn is still running. Press ■ to cancel or ↻ to reset, or wait for it to finish.");
            return;
        }
        if (AgentLoopWiring.Unavailability() is { } why)
        {
            AddSystem(why);
            return;
        }
        if (_bot is null)
        {
            // First use: the bot actor is created asynchronously; run this request once it is.
            _pendingAiRequest = request;
            AttachActors();
            AddSystem("Starting the agent...");
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
                    Add(ChatItemKind.Tool, "🔧 " + call.Tool, Compact(call.ArgsJson, 160), Compact(call.Result, 1200));
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
            AddSystem($"failed after {elapsed}ms · {r.TurnCount} turn(s)");
        }
        AppLogger.Log($"[AIMODE] result success={r.Success} turns={r.TurnCount} elapsed={elapsed}ms"
                      + (r.Success ? "" : $" reason=\"{r.FailureReason ?? r.FinalMessage}\""));
        AiBusy = false;
        _aiStopwatch = null;
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

    public void AddSystem(string text) => Add(ChatItemKind.System, "", text);

    private ChatItem Add(ChatItemKind kind, string label, string text, string? detail = null)
    {
        var item = new ChatItem(kind, label, text, detail);
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

    private static string Compact(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", "");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
