using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Agent.Common;
using Agent.Common.Platform;
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

        DataContextChanged += (_, _) =>
        {
            AttachBot(Vm);
            AttachAttention(Vm);
            if (Vm is { } vm)
                vm.ClipboardWriter = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
        BotSplitter.DragCompleted += (_, _) => RememberBotPaneHeight();

        // ShutdownMode.OnMainWindowClose closes the floating bot for us on exit. Its own
        // Closing handler would read that as the user re-docking, cancel the close and
        // persist IsBotDocked = true — so quitting while floating would come back docked.
        Closing += (_, _) =>
        {
            if (_botWindow is not null) _botWindow.ClosingProgrammatically = true;
            if (_codeWindow is not null) _codeWindow.ClosingProgrammatically = true;
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
            _botVm.CodeFloatRequested -= OnCodeFloatRequested;
            _botVm.PropertyChanged -= OnBotVmPropertyChanged;
        }
        _botVm = vm;
        if (_botVm is null) return;

        _botVm.BotFloatRequested += OnBotFloatRequested;
        _botVm.CodeFloatRequested += OnCodeFloatRequested;
        _botVm.PropertyChanged += OnBotVmPropertyChanged;
        ApplyBotDockRows();
        // The stored dock state may already say "floating".
        OnBotFloatRequested(_botVm.BotVisible && !_botVm.BotDocked);
    }

    private CodeWindow? _codeWindow;

    /// <summary>The code view's own window (⧉ on its tab) — the floating bot's arrangement.</summary>
    private void OnCodeFloatRequested(bool floating)
    {
        if (_botVm is null) return;
        if (!floating)
        {
            if (_codeWindow is not null)
            {
                _codeWindow.ClosingProgrammatically = true;
                _codeWindow.Close();
                _codeWindow = null;
            }
            return;
        }
        if (_codeWindow is not null) { _codeWindow.Activate(); return; }
        _codeWindow = new CodeWindow { DataContext = _botVm.Code };
        _codeWindow.EmbedRequested += () => _botVm.EmbedCode();
        _codeWindow.Show(this);
    }

    private void OnBotVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.BotMaximized)
                          or nameof(MainWindowViewModel.BottomDockVisible))
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

        // The bottom panel: the bot's tab, the code view's, or both.
        if (!_botVm.BottomDockVisible)
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

    // ── Attention: the corner popup and the taskbar flash / Dock bounce ──────

    private MainWindowViewModel? _attentionVm;
    private AttentionPopup? _attentionPopup;

    private void AttachAttention(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_attentionVm, vm)) return;
        if (_attentionVm is not null)
        {
            _attentionVm.AttentionRaised -= OnAttentionRaised;
            _attentionVm.PaletteRequested -= OpenPalette;
        }
        _attentionVm = vm;
        if (vm is null) return;
        vm.IsAppActive = () => IsActive && WindowState != WindowState.Minimized;
        vm.AttentionRaised += OnAttentionRaised;
        vm.PaletteRequested += OpenPalette;
    }

    private PaletteWindow? _palette;

    /// <summary>Ctrl+Shift+P (Cmd on macOS): a second press while it is open closes it.</summary>
    private void OpenPalette()
    {
        if (_attentionVm is not { } vm) return;
        if (_palette is not null) { _palette.Close(); return; }
        _palette = new PaletteWindow(new PaletteViewModel(vm.BuildPaletteItems()));
        _palette.Closed += (_, _) => _palette = null;
        _palette.ShowOver(this);
    }

    private void OnAttentionRaised(IReadOnlyList<(Agent.Common.Agents.AttentionAlert Alert, string Workspace)> alerts)
    {
        if (_attentionVm is not { } vm) return;
        var settings = vm.AttentionSettings;
        if (settings.FlashWindow && !IsActive) Services.WindowAttention.Request(this);
        if (!settings.Popup) return;

        if (_attentionPopup is null)
        {
            _attentionPopup = new AttentionPopup();
            _attentionPopup.GoRequested += alert =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                vm.GoToAlert(alert);
            };
            _attentionPopup.Closed += (_, _) => _attentionPopup = null;
        }
        _attentionPopup.Push(alerts.Select(a => new AttentionPopupItem(a.Alert, a.Workspace)), this, settings.ResolvePopupSeconds());
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
            // Sandboxed (Mac App Store): keep the dialog's grant past this run.
            SandboxFolderAccess.Remember(path);
            Vm.AddWorkspace(path);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Workspaces] folder picker failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
