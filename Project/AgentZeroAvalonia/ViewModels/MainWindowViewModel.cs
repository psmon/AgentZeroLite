using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;
using Agent.Common.Wearable;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.Services;
using AgentZeroAvalonia.Terminal;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>Which full-area page is showing — the WPF host's activity-bar pages, minus what is out of scope.</summary>
public enum AppPage
{
    Terminals,
    Settings,
    Wearable,
    Plugins,
}

/// <summary>
/// The shell (M0034) plus the workspace/terminal model (M0035) and the split/tab
/// commands (M0036): workspaces, their tabs and panes, which one is active, the CLI
/// definitions this OS can launch, the hotkey table, and the persistence that keeps all
/// of it in the same rows the WPF host reads. The view owns the renderers and listens
/// to <see cref="ActiveTerminalChanged"/> / <see cref="TabClosed"/>.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private AppPage _page = AppPage.Terminals;
    [ObservableProperty] private bool _sidebarExpanded = true;
    [ObservableProperty] private bool _botVisible;
    [ObservableProperty] private string _botStatus = "Bot: not started";

    /// <summary>
    /// Whether the bot sits in the shell or in its own window (M0041). Persisted in the
    /// shared <c>AppWindowState.IsBotDocked</c> column, so the WPF host comes up the same way.
    /// </summary>
    [ObservableProperty] private bool _botDocked = true;

    /// <summary>
    /// Height of the docked pane, remembered across a maximize/restore. 280 is the WPF
    /// host's <c>_botDockNormalHeight</c>.
    /// </summary>
    [ObservableProperty] private double _botPaneHeight = 280;

    [ObservableProperty] private bool _botMaximized;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private WorkspaceViewModel? _activeWorkspace;

    public string VersionLabel { get; }

    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = new();

    /// <summary>The definitions this OS can launch, in the settings' sort order.</summary>
    public ObservableCollection<CliDefinition> CliDefinitions { get; } = new();

    /// <summary>The chords both the renderer and the window answer to.</summary>
    public IReadOnlyList<HotkeyBinding> Hotkeys { get; private set; }

    /// <summary>Settings → Shortcuts saved: the table was rebuilt; open renderers need it too.</summary>
    public event Action? HotkeysChanged;

    /// <summary>Re-read shortcut-settings.json and rebuild the table (live, no restart).</summary>
    public void ReloadHotkeys()
    {
        ShortcutSettings? shortcuts = null;
        try { shortcuts = ShortcutSettingsStore.Load(); } catch { }
        Hotkeys = HotkeyTable.Build(shortcuts, OperatingSystem.IsMacOS());
        AppLogger.Log($"[Hotkey] table rebuilt | {Hotkeys.Count} chord(s)");
        HotkeysChanged?.Invoke();
    }

    /// <summary>The AgentBot pane (M0037), fed the active terminal and the workspace list.</summary>
    public AgentBotViewModel Bot { get; }

    /// <summary>The settings page (M0038).</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Terminal appearance was saved — the view re-posts the config to every open renderer.</summary>
    public event Action? TerminalAppearanceChanged;

    /// <summary>The active tab, the active workspace, or the split layout changed — the view re-lays the renderers out.</summary>
    public event Action? ActiveTerminalChanged;

    /// <summary>A tab left its workspace — the view kills its child.</summary>
    public event Action<WorkspaceViewModel, TerminalTabViewModel>? TabClosed;

    /// <summary>A workspace was removed — the view tears its tabs down.</summary>
    public event Action<WorkspaceViewModel>? WorkspaceRemoved;

    /// <summary>Move focus to the pane in a direction — needs the view's rectangles.</summary>
    public event Action<FocusDirection>? FocusPaneRequested;

    /// <summary>Open the find bar in the active terminal (Ctrl+Shift+F from outside it, or the palette).</summary>
    public event Action<TerminalTabViewModel>? FindRequested;

    /// <summary>Relaunch one tab's process (context menu / CLI).</summary>
    public event Action<WorkspaceViewModel, TerminalTabViewModel>? RestartRequested;

    /// <summary>Detach a tab into its own window (true) or dock it back (false) — the view owns the windows.</summary>
    public event Action<WorkspaceViewModel, TerminalTabViewModel, bool>? FloatRequested;

    private readonly DispatcherTimer _persistTimer;
    private bool _persistPending;

    public MainWindowViewModel()
    {
        var info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        VersionLabel = "v" + (plus >= 0 ? info[..plus] : info);
        WireCodeTabs();
        Workspaces.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(HasNoWorkspaces));
            OnPropertyChanged(nameof(EmptyStateHint));
        };

        ShortcutSettings? shortcuts = null;
        try { shortcuts = ShortcutSettingsStore.Load(); } catch { }
        Hotkeys = HotkeyTable.Build(shortcuts, OperatingSystem.IsMacOS());

        _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _persistTimer.Tick += (_, _) => FlushPersist();

        Bot = new AgentBotViewModel
        {
            ActiveSession = () => ActiveWorkspace?.ActiveTab?.Session,
            ActiveSessionLabel = () => ActiveWorkspace?.ActiveTab is { } t ? $"{ActiveWorkspace.DisplayName} / {t.Title}" : null,
            Groups = () => Groups,
            ActiveDirectory = () => ActiveWorkspace?.DirectoryPath,
            Post = a => Dispatcher.UIThread.Post(a),
        };
        Bot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AgentBotViewModel.Mode) or nameof(AgentBotViewModel.AiBusy))
                BotStatus = $"Bot: {Bot.ModeLabel}" + (Bot.AiBusy ? " · working" : "");
        };

        // M0041 — the bot follows the active terminal: header, session notice and the
        // AgentEventStream that watches for approval prompts and links.
        ActiveTerminalChanged += RefreshBotSession;

        // Where the bot was last left. The column is shared with the WPF host. Written to the
        // backing field so construction neither saves nor asks for a window that has no
        // listener yet — the view reads BotDocked when it subscribes.
        try { _botDocked = CliWorkspacePersistence.LoadWindowState()?.IsBotDocked ?? true; }
        catch (Exception ex) { AppLogger.LogError("[Bot] dock state load failed", ex); }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        // Only where the host can actually start: the Store MSIX leaves AgentZeroWearable.exe out
        // (build-msix.ps1 -p:IncludeWearableHost=false), and a page whose Start can only fail
        // is a broken feature to a reviewer.
        if (WearableHostLauncher.Supported && new WearableHostLauncher(configuration).ResolveHostPath() is not null)
            Wearable = new WearableViewModel(configuration);

        Plugins = new PluginsViewModel();

        Settings = new SettingsViewModel();
        Settings.CliDefinitionsChanged += ReloadCliDefinitions;
        Settings.AppearanceChanged += () => TerminalAppearanceChanged?.Invoke();
        Settings.Notifications.Changed += ReloadAttentionSettings;
        Settings.Notifications.TestRequested += TestAttention;
        Settings.Shortcuts.Saved += ReloadHotkeys;
    }

    // ── Spend chip (Settings → Budget) ───────────────────────────────────────

    private Avalonia.Threading.DispatcherTimer? _costTimer;

    /// <summary>Re-price the month every minute for the status bar (the collectors add records as agents work).</summary>
    public void StartCostMonitor()
    {
        if (_costTimer is not null) return;
        _costTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _costTimer.Tick += (_, _) => _ = Settings.Budget.RefreshInBackgroundAsync();
        _costTimer.Start();
        _ = Settings.Budget.RefreshInBackgroundAsync();
    }

    [RelayCommand]
    private void OpenBudget()
    {
        Settings.Section = SettingsSection.Budget;
        Page = AppPage.Settings;
    }

    /// <summary>Re-read the definitions this OS can launch (after the settings page changed them).</summary>
    public void ReloadCliDefinitions()
    {
        try
        {
            using var db = new AppDbContext();
            var fresh = db.CliDefinitions.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToList()
                .Where(d => TerminalLaunchPlanner.IsAvailableOnThisOs(d)).ToList();
            CliDefinitions.Clear();
            foreach (var d in fresh) CliDefinitions.Add(d);
            AppLogger.Log($"[Workspaces] CLI definitions reloaded: {CliDefinitions.Count}");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Workspaces] reloading CLI definitions failed", ex);
        }
    }

    public bool IsTerminalsPage => Page == AppPage.Terminals;
    public bool IsSettingsPage => Page == AppPage.Settings;
    public bool IsWearablePage => Page == AppPage.Wearable;
    public bool IsPluginsPage => Page == AppPage.Plugins;

    /// <summary>The Plugins page (the WPF host's WebDev page) — web plugins over the shared JS bridge.</summary>
    public PluginsViewModel Plugins { get; }

    /// <summary>The page exists where the watch's host can run: Windows (WinRT BLE) and a build that ships it (not the Store MSIX).</summary>
    public bool WearableSupported => Wearable is not null;

    /// <summary>The Wearable page; null where the host cannot run.</summary>
    public WearableViewModel? Wearable { get; }
    public bool HasWorkspaces => Workspaces.Count > 0;
    public bool HasNoWorkspaces => Workspaces.Count == 0;
    public bool HasActiveWorkspace => ActiveWorkspace is not null;
    public bool HasActiveTab => ActiveWorkspace?.ActiveTab is not null;

    public string EmptyStateHint => Workspaces.Count == 0
        ? "Add a workspace folder from the sidebar to open terminals in it."
        : "No terminal is open in this workspace yet.";

    /// <summary>The CLI catalog's view of the workspaces (terminal-list, the bot's toolbelt).</summary>
    public IReadOnlyList<ICliGroupInfo> Groups => Workspaces;

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsTerminalsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsWearablePage));
        OnPropertyChanged(nameof(IsPluginsPage));
        if (value == AppPage.Plugins) Plugins.NotifyShown();
    }

    partial void OnActiveWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        RefreshActiveWorkspaceMarks();
        OnPropertyChanged(nameof(HasActiveWorkspace));
        OnPropertyChanged(nameof(EmptyStateHint));
        // The code view shows the active workspace's files (loaded only while it is open).
        if (CodeVisible) Code.FollowWorkspace(newValue);
        RaiseActiveTerminalChanged();
    }

    /// <summary>
    /// Re-mark which top-level row holds the active workspace when that workspace is a worktree
    /// nested under it — after a switch, and after the worktree lists are rebuilt.
    /// </summary>
    internal void RefreshActiveWorkspaceMarks()
    {
        var active = ActiveWorkspace;
        foreach (var ws in Workspaces)
            ws.HoldsActiveWorktree = active is not null && !ReferenceEquals(ws, active)
                                     && ws.Worktrees.Any(e => ReferenceEquals(e.Workspace, active));
    }

    [RelayCommand] private void ShowTerminals() => Page = AppPage.Terminals;
    [RelayCommand] private void ShowSettings() => Page = Page == AppPage.Settings ? AppPage.Terminals : AppPage.Settings;
    [RelayCommand] private void ShowWearable() => Page = Page == AppPage.Wearable ? AppPage.Terminals : AppPage.Wearable;
    [RelayCommand] private void ShowPlugins() => Page = Page == AppPage.Plugins ? AppPage.Terminals : AppPage.Plugins;
    [RelayCommand] private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand] private void ToggleBot()
    {
        BotVisible = !BotVisible;
        if (BotVisible) Bot.AttachActors();
    }

    // ── M0041: docked pane ↔ floating window ─────────────────────────────────

    /// <summary>True when the bot should be drawn inside the shell right now.</summary>
    public bool BotDockVisible => BotVisible && BotDocked;

    /// <summary>Raised with "should the floating window be open"; the view opens/closes it.</summary>
    public event Action<bool>? BotFloatRequested;

    partial void OnBotVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(BotDockVisible));
        if (value) BottomTab = BottomTab.Bot;   // summoning the bot brings its tab to the front
        RaiseBottomPanel();
        BotFloatRequested?.Invoke(value && !BotDocked);
    }

    partial void OnBotDockedChanged(bool value)
    {
        OnPropertyChanged(nameof(BotDockVisible));
        RaiseBottomPanel();
        BotFloatRequested?.Invoke(BotVisible && !value);
        CliWorkspacePersistence.SaveBotDocked(value);
    }

    /// <summary>Ctrl+Shift+` — move the bot between the shell and its own window.</summary>
    [RelayCommand]
    public void ToggleBotEmbedded()
    {
        if (!BotVisible)
        {
            // Nothing on screen to move: summon the bot first, as the WPF host does.
            BotVisible = true;
            Bot.AttachActors();
            return;
        }
        BotDocked = !BotDocked;
        StatusText = BotDocked ? "BOT EMBEDDED" : "BOT FLOATING";
    }

    [RelayCommand] public void EmbedBot() { if (!BotDocked) ToggleBotEmbedded(); }
    [RelayCommand] public void UndockBot() { if (BotDocked) ToggleBotEmbedded(); }

    [RelayCommand]
    public void ToggleBotMaximized() => BotMaximized = !BotMaximized;

    [RelayCommand]
    public void CloseBot()
    {
        BotVisible = false;
        BotMaximized = false;
    }
    [RelayCommand] private void NewTerminalDefault() => NewTerminal(CliDefinitions.FirstOrDefault());
    [RelayCommand] private void SplitRight() => Split(vertical: false);
    [RelayCommand] private void SplitDown() => Split(vertical: true);

    // ── state ────────────────────────────────────────────────────────────────

    /// <summary>Definitions from the database (filtered to this OS) and the persisted workspaces, tabs and layouts.</summary>
    public void LoadState()
    {
        try
        {
            using var db = new AppDbContext();
            foreach (var def in db.CliDefinitions.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToList())
            {
                if (TerminalLaunchPlanner.IsAvailableOnThisOs(def)) CliDefinitions.Add(def);
            }
            AppLogger.Log($"[Workspaces] {CliDefinitions.Count} CLI definition(s) available on this OS");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Workspaces] loading CLI definitions failed", ex);
        }

        try
        {
            foreach (var snap in CliWorkspacePersistence.LoadCliGroups())
            {
                var ws = CreateWorkspace(snap.DirectoryPath, snap.DisplayName);
                foreach (var t in snap.Tabs)
                    ws.Tabs.Add(CreateTab(ws, t.Title, t.CliDefinitionId, FindDefinition(t.CliDefinitionId)));
                ws.RestoreLayout(snap.DockLayoutJson);
                Workspaces.Add(ws);
                TerminalActorBinder.RegisterWorkspace(ws);
                if (ws.Tabs.Count > 0)
                    ws.ActiveTab = ws.Tabs[Math.Clamp(snap.ActiveTabIndex, 0, ws.Tabs.Count - 1)];
                if (ws.Layout.PaneCount > 1)
                    AppLogger.Log($"[Workspaces] '{ws.DisplayName}' split restored | panes={ws.Layout.PaneCount} tabs={ws.Tabs.Count}");
            }
            AppLogger.Log($"[Workspaces] restored {Workspaces.Count} workspace(s), {Workspaces.Sum(w => w.Tabs.Count)} tab(s)");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Workspaces] restoring workspaces failed", ex);
        }

        ActiveWorkspace = Workspaces.FirstOrDefault();
    }

    public CliDefinition? FindDefinition(int id) => CliDefinitions.FirstOrDefault(d => d.Id == id);

    // ── workspaces ───────────────────────────────────────────────────────────

    /// <param name="preferredName">The name to use instead of the folder's (a Codex worktree's folder is the repository's own name).</param>
    public WorkspaceViewModel AddWorkspace(string path, string? preferredName = null)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = Workspaces.FirstOrDefault(w => string.Equals(w.DirectoryPath, full,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null)
        {
            ActiveWorkspace = existing;
            return existing;
        }

        var baseName = string.IsNullOrWhiteSpace(preferredName) ? Path.GetFileName(full) : preferredName.Trim();
        if (string.IsNullOrEmpty(baseName)) baseName = full;
        var name = baseName;
        for (var n = 2; Workspaces.Any(w => w.DisplayName == name); n++) name = $"{baseName}-{n}";

        var ws = CreateWorkspace(full, name);
        Workspaces.Add(ws);
        TerminalActorBinder.RegisterWorkspace(ws);
        ActiveWorkspace = ws;
        Persist();
        AppLogger.Log($"[Workspaces] added '{name}' -> {full}");
        return ws;
    }

    private readonly Agent.Common.Platform.IFolderOpener _folderOpener = Agent.Common.Platform.FolderOpener.Create();

    /// <summary>Sidebar context menu: show a workspace's (or worktree's) folder in Explorer / Finder.</summary>
    public void OpenFolder(string path)
    {
        var error = _folderOpener.Open(path);
        if (error is null)
        {
            StatusText = $"Opened {path}";
            return;
        }
        StatusText = $"Could not open the folder — {error}";
        AppLogger.Log($"[Workspace] open folder failed ({_folderOpener.Platform}) | {error}");
    }

    /// <summary>The window's clipboard; set by MainWindow (the clipboard belongs to a TopLevel).</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    /// <summary>Sidebar context menu: a workspace's or worktree's path onto the clipboard.</summary>
    public async void CopyText(string text)
    {
        if (ClipboardWriter is null) return;
        try
        {
            await ClipboardWriter(text);
            StatusText = $"Copied {text}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not copy — {ex.Message}";
        }
    }

    public void RemoveWorkspace(WorkspaceViewModel ws)
    {
        if (!Workspaces.Remove(ws)) return;
        WorkspaceRemoved?.Invoke(ws);
        TerminalActorBinder.UnregisterWorkspace(ws);
        if (ReferenceEquals(ActiveWorkspace, ws)) ActiveWorkspace = Workspaces.FirstOrDefault();
        Persist();
        AppLogger.Log($"[Workspaces] removed '{ws.DisplayName}'");
    }

    private WorkspaceViewModel CreateWorkspace(string path, string name)
    {
        var ws = new WorkspaceViewModel(path, name)
        {
            ActivateRequested = w => ActiveWorkspace = w,
            RemoveRequested = RemoveWorkspace,
            OpenFolderRequested = OpenFolder,
            CopyTextRequested = CopyText,
        };
        ws.PropertyChanged += OnWorkspacePropertyChanged;
        ws.LayoutChanged += () =>
        {
            if (ReferenceEquals(ws, ActiveWorkspace)) RaiseActiveTerminalChanged();
            Persist();
        };
        return ws;
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceViewModel.ActiveTab)) return;
        if (ReferenceEquals(sender, ActiveWorkspace)) RaiseActiveTerminalChanged();
        Persist();
    }

    // ── tabs ─────────────────────────────────────────────────────────────────

    /// <summary>Open a terminal for <paramref name="def"/> in the active workspace, in <paramref name="pane"/> or the active pane.</summary>
    public TerminalTabViewModel? NewTerminal(CliDefinition? def, PaneNode<TerminalTabViewModel>? pane = null)
    {
        var ws = ActiveWorkspace;
        if (ws is null)
        {
            StatusText = "Add a workspace folder first.";
            return null;
        }
        if (def is null)
        {
            StatusText = "No CLI definition available on this OS.";
            return null;
        }
        var title = def.Name;
        for (var n = 2; ws.Tabs.Any(t => t.Title == title); n++) title = $"{def.Name}-{n}";

        var tab = CreateTab(ws, title, def.Id, def);
        ws.Tabs.Add(tab);
        ws.Layout.AddTab(tab, pane ?? ws.ActivePane);
        ws.ActiveTab = tab;
        Persist();
        return tab;
    }

    private TerminalTabViewModel CreateTab(WorkspaceViewModel ws, string title, int definitionId, CliDefinition? def)
        => new(title, definitionId, def)
        {
            ActivateRequested = SelectTab,
            CloseRequested = t => CloseTab(ws, t),
        };

    public void SelectTab(TerminalTabViewModel tab)
    {
        var ws = Workspaces.FirstOrDefault(w => w.Tabs.Contains(tab));
        if (ws is null) return;
        if (!ReferenceEquals(ActiveWorkspace, ws)) ActiveWorkspace = ws;
        if (!ReferenceEquals(ws.ActiveTab, tab)) ws.ActiveTab = tab;
        else if (ws.Layout.PaneOf(tab) is { } pane && !ReferenceEquals(pane.ActiveTab, tab))
        {
            pane.ActiveTab = tab;
            RaiseActiveTerminalChanged();
        }
        // Clicking the tab that is already active (every click inside its renderer
        // reports one) changes nothing and must not rebuild the pane tree.
    }

    public void CloseTab(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        var idx = ws.Tabs.IndexOf(tab);
        if (idx < 0) return;
        var pane = ws.Layout.PaneOf(tab);
        ws.Tabs.RemoveAt(idx);
        ws.Layout.RemoveTab(tab);
        TabClosed?.Invoke(ws, tab);
        if (ReferenceEquals(ws.ActiveTab, tab))
        {
            // Stay in the same pane if it still has tabs; else fall back to the flat order.
            var next = pane?.ActiveTab ?? (ws.Tabs.Count == 0 ? null : ws.Tabs[Math.Min(idx, ws.Tabs.Count - 1)]);
            ws.ActiveTab = next;
        }
        Persist();
    }

    public void RenameTab(WorkspaceViewModel ws, TerminalTabViewModel tab, string newTitle)
    {
        newTitle = newTitle.Trim();
        if (newTitle.Length == 0 || newTitle == tab.Title) return;
        if (ws.Tabs.Any(t => !ReferenceEquals(t, tab) && t.Title == newTitle))
        {
            StatusText = $"A tab named '{newTitle}' already exists in this workspace.";
            return;
        }
        TerminalActorBinder.Rename(ws, tab, newTitle);
        var old = tab.Title;
        tab.Title = newTitle;
        AppLogger.Log($"[Workspaces] renamed '{ws.DisplayName}/{old}' -> '{newTitle}'");
        Persist();
        // The pane's tab strip is drawn from code with the title of the moment, so it showed the
        // old name until a tab switch rebuilt it (the sidebar, bound to Title, changed at once).
        // Re-lay out now — that also moves the bot's header and ACTIVE SESSIONS to the new name.
        if (ReferenceEquals(ws, ActiveWorkspace)) RaiseActiveTerminalChanged();
        else if (_agentTimer is not null) RefreshActiveSessions();
    }

    public void RestartTab(WorkspaceViewModel ws, TerminalTabViewModel tab) => RestartRequested?.Invoke(ws, tab);

    // ── layout commands (M0036) ──────────────────────────────────────────────

    /// <summary>
    /// The WPF host's split: move the active tab into a fresh pane next to its own. A tab
    /// that is alone in its pane has nothing to leave behind, so that case opens a new
    /// terminal of the same kind in the fresh pane instead of doing nothing.
    /// </summary>
    public void Split(bool vertical)
    {
        var ws = ActiveWorkspace;
        var tab = ws?.ActiveTab;
        if (ws is null || tab is null) return;
        if (RefuseFloating(tab, "split")) return;
        var fresh = ws.Layout.SplitOut(tab, vertical);
        if (fresh is not null)
        {
            AppLogger.Log($"[Layout] split {(vertical ? "down" : "right")} | moved '{tab.Title}' | panes={ws.Layout.PaneCount}");
            return; // Layout.Changed already re-laid out and persisted.
        }
        var pane = ws.Layout.PaneOf(tab) ?? ws.Layout.FirstPane;
        fresh = ws.Layout.SplitEmpty(pane, vertical);
        var def = tab.Definition ?? CliDefinitions.FirstOrDefault();
        var opened = NewTerminal(def, fresh);
        if (opened is null)
        {
            ws.Layout.RemoveTab(tab); // nothing could be opened: collapse the empty pane again
            ws.Layout.AddTab(tab, pane);
        }
        AppLogger.Log($"[Layout] split {(vertical ? "down" : "right")} | new '{opened?.Title}' | panes={ws.Layout.PaneCount}");
    }

    public void CloseActivePane()
    {
        var ws = ActiveWorkspace;
        if (ws is null) return;
        foreach (var tab in ws.ActivePane.Tabs.ToList()) CloseTab(ws, tab);
    }

    public void CycleTab(int delta)
    {
        var ws = ActiveWorkspace;
        if (ws is null) return;
        var pane = ws.ActivePane;
        if (pane.Tabs.Count == 0) return;
        var i = pane.ActiveTab is null ? 0 : pane.Tabs.IndexOf(pane.ActiveTab);
        var next = pane.Tabs[((i + delta) % pane.Tabs.Count + pane.Tabs.Count) % pane.Tabs.Count];
        SelectTab(next);
    }

    public void MoveActiveTabToNextPane()
    {
        var ws = ActiveWorkspace;
        var tab = ws?.ActiveTab;
        if (ws is null || tab is null || ws.Layout.PaneCount < 2) return;
        if (RefuseFloating(tab, "move")) return;
        var target = ws.Layout.NextPane(ws.ActivePane);
        ws.Layout.MoveTab(tab, target);
        SelectTab(tab);
    }

    // ── detach / dock (the WPF host's Float / Dock) ──────────────────────────

    /// <summary>Tabs currently in their own window, across every workspace.</summary>
    public int FloatingCount => Workspaces.Sum(w => w.Tabs.Count(t => t.IsFloating));

    /// <summary>Detach <paramref name="tab"/> into its own window; it keeps running.</summary>
    public void FloatTab(TerminalTabViewModel tab)
    {
        var ws = Workspaces.FirstOrDefault(w => w.Tabs.Contains(tab));
        if (ws is null || tab.IsFloating) return;
        FloatRequested?.Invoke(ws, tab, true);
    }

    /// <summary>Put a detached tab back in its pane.</summary>
    public void DockTab(TerminalTabViewModel tab)
    {
        var ws = Workspaces.FirstOrDefault(w => w.Tabs.Contains(tab));
        if (ws is null || !tab.IsFloating) return;
        FloatRequested?.Invoke(ws, tab, false);
    }

    /// <summary>The active tab if it is docked, for <see cref="WindowCommandIds.Float"/>.</summary>
    public void FloatActive()
    {
        var tab = ActiveWorkspace?.ActiveTab;
        if (tab is null) { StatusText = "No terminal to detach."; return; }
        if (tab.IsFloating) { StatusText = $"'{tab.Title}' is already in its own window."; return; }
        FloatTab(tab);
    }

    /// <summary>
    /// <see cref="WindowCommandIds.Dock"/>: the active tab if it is detached, else the active
    /// workspace's detached tab when there is exactly one (so the chord works from the main window).
    /// </summary>
    public void DockActive()
    {
        var ws = ActiveWorkspace;
        if (ws is null) return;
        var tab = ws.ActiveTab is { IsFloating: true } active ? active
                : ws.Tabs.Count(t => t.IsFloating) == 1 ? ws.Tabs.First(t => t.IsFloating) : null;
        if (tab is null)
        {
            StatusText = ws.Tabs.Any(t => t.IsFloating)
                ? "Several terminals are detached — select the one to dock, or use its ↩ Dock back."
                : "No detached terminal in this workspace.";
            return;
        }
        DockTab(tab);
    }

    /// <summary>Split and move act on the main window's panes; a detached tab is not in one on screen.</summary>
    private bool RefuseFloating(TerminalTabViewModel tab, string verb)
    {
        if (!tab.IsFloating) return false;
        StatusText = $"Cannot {verb} '{tab.Title}' while it is in its own window — dock it back first.";
        return true;
    }

    /// <summary>A chord, from the renderer (<c>hotkey</c> message) or the window.</summary>
    public void HandleHotkey(string name)
    {
        AppLogger.Log($"[Hotkey] {name}");
        switch (name)
        {
            case WindowCommandIds.SplitRight: Split(false); break;
            case WindowCommandIds.SplitDown: Split(true); break;
            case WindowCommandIds.TerminalAdd: NewTerminalDefault(); break;
            case WindowCommandIds.CloseTab:
                if (ActiveWorkspace?.ActiveTab is { } t) CloseTab(ActiveWorkspace, t);
                break;
            case WindowCommandIds.PanelToggle: ToggleBot(); break;
            case WindowCommandIds.Float: FloatActive(); break;
            case WindowCommandIds.Dock: DockActive(); break;
            case HotkeyTable.BotToggle: ToggleBot(); break;
            case HotkeyTable.BotEmbedToggle: ToggleBotEmbedded(); break;
            case HotkeyTable.CodeToggle: ToggleCode(); break;
            case HotkeyTable.PaletteOpen: OpenPalette(); break;
            case HotkeyTable.TerminalFind:
                if (ActiveWorkspace?.ActiveTab is { } findIn) FindRequested?.Invoke(findIn);
                break;
            case HotkeyTable.NextTab: CycleTab(+1); break;
            case HotkeyTable.PrevTab: CycleTab(-1); break;
            case HotkeyTable.MoveTabNextPane: MoveActiveTabToNextPane(); break;
            case HotkeyTable.ClosePane: CloseActivePane(); break;
            case HotkeyTable.FocusLeft: FocusPaneRequested?.Invoke(FocusDirection.Left); break;
            case HotkeyTable.FocusRight: FocusPaneRequested?.Invoke(FocusDirection.Right); break;
            case HotkeyTable.FocusUp: FocusPaneRequested?.Invoke(FocusDirection.Up); break;
            case HotkeyTable.FocusDown: FocusPaneRequested?.Invoke(FocusDirection.Down); break;
        }
    }

    private void RaiseActiveTerminalChanged()
    {
        OnPropertyChanged(nameof(HasActiveTab));
        ActiveTerminalChanged?.Invoke();
        // Move the ACTIVE SESSIONS highlight now rather than on the next detection tick
        // (only once the monitor runs — construction and LoadState come first).
        if (_agentTimer is not null) RefreshActiveSessions();
    }

    /// <summary>Points the bot at whichever terminal is now active — the WPF <c>RefreshSessionInfo</c>.</summary>
    private void RefreshBotSession()
        => Bot.OnActiveSessionChanged(ActiveWorkspace?.DisplayName, ActiveWorkspace?.ActiveTab?.Title);

    // ── persistence (debounced: a split or a drag can change the layout many times a second) ──

    private void Persist()
    {
        _persistPending = true;
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    /// <summary>Write now — on shutdown, and when the timer fires.</summary>
    public void FlushPersist()
    {
        _persistTimer.Stop();
        if (!_persistPending) return;
        _persistPending = false;
        try { CliWorkspacePersistence.SaveCliGroups(Groups); }
        catch (Exception ex) { AppLogger.LogError("[Workspaces] save failed", ex); }
    }
}
