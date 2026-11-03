using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Agents;
using Agent.Common.Data.Entities;
using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.Security;
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
/// never re-parents a renderer, so the native WebView and its scrollback survive. The one
/// move is detaching a tab into its own <see cref="TerminalWindow"/> and docking it back,
/// done in a single dispatcher turn so the native view is handed over, not rebuilt.
///
/// This class also owns the PTY lifecycle: a tab's process starts the first time its
/// pane shows it, keeps running while hidden, and dies when the tab closes.
/// </summary>
public partial class TerminalsView : UserControl
{
    private readonly Dictionary<TerminalTabViewModel, XtermWebViewTerminalControl> _controls = new();
    /// <summary>Password-auth ssh tabs waiting for their prompt; disposed with the tab.</summary>
    private readonly Dictionary<TerminalTabViewModel, SshPasswordWatcher> _sshAutofill = new();
    private readonly Dictionary<Pane, Border> _slots = new();
    private readonly Dictionary<XtermWebViewTerminalControl, Rect> _placed = new();
    /// <summary>Tabs detached into their own window; their renderer lives there, not on the canvas.</summary>
    private readonly Dictionary<TerminalTabViewModel, TerminalWindow> _floating = new();
    private MainWindowViewModel? _vm;
    private WorkspaceViewModel? _builtFor;

