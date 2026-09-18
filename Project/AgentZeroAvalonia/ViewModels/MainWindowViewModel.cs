using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Services;
using AgentZeroAvalonia.Services;
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
/// The shell (M0034) plus the workspace/terminal model (M0035): workspaces, their tabs,
/// which one is active, the CLI definitions this OS can launch, and the persistence
/// that keeps all of it in the same rows the WPF host reads. The view owns the
/// renderers and listens to <see cref="ActiveTerminalChanged"/> / <see cref="TabClosed"/>.
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

    /// <summary>The active tab changed, or the active workspace did — the view shows the matching renderer.</summary>
    public event Action? ActiveTerminalChanged;

    /// <summary>A tab left its workspace — the view kills its child.</summary>
    public event Action<WorkspaceViewModel, TerminalTabViewModel>? TabClosed;

    /// <summary>A workspace was removed — the view tears its tabs down.</summary>
    public event Action<WorkspaceViewModel>? WorkspaceRemoved;

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
    [RelayCommand] private void ToggleBot() => BotVisible = !BotVisible;
    [RelayCommand] private void NewTerminalDefault() => NewTerminal(CliDefinitions.FirstOrDefault());

    // ── state ────────────────────────────────────────────────────────────────

    /// <summary>Definitions from the database (filtered to this OS) and the persisted workspaces + tabs.</summary>
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
                Workspaces.Add(ws);
                TerminalActorBinder.RegisterWorkspace(ws);
                if (ws.Tabs.Count > 0)
                    ws.ActiveTab = ws.Tabs[Math.Clamp(snap.ActiveTabIndex, 0, ws.Tabs.Count - 1)];
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
        return ws;
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceViewModel.ActiveTab)) return;
        if (ReferenceEquals(sender, ActiveWorkspace)) RaiseActiveTerminalChanged();
        Persist();
    }

    // ── tabs ─────────────────────────────────────────────────────────────────

    /// <summary>Open a terminal for <paramref name="def"/> in the active workspace.</summary>
    public void NewTerminal(CliDefinition? def)
    {
        var ws = ActiveWorkspace;
        if (ws is null)
        {
            StatusText = "Add a workspace folder first.";
            return;
        }
        if (def is null)
        {
            StatusText = "No CLI definition available on this OS.";
            return;
        }
        var title = def.Name;
        for (var n = 2; ws.Tabs.Any(t => t.Title == title); n++) title = $"{def.Name}-{n}";

        var tab = CreateTab(ws, title, def.Id, def);
        ws.Tabs.Add(tab);
        ws.ActiveTab = tab;
        Persist();
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
        else RaiseActiveTerminalChanged();
    }

    public void CloseTab(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        var idx = ws.Tabs.IndexOf(tab);
        if (idx < 0) return;
        ws.Tabs.RemoveAt(idx);
        TabClosed?.Invoke(ws, tab);
        if (ReferenceEquals(ws.ActiveTab, tab))
            ws.ActiveTab = ws.Tabs.Count == 0 ? null : ws.Tabs[Math.Min(idx, ws.Tabs.Count - 1)];
        Persist();
    }

    /// <summary>A chord the renderer intercepted; the table and the commands arrive in M0036.</summary>
    public void HandleHotkey(string name)
    {
        AppLogger.Log($"[Hotkey] {name}");
        switch (name)
        {
            case "terminal.new": NewTerminalDefault(); break;
            case "terminal.close":
                if (ActiveWorkspace?.ActiveTab is { } t) CloseTab(ActiveWorkspace, t);
                break;
            case "bot.toggle": ToggleBot(); break;
        }
    }

    private void RaiseActiveTerminalChanged()
    {
        OnPropertyChanged(nameof(HasActiveTab));
        ActiveTerminalChanged?.Invoke();
    }

    private void Persist()
    {
        try { CliWorkspacePersistence.SaveCliGroups(Groups); }
        catch (Exception ex) { AppLogger.LogError("[Workspaces] save failed", ex); }
    }
}
