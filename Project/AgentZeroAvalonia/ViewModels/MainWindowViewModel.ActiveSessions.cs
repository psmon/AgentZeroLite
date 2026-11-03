using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Agents;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// One row of the sidebar's ACTIVE SESSIONS group: a running terminal in any workspace, with
/// the agent state detected on its screen (herdr H1/H2, the WPF SESSIONS panel's dot + chip).
/// Clicking it goes to that workspace and tab in one step.
/// </summary>
public sealed partial class ActiveSessionItem : ObservableObject
{
    public ActiveSessionItem(TerminalTabViewModel tab, string workspace, Action<TerminalTabViewModel> go)
    {
        Tab = tab;
        Workspace = workspace;
        _go = go;
        _title = tab.Title;
    }

    private readonly Action<TerminalTabViewModel> _go;

    public TerminalTabViewModel Tab { get; }
    public string Workspace { get; }

    /// <summary>
    /// The tab's title, re-read on every <see cref="Apply"/>. It used to be a plain pass-through
    /// with no change notification: a rename left the row (kept in place, since the set of rows
    /// had not changed) showing the old name while the workspace's own tab strip had the new one.
    /// </summary>
    [ObservableProperty] private string _title = "";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _stateLabel = "";
    [ObservableProperty] private IBrush _stateBrush = Brushes.Gray;
    [ObservableProperty] private string _tip = "running";
    [ObservableProperty] private bool _needsAttention;

    public bool HasStateLabel => StateLabel.Length > 0;
    partial void OnStateLabelChanged(string value) => OnPropertyChanged(nameof(HasStateLabel));

    [RelayCommand] private void Go() => _go(Tab);

    /// <summary>Dot colour, chip and tooltip for a detected state — the WPF host's AgentStateVisual.</summary>
    public void Apply(TerminalAgentState? s, bool isActive)
    {
        Title = Tab.Title;
        IsActive = isActive;
        NeedsAttention = s?.NeedsAttention == true;
        var (brush, label, tip) = Visual(s, isActive);
        StateBrush = brush;
        StateLabel = label;
        Tip = $"{Workspace} / {Title} — {tip}";
    }

    internal static (IBrush Brush, string Label, string Tip) Visual(TerminalAgentState? s, bool isActive)
    {
        static IBrush B(uint rgb) => new SolidColorBrush(Color.FromUInt32(0xFF000000 | rgb));
        if (s is null)
            return (isActive ? B(0x00D4FF) : B(0xA06CFF), "", isActive ? "active · running" : "running");
        return s.Activity switch
        {
            AgentActivity.Blocked => (B(0xE06C75), "blocked", "blocked — waiting for input or approval"),
            AgentActivity.Working => (B(0xE5C07B), "working", "working"),
            AgentActivity.Done when !s.Seen => (B(0x61AFEF), "done", "done — not seen yet"),
            AgentActivity.Done => (B(0x98C379), "done", "done (seen)"),
            AgentActivity.Idle => (B(0x7F848E), "idle", "idle"),
            _ => (B(0xA06CFF), "", "running"),
        };
    }
}

