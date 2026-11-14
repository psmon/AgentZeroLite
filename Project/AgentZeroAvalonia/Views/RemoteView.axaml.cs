using AgentZeroAvalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace AgentZeroAvalonia.Views;

public partial class RemoteView : UserControl
{
    public RemoteView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not RemoteViewModel vm) return;
            vm.CopyText = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            };
            vm.OpenUrl = url =>
            {
                try { _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); }
                catch (Exception ex) { Agent.Common.AppLogger.Log($"[Remote] open failed: {ex.Message}"); }
            };
        };
    }
}
