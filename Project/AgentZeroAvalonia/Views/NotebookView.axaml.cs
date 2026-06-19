using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class NotebookView : UserControl
{
    public NotebookView() => InitializeComponent();

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NotebookViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "워크스페이스 폴더 선택", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.SetRoot(path);
    }
}