    public TerminalsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        NewTerminalMenuButton.Flyout = BuildDefinitionsFlyout(null);
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
            _vm.FindRequested -= OnFindRequested;
            _vm.RestartRequested -= Restart;
            _vm.TerminalAppearanceChanged -= OnAppearanceChanged;
            _vm.HotkeysChanged -= OnHotkeysChanged;
            _vm.FloatRequested -= OnFloatRequested;
        }
        _vm = vm;
        if (_vm is null) return;
        _vm.FloatRequested += OnFloatRequested;
        _vm.ActiveTerminalChanged += OnActiveTerminalChanged;
        _vm.TabClosed += OnTabClosed;
        _vm.WorkspaceRemoved += OnWorkspaceRemoved;
        _vm.FocusPaneRequested += OnFocusPane;
        _vm.FindRequested += OnFindRequested;
        _vm.RestartRequested += Restart;
        _vm.TerminalAppearanceChanged += OnAppearanceChanged;
        _vm.HotkeysChanged += OnHotkeysChanged;
        _vm.CliDefinitions.CollectionChanged += (_, _) => NewTerminalMenuButton.Flyout = BuildDefinitionsFlyout(null);
        NewTerminalMenuButton.Flyout = BuildDefinitionsFlyout(null);
        OnActiveTerminalChanged();
    }

    /// <summary>The find chord or the palette: open the find bar in that tab's renderer.</summary>
    private void OnFindRequested(TerminalTabViewModel tab)
    {
        if (_controls.TryGetValue(tab, out var control)) control.OpenFind();
    }

    /// <summary>Settings saved a new appearance: every open renderer gets the config message (live, no restart).</summary>
    private void OnAppearanceChanged()
    {
        foreach (var control in _controls.Values) control.PostAppearance();
    }

    /// <summary>Settings → Shortcuts saved: every renderer matches the new chords (they travel in the config message).</summary>
    private void OnHotkeysChanged()
    {
        if (_vm is null) return;
        foreach (var control in _controls.Values)
        {
            control.Hotkeys = _vm.Hotkeys;
            control.PostAppearance();
        }
    }

    /// <summary>A menu of the CLI definitions this OS can launch; picking one opens it in <paramref name="pane"/> (null = the active pane).</summary>
    private MenuFlyout BuildDefinitionsFlyout(Pane? pane)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        if (_vm is not null)
        {
            foreach (var def in _vm.CliDefinitions)
            {
                var item = new MenuItem { Header = def.Name };
                var captured = def;
                item.Click += (_, _) => _vm.NewTerminal(captured, pane);
                menu.Items.Add(item);
            }
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No CLI definitions for this OS", IsEnabled = false });
        return menu;
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
        RefreshStatusText();
        if (tab is not null && _controls.TryGetValue(tab, out var active))
        {
            // Selecting a detached tab (its chip, ACTIVE SESSIONS) brings its window forward.
            if (_floating.TryGetValue(tab, out var window) && !window.IsActive) window.Activate();
            Dispatcher.UIThread.Post(active.FocusTerminal, DispatcherPriority.Background);
        }
    }

    /// <summary>The status bar line for the active terminal — cheap, no layout work.</summary>
    private void RefreshStatusText()
    {
        if (_vm is null) return;
        var ws = _vm.ActiveWorkspace;
        var tab = ws?.ActiveTab;
        _vm.StatusText = tab is null || ws is null
            ? "Ready"
            : $"{ws.DisplayName}/{tab.Title}" + (tab.HealthText.Length > 0 ? $" · {tab.HealthText}" : "")
              + (ws.Layout.PaneCount > 1 ? $" · {ws.Layout.PaneCount} panes" : "");
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
        var add = new Button { Classes = { "strip" }, Content = "＋", FontSize = 12, Flyout = BuildDefinitionsFlyout(pane) };
        ToolTip.SetTip(add, "New terminal in this pane (choose the CLI)");
        DockPanel.SetDock(add, Dock.Right);
        stripDock.Children.Add(add);
        // The WPF pane's detach / rejoin button: ⤢ pulls the shown tab into its own window,
        // ⤡ brings it back when the shown tab is already detached.
        if (pane.ActiveTab is { } shownTab)
        {
            var detached = shownTab.IsFloating;
            var floatButton = new Button { Classes = { "strip" }, Content = detached ? "⤡" : "⤢", FontSize = 12 };
            ToolTip.SetTip(floatButton, detached ? "Dock this terminal back into the pane (Ctrl+Alt+D)"
                                                 : "Detach this terminal into its own window (Ctrl+Alt+F)");
            floatButton.Click += (_, _) =>
            {
                if (shownTab.IsFloating) _vm?.DockTab(shownTab);
                else _vm?.FloatTab(shownTab);
            };
            DockPanel.SetDock(floatButton, Dock.Right);
            stripDock.Children.Add(floatButton);
        }
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
        if (pane.ActiveTab is { IsFloating: true } floatingTab) slot.Child = BuildFloatingPlaceholder(floatingTab);
        Grid.SetRow(slot, 1);
        grid.Children.Add(slot);
        _slots[pane] = slot;

        frame.Child = grid;
        return frame;
    }

    /// <summary>What a pane shows while its selected tab is in its own window.</summary>
    private Control BuildFloatingPlaceholder(TerminalTabViewModel tab)
    {
        var show = new Button { Content = "Show window" };
        show.Click += (_, _) => _vm?.SelectTab(tab);
        var dock = new Button { Content = "↩ Dock back" };
        dock.Click += (_, _) => _vm?.DockTab(tab);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(show);
        buttons.Children.Add(dock);
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = $"'{tab.Title}' is in its own window",
            Foreground = this.FindResource("TextDim") as Avalonia.Media.IBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(buttons);
        return panel;
    }

    private Control BuildTabChip(WorkspaceViewModel ws, Pane pane, TerminalTabViewModel tab)
    {
        var chip = new Border { Classes = { "tab" } };
        chip.Classes.Set("selected", ReferenceEquals(pane.ActiveTab, tab));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new Button { Classes = { "tabtitle" }, Content = tab.IsFloating ? "⧉ " + tab.Title : tab.Title };
        if (tab.HealthText.Length > 0) ToolTip.SetTip(title, tab.HealthText);
        else if (tab.IsFloating) ToolTip.SetTip(title, "In its own window — click to bring it forward");
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
        var move = new MenuItem { Header = "Move to next pane", IsEnabled = ws.Layout.PaneCount > 1 && !tab.IsFloating };
        move.Click += (_, _) => { _vm?.SelectTab(tab); _vm?.MoveActiveTabToNextPane(); };
        splitRight.IsEnabled = splitDown.IsEnabled = !tab.IsFloating;
        var floatItem = new MenuItem { Header = tab.IsFloating ? "Dock back" : "Detach to window" };
        floatItem.Click += (_, _) =>
        {
            if (tab.IsFloating) _vm?.DockTab(tab);
            else _vm?.FloatTab(tab);
        };
        var restart = new MenuItem { Header = "Restart" };
        restart.Click += (_, _) => Restart(ws, tab);
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => _vm?.CloseTab(ws, tab);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(splitRight);
        menu.Items.Add(splitDown);
        menu.Items.Add(move);
        menu.Items.Add(floatItem);
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
                if (pane.ActiveTab is null || pane.ActiveTab.IsFloating) continue;
                if (!_controls.TryGetValue(pane.ActiveTab, out var control)) continue;
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
        foreach (var (tab, control) in _controls)
            if (!visible.Contains(control) && control.IsVisible && !_floating.ContainsKey(tab)) control.IsVisible = false;
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

    // ── detach / dock (the WPF host's Float / Dock) ──────────────────────────

    private void OnFloatRequested(WorkspaceViewModel ws, TerminalTabViewModel tab, bool floating)
    {
        if (floating) Detach(ws, tab);
        else Redock(tab);
    }

    /// <summary>
    /// Move the tab's renderer — the same control, not a new one — off the canvas and into a
    /// window of its own. Removing it and adding it to the window happen in one dispatcher
    /// turn, so the native web view is handed to the new window rather than torn down: the
    /// scrollback and the PTY carry on. Measured on Windows: output written before the
    /// detach, while floating and after docking back all read back from one screen.
    /// </summary>
    private void Detach(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (_floating.ContainsKey(tab)) return;
        EnsureStarted(ws, tab);
        if (!_controls.TryGetValue(tab, out var control)) return;

        // Open it the size it had in its pane (plus the strip), or a sensible default.
        var width = 960.0;
        var height = 600.0;
        if (_placed.TryGetValue(control, out var rect) && rect.Width >= 360 && rect.Height >= 160)
        {
            width = rect.Width;
            height = rect.Height + 28;
        }
        _placed.Remove(control);
        SurfaceHost.Children.Remove(control);
        control.Width = double.NaN;
        control.Height = double.NaN;
        control.IsVisible = true;

        var window = new TerminalWindow { Width = width, Height = height };
        window.SetCaption($"{ws.DisplayName}/{tab.Title}");
        window.Hold(control);
        window.DockRequested += () => _vm?.DockTab(tab);
        // Chords pressed while focus is in the window's strip rather than the renderer.
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_vm is null || e.KeyModifiers == KeyModifiers.None) return;
            if (HotkeyTable.Match(_vm.Hotkeys, e) is not { } name) return;
            e.Handled = true;
            _vm.SelectTab(tab);
            _vm.HandleHotkey(name);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        void OnTitle(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TerminalTabViewModel.Title)) window.SetCaption($"{ws.DisplayName}/{tab.Title}");
        }
        tab.PropertyChanged += OnTitle;
        window.Closed += (_, _) => tab.PropertyChanged -= OnTitle;

        _floating[tab] = window;
        tab.IsFloating = true;
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
        AppLogger.Log($"[Layout] detached '{ws.DisplayName}/{tab.Title}' into its own window | floating={_floating.Count}");

        OnActiveTerminalChanged(); // the pane now shows the placeholder (or its next tab)
    }

    /// <summary>Hand the renderer back to the canvas (same turn), close the window, re-lay out.</summary>
    private void Redock(TerminalTabViewModel tab)
    {
        tab.IsFloating = false;
        if (!_floating.Remove(tab, out var window)) return;
        if (window.Release() is { } control) SurfaceHost.Children.Add(control);
        window.ClosingProgrammatically = true;
        window.Close();
        AppLogger.Log($"[Layout] docked '{tab.Title}' back into its pane | floating={_floating.Count}");
        _vm?.SelectTab(tab);
        OnActiveTerminalChanged();
    }

    // ── PTY lifecycle ────────────────────────────────────────────────────────

    private void EnsureStarted(WorkspaceViewModel ws, TerminalTabViewModel tab)
    {
        if (_controls.ContainsKey(tab)) return;

        var control = new XtermWebViewTerminalControl { Hotkeys = _vm?.Hotkeys ?? Array.Empty<HotkeyBinding>() };
        control.TerminalClicked += (_, _) => _vm?.SelectTab(tab);
        control.HotkeyRequested += (_, name) => _vm?.HandleHotkey(name);
        control.RestartRequested += (_, _) => Restart(ws, tab);
        control.NotificationReceived += (_, note) => _vm?.OnTerminalNotification(tab, note);
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

        // Claude / Codex: offer the sessions the agent recorded for this folder before its
        // process starts. The control is already registered, so a second EnsureStarted
        // for this tab while the question is open is a no-op.
        if (!def.IsRemote && AgentCliTools.Match(def) is { } tool && AgentSessionCatalog.Supports(tool)
            && AgentResumeSettingsStore.Load().Asks(tool))
        {
            _ = LaunchAfterResumeChoiceAsync(ws, tab, control, def, spec, tool);
            return;
        }
        Launch(ws, tab, control, def, spec);
    }

    /// <summary>One resume question on screen at a time — restoring several agent tabs at startup queues them.</summary>
    private static readonly SemaphoreSlim ResumeQuestion = new(1, 1);

    private async Task LaunchAfterResumeChoiceAsync(WorkspaceViewModel ws, TerminalTabViewModel tab,
        XtermWebViewTerminalControl control, CliDefinition def, TerminalLaunchSpec spec, AgentCliTool tool)
    {
        bool StillOurs() => _controls.TryGetValue(tab, out var c) && ReferenceEquals(c, control);

        var launch = spec;
        try
        {
            var sessions = await Task.Run(() => AgentSessionCatalog.List(tool, ws.DirectoryPath, spec.Env));
            if (sessions.Count > 0 && StillOurs())
            {
                ResumeChoice? choice = null;
                tab.HealthText = "choose a session";
                await ResumeQuestion.WaitAsync();
                try
                {
                    if (StillOurs() && await DialogOwnerAsync() is { } owner)
                    {
                        var dialog = new ResumeSessionDialog(tool.Name, $"{ws.DisplayName}/{tab.Title} · {ws.DirectoryPath}", sessions);
                        choice = await dialog.ShowDialog<ResumeChoice?>(owner) ?? dialog.Result;
                    }
                }
                finally
                {
                    ResumeQuestion.Release();
                    tab.HealthText = "";
                }

                if (choice?.DontAskAgain == true)
                {
                    var settings = AgentResumeSettingsStore.Load();
                    settings.SetAsks(tool, false);
                    AgentResumeSettingsStore.Save(settings);
                    _vm?.Settings.ReloadResumeSettings();
                    AppLogger.Log($"[Terminals] resume question for {tool.Name} turned off (don't ask again)");
                }
                if (choice?.SessionId is { } id)
                {
                    var resumed = AgentResumeLaunch.WithResume(def, tool, id) is { } d
                        ? TerminalLaunchPlanner.Plan(d, ws.DirectoryPath, AppContext.BaseDirectory, out _)
                        : null;
                    if (resumed is not null)
                    {
                        launch = resumed;
                        AppLogger.Log($"[Terminals] {ws.DisplayName}/{tab.Title} resumes {tool.Name} session {id}");
                    }
                    else AppLogger.Log($"[Terminals] {ws.DisplayName}/{tab.Title}: could not put a resume of {id} into '{def.Arguments}' — starting new");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[Terminals] resume question for {ws.DisplayName}/{tab.Title} failed — starting new", ex);
        }

        if (StillOurs()) Launch(ws, tab, control, def, launch);
    }

    /// <summary>The window a dialog can be modal to — the main window, once it is on screen.</summary>
    private async Task<Window?> DialogOwnerAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window
                    ?? (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;
        if (!owner.IsVisible)
        {
            // Tabs restored at startup start before the main window has opened.
            var opened = new TaskCompletionSource();
            void OnOpened(object? s, EventArgs e) => opened.TrySetResult();
            owner.Opened += OnOpened;
            if (!owner.IsVisible) await opened.Task;
            owner.Opened -= OnOpened;
        }
        return owner;
    }

    private void Launch(WorkspaceViewModel ws, TerminalTabViewModel tab, XtermWebViewTerminalControl control,
        CliDefinition def, TerminalLaunchSpec spec)
    {
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
            // Only Dead is worth a word; Stale is the tracker's normal reading mid-keystroke.
            tab.HealthText = state == TerminalHealthState.Dead ? "Dead" : "";
            if (_vm?.ActiveWorkspace?.ActiveTab == tab) RefreshStatusText();
        });
        TerminalActorBinder.Bind(ws, tab);

        // Password-auth ssh: OpenSSH will not take the password on argv, so the stored
        // one is typed at its prompt (the WPF host's M0021 autofill, now shared).
        if (SshPasswordWatcher.ArmFor(def, session, SshPasswordVault.Unprotect) is { } autofill)
            _sshAutofill[tab] = autofill;

        AppLogger.Log($"[Terminals] started {ws.DisplayName}/{tab.Title} | cwd={spec.Cwd} | {spec.CommandLine}");
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
        if (_sshAutofill.Remove(tab, out var autofill)) autofill.Dispose();
        if (_floating.Remove(tab, out var window))
        {
            window.Release();
            window.ClosingProgrammatically = true;
            window.Close();
        }
        tab.IsFloating = false;
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
