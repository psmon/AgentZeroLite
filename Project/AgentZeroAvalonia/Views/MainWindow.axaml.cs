using Avalonia.Controls;
using Avalonia.Interactivity;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        Title = "AgentZero Lite — Avalonia cross-platform 포트";
    }
}
