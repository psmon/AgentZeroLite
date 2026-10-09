using AgentZeroAvalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AgentZeroAvalonia.Views;

public partial class WearableView : UserControl
{
    public WearableView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not WearableViewModel vm) return;
            vm.PickFolder = PickFolderAsync;
            vm.LogAppended += () => Dispatcher.UIThread.Post(() => LogBox.CaretIndex = LogBox.Text?.Length ?? 0);
        };
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder the watch may use",
            AllowMultiple = false,
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
