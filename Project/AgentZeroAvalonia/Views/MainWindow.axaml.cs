using Avalonia.Controls;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
