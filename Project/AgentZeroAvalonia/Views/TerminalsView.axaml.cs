using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.Services;
using AgentZeroAvalonia.Terminal;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

using Pane = PaneNode<TerminalTabViewModel>;

/// <summary>
/// The terminals page (M0035/M0036). Two layers: the split tree (a recursive Grid of
/// pane frames, tab strips and GridSplitters, rebuilt from the workspace's
/// <see cref="WorkspaceLayout{T}"/>), and the surface host — one Canvas that holds every
/// tab's <see cref="XtermWebViewTerminalControl"/> and positions the active one of each
/// pane over that pane's content slot. Splitting, moving a tab or switching workspaces
/// never re-parents a renderer, so the native WebView and its scrollback survive.
///
/// This class also owns the PTY lifecycle: a tab's process starts the first time its
/// pane shows it, keeps running while hidden, and dies when the tab closes.
/// </summary>
public partial class TerminalsView : UserControl
{
    private readonly Dictionary<TerminalTabViewModel, XtermWebViewTerminalControl> _controls = new();
    private readonly Dictionary<Pane, Border> _slots = new();
    private readonly Dictionary<XtermWebViewTerminalControl, Rect> _placed = new();
    private MainWindowViewModel? _vm;
    private WorkspaceViewModel? _builtFor;

