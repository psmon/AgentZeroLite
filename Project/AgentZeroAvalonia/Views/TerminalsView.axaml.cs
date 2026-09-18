using Avalonia.Controls;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Data.Entities;
using Agent.Common.Services;
using AgentZeroAvalonia.Services;
using AgentZeroAvalonia.Terminal;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The terminals page (M0035): a tab strip over one surface that holds every tab's
/// <see cref="XtermWebViewTerminalControl"/>. The view model decides which tab is
/// active; this class owns the controls — spawning the PTY when a tab is first shown
/// (the WPF host's lazy <c>Loaded</c> start), wiring the session, binding the actors,
/// and killing the child when the tab closes. Inactive tabs keep running, hidden.
/// </summary>
public partial class TerminalsView : UserControl
{
    private readonly Dictionary<TerminalTabViewModel, XtermWebViewTerminalControl> _controls = new();
    private MainWindowViewModel? _vm;

    public TerminalsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        NewTerminalMenuButton.Click += (_, _) => RebuildNewTerminalMenu();
    }

    private void Attach(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null)
        {
            _vm.ActiveTerminalChanged -= OnActiveTerminalChanged;
            _vm.TabClosed -= OnTabClosed;
            _vm.WorkspaceRemoved -= OnWorkspaceRemoved;
        }
        _vm = vm;
        if (_vm is null) return;
        _vm.ActiveTerminalChanged += OnActiveTerminalChanged;
        _vm.TabClosed += OnTabClosed;
        _vm.WorkspaceRemoved += OnWorkspaceRemoved;
        OnActiveTerminalChanged();
    }

    private void RebuildNewTerminalMenu()
    {
        // A name inside a Flyout is not a generated field; reach it through the button.
        if (NewTerminalMenuButton.Flyout is not MenuFlyout NewTerminalMenu) return;
        NewTerminalMenu.Items.Clear();
        if (_vm is null) return;
        foreach (var def in _vm.CliDefinitions)
        {
            var item = new MenuItem { Header = def.Name };
            var captured = def;
            item.Click += (_, _) => _vm.NewTerminal(captured);
            NewTerminalMenu.Items.Add(item);
        }
        if (NewTerminalMenu.Items.Count == 0)
            NewTerminalMenu.Items.Add(new MenuItem { Header = "No CLI definitions for this OS", IsEnabled = false });
    }

    // ── active tab → visible control ─────────────────────────────────────────

    private void OnActiveTerminalChanged()
    {
        if (_vm is null) return;
        var ws = _vm.ActiveWorkspace;
        var tab = ws?.ActiveTab;

        if (ws is not null && tab is not null) EnsureStarted(ws, tab);

        foreach (var (t, control) in _controls)
            control.IsVisible = ReferenceEquals(t, tab);

        TerminalActorBinder.SetActive(ws, tab);
        if (tab is not null && _controls.TryGetValue(tab, out var active))
        {
            _vm.StatusText = $"{ws!.DisplayName}/{tab.Title}" + (tab.HealthText.Length > 0 ? $" · {tab.HealthText}" : "");
            Dispatcher.UIThread.Post(active.FocusTerminal, DispatcherPriority.Background);
        }
        else
        {
            _vm.StatusText = "Ready";
        }
    }

    private void EnsureStarted(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (_controls.ContainsKey(tab)) return;

        var control = new XtermWebViewTerminalControl();
        control.TerminalClicked += (_, _) => _vm?.SelectTab(tab);
        control.HotkeyRequested += (_, name) => _vm?.HandleHotkey(name);
        control.RestartRequested += (_, _) => Restart(ws, tab);
        _controls[tab] = control;
        Surface.Children.Add(control);

        var def = tab.Definition ?? _vm?.FindDefinition(tab.CliDefinitionId);
        if (def is null)
        {
            tab.HealthText = "CLI definition missing";
            AppLogger.Log($"[Terminals] tab '{tab.Title}' has no CLI definition (id={tab.CliDefinitionId})");
            return;
        }
        var spec = TerminalLaunchPlanner.Plan(def, ws.DirectoryPath, AppContext.BaseDirectory, out var error);
        if (spec is null)
        {
            tab.HealthText = error ?? "cannot launch on this OS";
            AppLogger.Log($"[Terminals] cannot plan '{def.Name}': {error}");
            return;
        }
        tab.LaunchSpec = spec;

        var host = control.StartPty(spec);
        tab.IsTerminalStarted = true;
        if (host is null)
        {
            tab.HealthText = "failed to start";
            return;
        }

        var session = new XtermTerminalSession(host, $"{ws.DisplayName}/{tab.Title}");
        tab.Session = session;
        control.Session = session;
        session.HealthChanged += state => Dispatcher.UIThread.Post(() =>
        {
            control.ApplyHealth(state);
            tab.HealthText = state == TerminalHealthState.Alive ? "" : state.ToString();
            if (_vm?.ActiveWorkspace?.ActiveTab == tab) OnActiveTerminalChanged();
        });
        TerminalActorBinder.Bind(ws, tab);
        AppLogger.Log($"[Terminals] started {ws.DisplayName}/{tab.Title} | {spec.CommandLine}");
    }

    /// <summary>Kill and relaunch one tab with the spec it was started with (the banner's button).</summary>
    private void Restart(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        Teardown(ws, tab);
        tab.HealthText = "";
        if (_vm?.ActiveWorkspace?.ActiveTab == tab) OnActiveTerminalChanged();
        else EnsureStarted(ws, tab);
    }

    private void Teardown(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (_controls.Remove(tab, out var control))
        {
            try { control.Shutdown(); } catch { }
            Surface.Children.Remove(control);
        }
        TerminalActorBinder.Destroy(ws, tab);
        try { tab.Session?.Dispose(); } catch { }
        tab.Session = null;
        tab.IsTerminalStarted = false;
    }

    private void OnTabClosed(WorkspaceViewModel ws, TerminalTabViewModel tab) => Teardown(ws, tab);

    private void OnWorkspaceRemoved(WorkspaceViewModel ws)
    {
        foreach (var tab in ws.Tabs.ToList()) Teardown(ws, tab);
    }
}
