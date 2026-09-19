using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Agent.Common;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddWorkspaceButton.Click += async (_, _) => await PickWorkspaceFolderAsync();
        // Tunnelling, so a chord is seen before a focused TextBox or button eats it. The
        // renderer never lets a key reach here while it has focus; term.js reports those
        // through the hotkey message instead, against the same table.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) => AttachBot(Vm);
        BotSplitter.DragCompleted += (_, _) => RememberBotPaneHeight();

        // ShutdownMode.OnMainWindowClose closes the floating bot for us on exit. Its own
        // Closing handler would read that as the user re-docking, cancel the close and
        // persist IsBotDocked = true — so quitting while floating would come back docked.
        Closing += (_, _) =>
        {
            if (_botWindow is not null) _botWindow.ClosingProgrammatically = true;
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    // ── M0041: the bot pane and its floating window ──────────────────────────

    /// <summary>Thickness of the bot dock splitter, matching the WPF host.</summary>
    private const double BotSplitterPx = 6;

    /// <summary>Sliver of terminal kept visible when the dock is maximized (WPF's value).</summary>
    private const double BotMaximizedTopPx = 90;

    // A RowDefinition x:Name generates no field in Avalonia, so the rows are reached
    // through the named grid.
    private RowDefinition TopRow => ShellGrid.RowDefinitions[0];
    private RowDefinition BotSplitterRow => ShellGrid.RowDefinitions[1];
    private RowDefinition BotRow => ShellGrid.RowDefinitions[2];

    private MainWindowViewModel? _botVm;
    private AgentBotWindow? _botWindow;

    private void AttachBot(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_botVm, vm)) return;
        if (_botVm is not null)
        {
            _botVm.BotFloatRequested -= OnBotFloatRequested;
            _botVm.PropertyChanged -= OnBotVmPropertyChanged;
        }
        _botVm = vm;
        if (_botVm is null) return;

        _botVm.BotFloatRequested += OnBotFloatRequested;
        _botVm.PropertyChanged += OnBotVmPropertyChanged;
        ApplyBotDockRows();
        // The stored dock state may already say "floating".
        OnBotFloatRequested(_botVm.BotVisible && !_botVm.BotDocked);
    }

    private void OnBotVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.BotMaximized)
                          or nameof(MainWindowViewModel.BotDockVisible))
            ApplyBotDockRows();
    }

    /// <summary>
    /// Writes the dragged height back once, on release. Every layout pass re-places the
    /// terminal renderers over their panes, so tracking the drag live would resize the
    /// native web view on every frame.
    /// </summary>
    private void RememberBotPaneHeight()
    {
        if (_botVm is null || _botVm.BotMaximized) return;
        if (BotRow.Height.IsAbsolute) _botVm.BotPaneHeight = BotRow.Height.Value;
    }

    /// <summary>
    /// Sizes the three shell rows. A hidden dock collapses to zero rather than leaving a gap;
    /// a maximized one keeps a sliver of terminal on top, as the WPF host does.
    /// </summary>
    private void ApplyBotDockRows()
    {
        if (_botVm is null) return;

        if (!_botVm.BotDockVisible)
        {
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BotSplitterRow.Height = new GridLength(0);
            BotRow.Height = new GridLength(0);
            return;
        }

        BotSplitterRow.Height = new GridLength(BotSplitterPx);
        if (_botVm.BotMaximized)
        {
            TopRow.Height = new GridLength(BotMaximizedTopPx);
            BotRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BotRow.Height = new GridLength(_botVm.BotPaneHeight);
        }
    }

    private void OnBotFloatRequested(bool floating)
    {
        if (_botVm is null) return;

        if (!floating)
        {
            if (_botWindow is not null)
            {
                _botWindow.ClosingProgrammatically = true;
                _botWindow.Close();
                _botWindow = null;
            }
            return;
        }

        if (_botWindow is not null) { _botWindow.Activate(); return; }

        _botWindow = new AgentBotWindow { DataContext = _botVm.Bot };
        _botWindow.EmbedRequested += () => _botVm.EmbedBot();
        _botWindow.Show(this);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null || e.KeyModifiers == KeyModifiers.None) return;
        var name = HotkeyTable.Match(Vm.Hotkeys, e);
        if (name is null) return;
        e.Handled = true;
        Vm.HandleHotkey(name);
    }

    /// <summary>The OS folder picker -> a new workspace (the WPF host's "+ Add folder").</summary>
    private async Task PickWorkspaceFolderAsync()
    {
        if (Vm is null) return;
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add workspace folder",
                AllowMultiple = false,
            });
            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            Vm.AddWorkspace(path);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Workspaces] folder picker failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