/// <summary>
/// ACTIVE SESSIONS: every started terminal across all workspaces, with its detected agent
/// state. Detection is ZeroCommon's <see cref="AgentActivityTracker"/> — the same rules and
/// "seen" bookkeeping the WPF host's SESSIONS panel runs — on a 1.5 s UI-thread timer, as there.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly AgentActivityTracker _agentTracker = new();
    private readonly ClaudeSubagentReader _subagents = new();
    private readonly HermesSubagentReader _hermesSubagents = new();
    private readonly CodexSubagentReader _codexSubagents = new();
    private DispatcherTimer? _agentTimer;
    private string _sessionsSignature = "";

    /// <summary>Running terminals of every workspace, in workspace then tab order.</summary>
    public ObservableCollection<ActiveSessionItem> ActiveSessions { get; } = new();

    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private string _windowTitle = BuildFlavor.AppTitle;

    public bool HasActiveSessions => ActiveSessions.Count > 0;
    public bool HasNoActiveSessions => ActiveSessions.Count == 0;
    public string ActiveSessionsHeader => AttentionCount > 0
        ? $"ACTIVE SESSIONS · {AttentionCount} need you"
        : "ACTIVE SESSIONS";

    partial void OnAttentionCountChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveSessionsHeader));
        // The WPF host's title-bar rollup, so a blocked agent shows from the taskbar too.
        WindowTitle = value > 0 ? $"{BuildFlavor.AppTitle}  ● {value} need attention" : BuildFlavor.AppTitle;
    }

    /// <summary>Start the detection timer (App, once the window exists).</summary>
    public void StartAgentStateMonitor()
    {
        if (_agentTimer is not null) return;
        _agentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _agentTimer.Tick += (_, _) => RefreshActiveSessions();
        _agentTimer.Start();
        WorkspaceSessionsFeed.SetFocusHandler(FocusFromPlugin);
        RefreshActiveSessions();
    }

    /// <summary>
    /// Hand web plugins (<c>sessions.list</c>) the same picture as the sidebar: every workspace,
    /// its started terminals, each one's agent and detected state, and for a Claude tab the
    /// sub-agents its session is running (read incrementally from its transcripts). Every pass, not
    /// only on change — the plugin polls, and a snapshot is a handful of small records.
    /// </summary>
    /// <summary>Which agent each tab runs — not from the title alone (a renamed tab, a claude-sam profile).</summary>
    private readonly AgentKindDetector _agentKinds = new();

    private void PublishSessionsFeed((int G, int T)? active)
    {
        var views = new List<WorkspaceView>(Workspaces.Count);
        for (int g = 0; g < Workspaces.Count; g++)
        {
            var ws = Workspaces[g];
            var sessions = new List<WorkspaceSessionView>();
            for (int t = 0; t < ws.Tabs.Count; t++)
            {
                var tab = ws.Tabs[t];
                if (!tab.IsTerminalStarted) continue;
                var s = _agentTracker.Lookup(g, t);
                var kind = _agentKinds.KindOf(tab.Title, s?.Session,
                    (tab.Session as Agent.Common.Services.ITerminalProcessSource)?.ProcessId, tab.Definition);
                sessions.Add(new WorkspaceSessionView(g, t, tab.Title, kind,
                    WorkspaceSessionsFeed.StateOf(s), active is { } a && a.G == g && a.T == t, s?.NeedsAttention == true,
                    s?.Session switch
                    {
                        { Agent: "claude" } claude => _subagents.Read(claude),
                        { Agent: "hermes" } hermes => _hermesSubagents.Read(hermes),
                        { Agent: "codex" } codex => _codexSubagents.Read(codex),
                        _ => null,
                    }));
            }
            views.Add(new WorkspaceView(g, ws.DisplayName, ws.DirectoryPath, ReferenceEquals(ws, ActiveWorkspace), sessions));
        }
        WorkspaceSessionsFeed.Publish(views);
    }

    /// <summary>A plugin's "go to this terminal" — the same jump as clicking its ACTIVE SESSIONS row.</summary>
    private bool FocusFromPlugin(int group, int tab)
    {
        if (group < 0 || group >= Workspaces.Count || tab < 0 || tab >= Workspaces[group].Tabs.Count) return false;
        var target = Workspaces[group].Tabs[tab];
        Dispatcher.UIThread.Post(() =>
        {
            Page = AppPage.Terminals;   // the plugin is on the Plugins page; the jump shows the terminal
            SelectTab(target);
        });
        return true;
    }

    // ── Attention: popup + taskbar flash / Dock bounce when an agent starts needing you ──

    private readonly AttentionNotifier _attention = new();

    /// <summary>What to announce (Settings → Notifications). Re-read by <see cref="ReloadAttentionSettings"/>.</summary>
    public AttentionSettings AttentionSettings { get; private set; } = AttentionSettingsStore.Load();

    /// <summary>Whether the main window has the focus — set by the view; "active" keeps the default rule quiet.</summary>
    public Func<bool> IsAppActive { get; set; } = () => false;

    /// <summary>Alerts of one detection pass, each with its workspace name. Raised on the UI thread.</summary>
    public event Action<IReadOnlyList<(AttentionAlert Alert, string Workspace)>>? AttentionRaised;

    public void ReloadAttentionSettings() => AttentionSettings = AttentionSettingsStore.Load();

    /// <summary>
    /// A program in <paramref name="tab"/> sent a notification (OSC 9 / 777 / 99). Unlike the
    /// detected states it is an event, so it is announced now, through the same popup and flash
    /// and under the same settings; <see cref="AttentionNotifier.ObserveNotification"/> holds the rules.
    /// </summary>
    public void OnTerminalNotification(TerminalTabViewModel tab, TerminalNotification note)
    {
        for (int g = 0; g < Workspaces.Count; g++)
        {
            var t = Workspaces[g].Tabs.IndexOf(tab);
            if (t < 0) continue;
            var alert = _attention.ObserveNotification(g, t, tab.Title, note, AttentionSettings, IsAppActive(), DateTime.UtcNow);
            if (alert is null) return;
            AppLogger.Log($"[Attention] {Workspaces[g].DisplayName}/{tab.Title}=Notify | {note.Text}");
            AttentionRaised?.Invoke([(alert, Workspaces[g].DisplayName)]);
            return;
        }
    }

    private void RaiseAttention()
    {
        var alerts = _attention.Observe(_agentTracker.Snapshot(), AttentionSettings, IsAppActive(), DateTime.UtcNow);
        if (alerts.Count == 0) return;
        var named = alerts.Select(a => (a, a.Group >= 0 && a.Group < Workspaces.Count ? Workspaces[a.Group].DisplayName : "")).ToList();
        AppLogger.Log($"[Attention] {string.Join(", ", named.Select(n => $"{n.Item2}/{n.a.Title}={n.a.Kind}"))}");
        AttentionRaised?.Invoke(named);
    }

    /// <summary>
    /// Fire a sample alert through the same path (Settings → Notifications → Test), ignoring the
    /// "only when inactive" rule — the user is looking at the settings page when they press it.
    /// </summary>
    public void TestAttention()
    {
        var target = ActiveWorkspace?.ActiveTab;
        var g = ActiveWorkspace is { } w ? Workspaces.IndexOf(w) : -1;
        var alert = new AttentionAlert(g, ActiveWorkspace?.ActiveTabIndex ?? -1, target?.Title ?? "Test terminal", AttentionKind.Blocked);
        AttentionRaised?.Invoke([(alert, ActiveWorkspace?.DisplayName ?? "AgentZero")]);
    }

    /// <summary>The <c>attention status|test</c> CLI reply.</summary>
    public string AttentionCommand(string verb)
    {
        var s = AttentionSettings;
        var settings = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = s.Enabled, ["onBlocked"] = s.OnBlocked, ["onDone"] = s.OnDone, ["onNotify"] = s.OnNotify,
            ["onlyWhenInactive"] = s.OnlyWhenInactive, ["flashWindow"] = s.FlashWindow,
            ["popup"] = s.Popup, ["popupSeconds"] = s.ResolvePopupSeconds(),
        };
        switch (verb)
        {
            case "" or "status":
                return new System.Text.Json.Nodes.JsonObject { ["ok"] = true, ["settings"] = settings, ["attention"] = AttentionCount }.ToJsonString();
            case "test":
                TestAttention();
                return new System.Text.Json.Nodes.JsonObject { ["ok"] = true, ["fired"] = true, ["settings"] = settings }.ToJsonString();
            default:
                return Agent.Common.Platform.CliIpcProtocol.ErrorJson($"unknown attention verb '{verb}' (status | test)");
        }
    }

    /// <summary>
    /// Go to the terminal an alert named. Indexes can shift while the popup is up (a tab closed),
    /// so the title is checked and, if it no longer matches, looked up by title instead.
    /// </summary>
    public void GoToAlert(AttentionAlert alert)
    {
        TerminalTabViewModel? target = null;
        if (alert.Group >= 0 && alert.Group < Workspaces.Count
            && alert.Tab >= 0 && alert.Tab < Workspaces[alert.Group].Tabs.Count
            && Workspaces[alert.Group].Tabs[alert.Tab].Title == alert.Title)
            target = Workspaces[alert.Group].Tabs[alert.Tab];
        target ??= Workspaces.SelectMany(w => w.Tabs).FirstOrDefault(t => t.IsTerminalStarted && t.Title == alert.Title);
        if (target is null) return;
        Page = AppPage.Terminals;
        SelectTab(target);
    }

    public void StopAgentStateMonitor()
    {
        _agentTimer?.Stop();
        _agentTimer = null;
    }

    /// <summary>The <c>agent-state</c> CLI reply — the WPF host's contract.</summary>
    public string AgentStateJson() => _agentTracker.ToCliJson();

    /// <summary>One detection pass + list sync. Cheap when nothing changed.</summary>
    public void RefreshActiveSessions()
    {
        try
        {
            var active = ActiveWorkspace is { } aw && Workspaces.IndexOf(aw) is var gi && gi >= 0 && aw.ActiveTab is not null
                ? (gi, aw.ActiveTabIndex)
                : ((int, int)?)null;
            var stateChanged = _agentTracker.Update(Groups, active);
            RaiseAttention();

            // Which rows exist: started terminals, in workspace then tab order.
            var running = new List<(int G, int T, WorkspaceViewModel W, TerminalTabViewModel Tab)>();
            for (int g = 0; g < Workspaces.Count; g++)
                for (int t = 0; t < Workspaces[g].Tabs.Count; t++)
                    if (Workspaces[g].Tabs[t].IsTerminalStarted)
                        running.Add((g, t, Workspaces[g], Workspaces[g].Tabs[t]));

            PublishSessionsFeed(active);

            var signature = string.Join("|", running.Select(r =>
                $"{r.W.DisplayName}/{r.Tab.Title}/{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Tab)}"))
                + $"#{active}";
            if (!stateChanged && signature == _sessionsSignature) return;
            _sessionsSignature = signature;

            // Rebuild only when the set of rows changed; otherwise update in place (no flicker).
            var sameRows = ActiveSessions.Count == running.Count
                && ActiveSessions.Select(i => i.Tab).SequenceEqual(running.Select(r => r.Tab))
                && ActiveSessions.Select(i => i.Workspace).SequenceEqual(running.Select(r => r.W.DisplayName));
            if (!sameRows)
            {
                ActiveSessions.Clear();
                foreach (var r in running) ActiveSessions.Add(new ActiveSessionItem(r.Tab, r.W.DisplayName, SelectTab));
                OnPropertyChanged(nameof(HasActiveSessions));
                OnPropertyChanged(nameof(HasNoActiveSessions));
            }
            for (int i = 0; i < running.Count; i++)
            {
                var r = running[i];
                var isActive = active is { } a && a.Item1 == r.G && a.Item2 == r.T;
                ActiveSessions[i].Apply(_agentTracker.Lookup(r.G, r.T), isActive);
            }
            AttentionCount = _agentTracker.AttentionCount();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AgentState] refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Bold chip for the states that need the user (blocked, unseen done).</summary>
public sealed class AttentionWeight : Avalonia.Data.Converters.IValueConverter
{
    public static readonly AttentionWeight Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
