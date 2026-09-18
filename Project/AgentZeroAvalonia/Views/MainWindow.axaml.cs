using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Agent.Common;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddWorkspaceButton.Click += async (_, _) => await PickWorkspaceFolderAsync();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>The OS folder picker → a new workspace (the WPF host's "+ Add folder").</summary>
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
