using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;
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
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private WorkspaceViewModel? _activeWorkspace;

    public string VersionLabel { get; }

    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = new();

    /// <summary>The definitions this OS can launch, in the settings' sort order.</summary>
    public ObservableCollection<CliDefinition> CliDefinitions { get; } = new();

    /// <summary>The chords both the renderer and the window answer to.</summary>
    public IReadOnlyList<HotkeyBinding> Hotkeys { get; }

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

    /// <summary>Relaunch one tab's process (context menu / CLI).</summary>
    public event Action<WorkspaceViewModel, TerminalTabViewModel>? RestartRequested;

    private readonly DispatcherTimer _persistTimer;
    private bool _persistPending;

    public MainWindowViewModel()
    {
        var info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        VersionLabel = "v" + (plus >= 0 ? info[..plus] : info);
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

        Settings = new SettingsViewModel();
        Settings.CliDefinitionsChanged += ReloadCliDefinitions;
        Settings.AppearanceChanged += () => TerminalAppearanceChanged?.Invoke();
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
    }

    partial void OnActiveWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        OnPropertyChanged(nameof(HasActiveWorkspace));
        OnPropertyChanged(nameof(EmptyStateHint));
        RaiseActiveTerminalChanged();
    }

    [RelayCommand] private void ShowTerminals() => Page = AppPage.Terminals;
    [RelayCommand] private void ShowSettings() => Page = Page == AppPage.Settings ? AppPage.Terminals : AppPage.Settings;
    [RelayCommand] private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand] private void ToggleBot()
    {
        BotVisible = !BotVisible;
        if (BotVisible) Bot.AttachActors();
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

    public WorkspaceViewModel AddWorkspace(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = Workspaces.FirstOrDefault(w => string.Equals(w.DirectoryPath, full,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null)
        {
            ActiveWorkspace = existing;
            return existing;
        }

        var baseName = Path.GetFileName(full);
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
        var target = ws.Layout.NextPane(ws.ActivePane);
        ws.Layout.MoveTab(tab, target);
        SelectTab(tab);
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
            case HotkeyTable.BotToggle: ToggleBot(); break;
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
    }

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
