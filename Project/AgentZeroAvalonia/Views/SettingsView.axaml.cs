using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

/// <summary>The settings page (M0038): three sections, file pickers for the CLI definition fields.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        PickExeButton.Click += async (_, _) => await PickAsync("Choose the executable", p => Vm!.SelectedCli!.ExePath = p);
        PickKeyButton.Click += async (_, _) => await PickAsync("Choose the SSH key file", p => Vm!.SelectedCli!.SshKeyPath = p);
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private async Task PickAsync(string title, Action<string> apply)
    {
        if (Vm?.SelectedCli is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) apply(path);
    }
}