    public TerminalsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        NewTerminalMenuButton.Click += (_, _) => RebuildNewTerminalMenu();
        SurfaceHost.LayoutUpdated += (_, _) => PlaceSurfaces();
    }

    private void Attach(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null)
        {
            _vm.ActiveTerminalChanged -= OnActiveTerminalChanged;
            _vm.TabClosed -= OnTabClosed;
            _vm.WorkspaceRemoved -= OnWorkspaceRemoved;
            _vm.FocusPaneRequested -= OnFocusPane;
            _vm.RestartRequested -= Restart;
        }
        _vm = vm;
        if (_vm is null) return;
        _vm.ActiveTerminalChanged += OnActiveTerminalChanged;
        _vm.TabClosed += OnTabClosed;
        _vm.WorkspaceRemoved += OnWorkspaceRemoved;
        _vm.FocusPaneRequested += OnFocusPane;
        _vm.RestartRequested += Restart;
        OnActiveTerminalChanged();
    }

    private void RebuildNewTerminalMenu()
    {
        if (NewTerminalMenuButton.Flyout is not MenuFlyout menu) return;
        menu.Items.Clear();
        if (_vm is null) return;
        foreach (var def in _vm.CliDefinitions)
        {
            var item = new MenuItem { Header = def.Name };
            var captured = def;
            item.Click += (_, _) => _vm.NewTerminal(captured);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No CLI definitions for this OS", IsEnabled = false });
    }

    // ── layout → tree ────────────────────────────────────────────────────────

    private void OnActiveTerminalChanged()
    {
        if (_vm is null) return;
        var ws = _vm.ActiveWorkspace;
        RebuildTree(ws);

        if (ws is not null)
            foreach (var pane in ws.Layout.Panes)
                if (pane.ActiveTab is { } shown) EnsureStarted(ws, shown);

        PlaceSurfaces(force: true);

        var tab = ws?.ActiveTab;
        TerminalActorBinder.SetActive(ws, tab);
        if (tab is not null && _controls.TryGetValue(tab, out var active))
        {
            _vm.StatusText = $"{ws!.DisplayName}/{tab.Title}" + (tab.HealthText.Length > 0 ? $" · {tab.HealthText}" : "")
                             + (ws.Layout.PaneCount > 1 ? $" · {ws.Layout.PaneCount} panes" : "");
            Dispatcher.UIThread.Post(active.FocusTerminal, DispatcherPriority.Background);
        }
        else
        {
            _vm.StatusText = "Ready";
        }
    }

    private void RebuildTree(WorkspaceViewModel? ws)
    {
        LayoutRoot.Children.Clear();
        _slots.Clear();
        _builtFor = ws;
        if (ws is null || ws.Tabs.Count == 0) return;
        LayoutRoot.Children.Add(BuildNode(ws, ws.Layout.Root));
    }

    private Control BuildNode(WorkspaceViewModel ws, LayoutNode<TerminalTabViewModel> node)
    {
        if (node is Pane pane) return BuildPane(ws, pane);

        var split = (SplitNode<TerminalTabViewModel>)node;
        var grid = new Grid();
        for (var i = 0; i < split.Children.Count; i++)
        {
            if (i > 0)
            {
                if (split.Vertical) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                else grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var splitter = new GridSplitter
                {
                    ResizeDirection = split.Vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns,
                    Width = split.Vertical ? double.NaN : 4,
                    Height = split.Vertical ? 4 : double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Place(splitter, split.Vertical, i * 2 - 1);
                grid.Children.Add(splitter);
            }
            if (split.Vertical) grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            else grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var child = BuildNode(ws, split.Children[i]);
            Place(child, split.Vertical, i * 2);
            grid.Children.Add(child);
        }
        return grid;
    }

    private static void Place(Control c, bool vertical, int index)
    {
        if (vertical) Grid.SetRow(c, index);
        else Grid.SetColumn(c, index);
    }

    private Control BuildPane(WorkspaceViewModel ws, Pane pane)
    {
        var frame = new Border { Classes = { "pane" } };
        frame.Classes.Set("active", ReferenceEquals(pane, ws.ActivePane));

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        // Tab strip
        var strip = new Border { Classes = { "panestrip" } };
        var stripDock = new DockPanel();
        var add = new Button { Classes = { "strip" }, Content = "＋", FontSize = 12 };
        ToolTip.SetTip(add, "New terminal in this pane");
        add.Click += (_, _) => _vm?.NewTerminal(_vm.CliDefinitions.FirstOrDefault(), pane);
        DockPanel.SetDock(add, Dock.Right);
        stripDock.Children.Add(add);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in pane.Tabs) tabs.Children.Add(BuildTabChip(ws, pane, tab));
        stripDock.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tabs,
        });
        strip.Child = stripDock;
        Grid.SetRow(strip, 0);
        grid.Children.Add(strip);

        // Content slot: the renderer is positioned over this rectangle by PlaceSurfaces.
        var slot = new Border { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1e1e1e")) };
        slot.PointerPressed += (_, _) =>
        {
            if (pane.ActiveTab is { } t) _vm?.SelectTab(t);
        };
        Grid.SetRow(slot, 1);
        grid.Children.Add(slot);
        _slots[pane] = slot;

        frame.Child = grid;
        return frame;
    }

    private Control BuildTabChip(WorkspaceViewModel ws, Pane pane, TerminalTabViewModel tab)
    {
        var chip = new Border { Classes = { "tab" } };
        chip.Classes.Set("selected", ReferenceEquals(pane.ActiveTab, tab));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new Button { Classes = { "tabtitle" }, Content = tab.Title };
        if (tab.HealthText.Length > 0) ToolTip.SetTip(title, tab.HealthText);
        title.Click += (_, _) => _vm?.SelectTab(tab);
        title.ContextFlyout = BuildTabMenu(ws, tab);
        var close = new Button { Classes = { "tabclose" }, Content = "✕" };
        close.Click += (_, _) => _vm?.CloseTab(ws, tab);
        row.Children.Add(title);
        row.Children.Add(close);
        chip.Child = row;
        return chip;
    }

    private MenuFlyout BuildTabMenu(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        var menu = new MenuFlyout();
        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => ShowRenameFlyout(ws, tab);
        var splitRight = new MenuItem { Header = "Split right" };
        splitRight.Click += (_, _) => { _vm?.SelectTab(tab); _vm?.Split(false); };
        var splitDown = new MenuItem { Header = "Split down" };
        splitDown.Click += (_, _) => { _vm?.SelectTab(tab); _vm?.Split(true); };
        var move = new MenuItem { Header = "Move to next pane", IsEnabled = ws.Layout.PaneCount > 1 };
        move.Click += (_, _) => { _vm?.SelectTab(tab); _vm?.MoveActiveTabToNextPane(); };
        var restart = new MenuItem { Header = "Restart" };
        restart.Click += (_, _) => Restart(ws, tab);
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => _vm?.CloseTab(ws, tab);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(splitRight);
        menu.Items.Add(splitDown);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        menu.Items.Add(restart);
        menu.Items.Add(close);
        return menu;
    }

    private void ShowRenameFlyout(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (!_slots.TryGetValue(ws.Layout.PaneOf(tab) ?? ws.Layout.FirstPane, out var anchor)) return;
        var box = new TextBox { Text = tab.Title, Width = 200 };
        var ok = new Button { Content = "Rename", Margin = new Thickness(6, 0, 0, 0) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Top };
        void Commit()
        {
            _vm?.RenameTab(ws, tab, box.Text ?? "");
            flyout.Hide();
        }
        ok.Click += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        flyout.ShowAt(anchor);
        box.Focus();
        box.SelectAll();
    }

    // ── surfaces: renderers over their pane slots ────────────────────────────

    private void PlaceSurfaces(bool force = false)
    {
        if (_vm is null) return;
        var ws = _vm.ActiveWorkspace;
        var visible = new HashSet<XtermWebViewTerminalControl>();
        if (ws is not null && ReferenceEquals(ws, _builtFor))
        {
            foreach (var (pane, slot) in _slots)
            {
                if (pane.ActiveTab is null || !_controls.TryGetValue(pane.ActiveTab, out var control)) continue;
                if (slot.Bounds.Width <= 0 || slot.Bounds.Height <= 0) continue;
                var origin = slot.TranslatePoint(new Point(0, 0), SurfaceHost);
                if (origin is null) continue;
                var rect = new Rect(origin.Value.X, origin.Value.Y, slot.Bounds.Width, slot.Bounds.Height);
                visible.Add(control);
                if (!force && _placed.TryGetValue(control, out var last) && last == rect && control.IsVisible) continue;
                _placed[control] = rect;
                Canvas.SetLeft(control, rect.X);
                Canvas.SetTop(control, rect.Y);
                control.Width = rect.Width;
                control.Height = rect.Height;
                control.IsVisible = true;
            }
        }
        foreach (var control in _controls.Values)
            if (!visible.Contains(control) && control.IsVisible) control.IsVisible = false;
    }

    private IReadOnlyDictionary<Pane, (double X, double Y, double W, double H)> PaneRects()
    {
        var rects = new Dictionary<Pane, (double, double, double, double)>();
        foreach (var (pane, slot) in _slots)
        {
            var origin = slot.TranslatePoint(new Point(0, 0), SurfaceHost);
            if (origin is null) continue;
            rects[pane] = (origin.Value.X, origin.Value.Y, slot.Bounds.Width, slot.Bounds.Height);
        }
        return rects;
    }

    private void OnFocusPane(FocusDirection dir)
    {
        var ws = _vm?.ActiveWorkspace;
        if (ws is null || ws.Layout.PaneCount < 2) return;
        var target = WorkspaceLayout<TerminalTabViewModel>.Neighbour(ws.ActivePane, dir, PaneRects());
        if (target?.ActiveTab is { } tab) _vm!.SelectTab(tab);
    }

    // ── PTY lifecycle ────────────────────────────────────────────────────────

    private void EnsureStarted(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (_controls.ContainsKey(tab)) return;

        var control = new XtermWebViewTerminalControl { Hotkeys = _vm?.Hotkeys ?? Array.Empty<HotkeyBinding>() };
        control.TerminalClicked += (_, _) => _vm?.SelectTab(tab);
        control.HotkeyRequested += (_, name) => _vm?.HandleHotkey(name);
        control.RestartRequested += (_, _) => Restart(ws, tab);
        control.IsVisible = false;
        _controls[tab] = control;
        SurfaceHost.Children.Add(control);

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

    /// <summary>Kill and relaunch one tab with the spec it was started with (banner button, context menu).</summary>
    private void Restart(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        Teardown(ws, tab);
        tab.HealthText = "";
        OnActiveTerminalChanged();
    }

    private void Teardown(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (_controls.Remove(tab, out var control))
        {
            try { control.Shutdown(); } catch { }
            _placed.Remove(control);
            SurfaceHost.Children.Remove(control);
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
