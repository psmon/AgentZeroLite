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
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

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
