using Avalonia.Controls;
using Avalonia.Interactivity;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class AgentChatView : UserControl
{
    private readonly AgentChatViewModel _vm = new();

    public AgentChatView()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        await _vm.InitializeAsync();
    }
}
